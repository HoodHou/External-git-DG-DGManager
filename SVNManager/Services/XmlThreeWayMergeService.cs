using System.Text;
using System.Xml.Linq;

namespace SVNManager;

internal enum XmlMergeChangeKind
{
    AutoRemote,
    LocalOnly,
    SameBoth,
    Conflict,
}

internal enum XmlMergeResolution
{
    KeepTarget,
    UseRemote,
}

internal enum XmlMergeActionKind
{
    SetAttribute,
    RemoveAttribute,
    SetText,
    AddElement,
    DeleteElement,
}

internal sealed class XmlMergePlan
{
    public XmlMergePlan(
        IReadOnlyList<XmlMergeChange> autoRemoteChanges,
        IReadOnlyList<XmlMergeChange> localOnlyChanges,
        IReadOnlyList<XmlMergeChange> sameBothChanges,
        IReadOnlyList<XmlMergeChange> conflicts)
    {
        AutoRemoteChanges = autoRemoteChanges;
        LocalOnlyChanges = localOnlyChanges;
        SameBothChanges = sameBothChanges;
        Conflicts = conflicts;
    }

    public IReadOnlyList<XmlMergeChange> AutoRemoteChanges { get; }
    public IReadOnlyList<XmlMergeChange> LocalOnlyChanges { get; }
    public IReadOnlyList<XmlMergeChange> SameBothChanges { get; }
    public IReadOnlyList<XmlMergeChange> Conflicts { get; }
    public IReadOnlyList<XmlMergeChange> AllChanges => AutoRemoteChanges
        .Concat(LocalOnlyChanges)
        .Concat(SameBothChanges)
        .Concat(Conflicts)
        .ToList();
    public int RelevantChangeCount => AutoRemoteChanges.Count + LocalOnlyChanges.Count + SameBothChanges.Count + Conflicts.Count;

    public IReadOnlyList<XmlMergeAction> BuildActions()
    {
        return AllChanges
            .Where(change => change.Resolution == XmlMergeResolution.UseRemote)
            .Where(change => change.RequiresWrite)
            .Select(change => change.ToAction())
            .ToList();
    }
}

internal sealed class XmlMergeChange
{
    public XmlMergeChange(
        XmlMergeChangeKind kind,
        XmlMergeActionKind actionKind,
        string path,
        string parentPath,
        string previousSiblingPath,
        string nextSiblingPath,
        string displayName,
        string baseValue,
        string localValue,
        string remoteValue,
        bool localExists,
        bool remoteExists,
        string remoteXml)
    {
        Kind = kind;
        ActionKind = actionKind;
        Path = path;
        ParentPath = parentPath;
        PreviousSiblingPath = previousSiblingPath;
        NextSiblingPath = nextSiblingPath;
        DisplayName = displayName;
        BaseValue = baseValue;
        LocalValue = localValue;
        RemoteValue = remoteValue;
        LocalExists = localExists;
        RemoteExists = remoteExists;
        RemoteXml = remoteXml;
        Resolution = kind == XmlMergeChangeKind.AutoRemote
            ? XmlMergeResolution.UseRemote
            : XmlMergeResolution.KeepTarget;
    }

    public XmlMergeChangeKind Kind { get; }
    public XmlMergeActionKind ActionKind { get; }
    public string Path { get; }
    public string ParentPath { get; }
    public string PreviousSiblingPath { get; }
    public string NextSiblingPath { get; }
    public string DisplayName { get; }
    public string BaseValue { get; }
    public string LocalValue { get; }
    public string RemoteValue { get; }
    public bool LocalExists { get; }
    public bool RemoteExists { get; }
    public string RemoteXml { get; }
    public XmlMergeResolution Resolution { get; set; }
    public bool RequiresWrite => ActionKind switch
    {
        XmlMergeActionKind.AddElement => RemoteExists && !string.Equals(LocalValue, RemoteValue, StringComparison.Ordinal),
        XmlMergeActionKind.DeleteElement => LocalExists && !RemoteExists,
        XmlMergeActionKind.RemoveAttribute => LocalExists,
        _ => !string.Equals(LocalValue, RemoteValue, StringComparison.Ordinal),
    };

    public XmlMergeAction ToAction()
    {
        return new XmlMergeAction(ActionKind, Path, ParentPath, PreviousSiblingPath, NextSiblingPath, RemoteValue, RemoteXml);
    }
}

internal sealed record XmlMergeAction(
    XmlMergeActionKind Kind,
    string Path,
    string ParentPath,
    string PreviousSiblingPath,
    string NextSiblingPath,
    string Value,
    string RemoteXml);

internal static class XmlThreeWayMergeService
{
    private static readonly string[] KeyNames =
    [
        "id",
        "key",
        "code",
        "name",
        "type",
        "guid",
        "uid",
        "skillid",
        "itemid",
        "leaderid",
    ];

    public static bool IsSupportedPath(string filePath)
    {
        if (!string.Equals(Path.GetExtension(SvnConflictArtifact.NormalizeToBasePath(filePath)), ".xml", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (SpreadsheetXmlFormat.IsSpreadsheetXmlFile(filePath))
        {
            return false;
        }

        try
        {
            XDocument.Load(filePath, LoadOptions.None);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static XmlMergePlan BuildPlan(string baseFilePath, string localFilePath, string remoteFilePath)
    {
        SpreadsheetThreeWayMergeService.ValidateMergeInputs(baseFilePath, localFilePath, remoteFilePath);
        var baseDocument = ReadDocument(baseFilePath);
        var localDocument = ReadDocument(localFilePath);
        var remoteDocument = ReadDocument(remoteFilePath);

        ValidateCompatibleRoots(baseDocument, localDocument, remoteDocument);

        var structuralChanges = BuildStructuralChanges(baseDocument, localDocument, remoteDocument);
        var structuralPaths = structuralChanges.Select(change => change.Path).ToHashSet(StringComparer.Ordinal);
        var fieldChanges = BuildFieldChanges(baseDocument, localDocument, remoteDocument, structuralPaths);
        return CreatePlan(structuralChanges.Concat(fieldChanges).ToList());
    }

    public static void ApplyActions(string localFilePath, IReadOnlyList<XmlMergeAction> actions)
    {
        if (actions.Count == 0)
        {
            return;
        }

        var document = XDocument.Load(localFilePath, LoadOptions.PreserveWhitespace);
        if (document.Root == null)
        {
            throw new InvalidOperationException("XML 文件缺少根节点。");
        }

        foreach (var action in actions
            .Where(action => action.Kind == XmlMergeActionKind.DeleteElement)
            .OrderByDescending(action => Depth(action.Path)))
        {
            FindElement(document, action.Path)?.Remove();
        }

        foreach (var action in actions
            .Where(action => action.Kind == XmlMergeActionKind.AddElement)
            .OrderBy(action => Depth(action.Path)))
        {
            AddElement(document, action);
        }

        foreach (var action in actions.Where(action => action.Kind is XmlMergeActionKind.SetAttribute or XmlMergeActionKind.RemoveAttribute or XmlMergeActionKind.SetText))
        {
            ApplyFieldAction(document, action);
        }

        document.Save(localFilePath, SaveOptions.DisableFormatting);
    }

    private static XmlMergeDocument ReadDocument(string filePath)
    {
        var document = XDocument.Load(filePath, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidOperationException("XML 文件缺少根节点。");
        var elements = new Dictionary<string, XmlElementSnapshot>(StringComparer.Ordinal);
        var fields = new Dictionary<string, XmlFieldSnapshot>(StringComparer.Ordinal);
        BuildElementIndex(root, "", "", "", elements, fields);
        return new XmlMergeDocument(document, elements, fields);
    }

    private static void ValidateCompatibleRoots(params XmlMergeDocument[] documents)
    {
        var rootNames = documents
            .Select(document => document.Document.Root?.Name.LocalName ?? "")
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (rootNames.Count != 1)
        {
            throw new InvalidOperationException("三份 XML 的根节点不同，无法安全执行结构化三方合并。");
        }
    }

    private static void BuildElementIndex(
        XElement element,
        string parentPath,
        string previousSiblingPath,
        string forcedSegment,
        Dictionary<string, XmlElementSnapshot> elements,
        Dictionary<string, XmlFieldSnapshot> fields)
    {
        var segment = parentPath.Length == 0 ? "/" + ElementDisplayName(element) : forcedSegment;
        var path = parentPath.Length == 0 ? segment : parentPath + "/" + segment;
        var children = element.Elements().ToList();
        var childSegments = BuildChildSegments(children);
        var childPaths = children.Select(child => path + "/" + childSegments[child]).ToList();
        var nextSiblingPath = "";
        elements[path] = new XmlElementSnapshot(
            path,
            parentPath,
            previousSiblingPath,
            nextSiblingPath,
            element,
            CanonicalElement(element));

        foreach (var attribute in element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration))
        {
            var attributeName = AttributeDisplayName(attribute);
            var key = path + "/@" + attributeName;
            fields[key] = new XmlFieldSnapshot(key, path, XmlMergeActionKind.SetAttribute, attributeName, NormalizeValue(attribute.Value), true);
        }

        if (!children.Any())
        {
            var key = path + "/text()";
            fields[key] = new XmlFieldSnapshot(key, path, XmlMergeActionKind.SetText, "text()", NormalizeValue(element.Value), true);
        }

        for (var index = 0; index < children.Count; index++)
        {
            var previousPath = index > 0 ? childPaths[index - 1] : "";
            BuildElementIndex(children[index], path, previousPath, childSegments[children[index]], elements, fields);
        }

        for (var index = 0; index < children.Count - 1; index++)
        {
            if (elements.TryGetValue(childPaths[index], out var snapshot))
            {
                elements[childPaths[index]] = snapshot with { NextSiblingPath = childPaths[index + 1] };
            }
        }
    }

    private static Dictionary<XElement, string> BuildChildSegments(IReadOnlyList<XElement> children)
    {
        var result = new Dictionary<XElement, string>();
        foreach (var group in children.GroupBy(ElementDisplayName, StringComparer.Ordinal))
        {
            var siblings = group.ToList();
            var keySelector = ChooseUniqueKeySelector(siblings);
            for (var index = 0; index < siblings.Count; index++)
            {
                var child = siblings[index];
                if (keySelector != null)
                {
                    var key = keySelector(child);
                    if (!string.IsNullOrWhiteSpace(key.Value))
                    {
                        result[child] = $"{group.Key}[{key.Label}='{EscapePathValue(key.Value)}']";
                        continue;
                    }
                }

                result[child] = $"{group.Key}[{index + 1}]";
            }
        }

        return result;
    }

    private static Func<XElement, XmlElementKey>? ChooseUniqueKeySelector(IReadOnlyList<XElement> siblings)
    {
        if (siblings.Count == 0)
        {
            return null;
        }

        var selectors = BuildKeySelectors(siblings);
        foreach (var selector in selectors)
        {
            var values = siblings
                .Select(selector)
                .Select(key => key.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
            if (values.Count == siblings.Count && values.Distinct(StringComparer.Ordinal).Count() == values.Count)
            {
                return selector;
            }
        }

        return null;
    }

    private static IReadOnlyList<Func<XElement, XmlElementKey>> BuildKeySelectors(IReadOnlyList<XElement> siblings)
    {
        var attributeNames = siblings
            .SelectMany(element => element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration))
            .Select(attribute => attribute.Name.LocalName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(KeyPriority)
            .ThenBy(name => name, StringComparer.Ordinal)
            .ToList();
        var childNames = siblings
            .SelectMany(element => element.Elements().Where(child => !child.Elements().Any()))
            .Select(element => element.Name.LocalName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(KeyPriority)
            .ThenBy(name => name, StringComparer.Ordinal)
            .ToList();
        var selectors = new List<Func<XElement, XmlElementKey>>();
        selectors.AddRange(attributeNames.Select<string, Func<XElement, XmlElementKey>>(name => element =>
            new XmlElementKey("@" + name, NormalizeValue(element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == name)?.Value ?? ""))));
        selectors.AddRange(childNames.Select<string, Func<XElement, XmlElementKey>>(name => element =>
            new XmlElementKey(name, NormalizeValue(element.Elements().FirstOrDefault(child => child.Name.LocalName == name)?.Value ?? ""))));
        return selectors;
    }

    private static int KeyPriority(string name)
    {
        var normalized = name.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        var exact = Array.IndexOf(KeyNames, normalized);
        if (exact >= 0)
        {
            return exact;
        }

        return normalized.EndsWith("id", StringComparison.Ordinal) ? KeyNames.Length : KeyNames.Length + 10;
    }

    private static IReadOnlyList<XmlMergeChange> BuildStructuralChanges(
        XmlMergeDocument baseDocument,
        XmlMergeDocument localDocument,
        XmlMergeDocument remoteDocument)
    {
        var changes = new List<XmlMergeChange>();
        var structuralPaths = new HashSet<string>(StringComparer.Ordinal);
        var paths = baseDocument.Elements.Keys
            .Union(localDocument.Elements.Keys, StringComparer.Ordinal)
            .Union(remoteDocument.Elements.Keys, StringComparer.Ordinal)
            .OrderBy(Depth)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToList();

        foreach (var path in paths)
        {
            if (Depth(path) == 1 || HasChangedAncestor(path, structuralPaths))
            {
                continue;
            }

            var baseExists = baseDocument.Elements.TryGetValue(path, out var baseElement);
            var localExists = localDocument.Elements.TryGetValue(path, out var localElement);
            var remoteExists = remoteDocument.Elements.TryGetValue(path, out var remoteElement);

            if (baseExists == remoteExists && baseExists == localExists)
            {
                continue;
            }

            var localChanged = !SameElementState(baseElement, localElement, baseExists, localExists);
            var remoteChanged = !SameElementState(baseElement, remoteElement, baseExists, remoteExists);
            if (!localChanged && !remoteChanged)
            {
                continue;
            }

            var kind = ClassifyChange(localChanged, remoteChanged, localElement?.Canonical ?? "", remoteElement?.Canonical ?? "");
            var actionKind = remoteExists ? XmlMergeActionKind.AddElement : XmlMergeActionKind.DeleteElement;
            var source = remoteElement ?? baseElement ?? localElement;
            var change = new XmlMergeChange(
                kind,
                actionKind,
                path,
                source?.ParentPath ?? ParentPath(path),
                remoteElement?.PreviousSiblingPath ?? "",
                remoteElement?.NextSiblingPath ?? "",
                ElementDisplayPath(path),
                baseExists ? baseElement?.Canonical ?? "" : "",
                localExists ? localElement?.Canonical ?? "" : "",
                remoteExists ? remoteElement?.Canonical ?? "" : "",
                localExists,
                remoteExists,
                remoteExists ? remoteElement?.Element.ToString(SaveOptions.DisableFormatting) ?? "" : "");
            changes.Add(change);
            structuralPaths.Add(path);
        }

        return changes;
    }

    private static IReadOnlyList<XmlMergeChange> BuildFieldChanges(
        XmlMergeDocument baseDocument,
        XmlMergeDocument localDocument,
        XmlMergeDocument remoteDocument,
        HashSet<string> structuralPaths)
    {
        var changes = new List<XmlMergeChange>();
        var fields = baseDocument.Fields.Keys
            .Union(localDocument.Fields.Keys, StringComparer.Ordinal)
            .Union(remoteDocument.Fields.Keys, StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        foreach (var key in fields)
        {
            var elementPath = FieldElementPath(key);
            if (structuralPaths.Contains(elementPath) || HasChangedAncestor(elementPath, structuralPaths))
            {
                continue;
            }

            var baseExists = baseDocument.Fields.TryGetValue(key, out var baseField);
            var localExists = localDocument.Fields.TryGetValue(key, out var localField);
            var remoteExists = remoteDocument.Fields.TryGetValue(key, out var remoteField);
            var localChanged = !SameFieldState(baseField, localField, baseExists, localExists);
            var remoteChanged = !SameFieldState(baseField, remoteField, baseExists, remoteExists);
            if (!localChanged && !remoteChanged)
            {
                continue;
            }

            var localValue = localExists ? localField?.Value ?? "" : "";
            var remoteValue = remoteExists ? remoteField?.Value ?? "" : "";
            var actionKind = remoteField?.Kind ?? localField?.Kind ?? baseField?.Kind ?? XmlMergeActionKind.SetText;
            if (actionKind == XmlMergeActionKind.SetAttribute && !remoteExists)
            {
                actionKind = XmlMergeActionKind.RemoveAttribute;
            }

            changes.Add(new XmlMergeChange(
                ClassifyChange(localChanged, remoteChanged, localValue, remoteValue),
                actionKind,
                key,
                elementPath,
                "",
                "",
                (remoteField ?? localField ?? baseField)?.Name ?? Path.GetFileName(key),
                baseExists ? baseField?.Value ?? "" : "",
                localValue,
                remoteValue,
                localExists,
                remoteExists,
                ""));
        }

        return changes;
    }

    private static XmlMergePlan CreatePlan(IReadOnlyList<XmlMergeChange> changes)
    {
        return new XmlMergePlan(
            changes.Where(change => change.Kind == XmlMergeChangeKind.AutoRemote).ToList(),
            changes.Where(change => change.Kind == XmlMergeChangeKind.LocalOnly).ToList(),
            changes.Where(change => change.Kind == XmlMergeChangeKind.SameBoth).ToList(),
            changes.Where(change => change.Kind == XmlMergeChangeKind.Conflict).ToList());
    }

    private static XmlMergeChangeKind ClassifyChange(bool localChanged, bool remoteChanged, string localValue, string remoteValue)
    {
        if (remoteChanged && !localChanged)
        {
            return XmlMergeChangeKind.AutoRemote;
        }

        if (localChanged && !remoteChanged)
        {
            return XmlMergeChangeKind.LocalOnly;
        }

        return string.Equals(localValue, remoteValue, StringComparison.Ordinal)
            ? XmlMergeChangeKind.SameBoth
            : XmlMergeChangeKind.Conflict;
    }

    private static void ApplyFieldAction(XDocument document, XmlMergeAction action)
    {
        var element = FindElement(document, action.ParentPath);
        if (element == null)
        {
            return;
        }

        if (action.Kind == XmlMergeActionKind.SetText)
        {
            element.Value = action.Value;
            return;
        }

        var attributeName = action.Path.Split('/').Last()[1..];
        var attribute = element.Attributes().FirstOrDefault(item => AttributeDisplayName(item) == attributeName);
        if (action.Kind == XmlMergeActionKind.RemoveAttribute)
        {
            attribute?.Remove();
            return;
        }

        if (attribute != null)
        {
            attribute.Value = action.Value;
            return;
        }

        element.SetAttributeValue(attributeName, action.Value);
    }

    private static void AddElement(XDocument document, XmlMergeAction action)
    {
        if (string.IsNullOrWhiteSpace(action.RemoteXml))
        {
            return;
        }

        var existing = FindElement(document, action.Path);
        var element = XElement.Parse(action.RemoteXml, LoadOptions.PreserveWhitespace);
        if (existing != null)
        {
            existing.ReplaceWith(element);
            return;
        }

        var parent = FindElement(document, action.ParentPath);
        if (parent == null)
        {
            return;
        }

        var previous = string.IsNullOrWhiteSpace(action.PreviousSiblingPath) ? null : FindElement(document, action.PreviousSiblingPath);
        if (previous != null && previous.Parent == parent)
        {
            previous.AddAfterSelf(new XText(InferChildIndent(parent)), element);
            return;
        }

        var next = string.IsNullOrWhiteSpace(action.NextSiblingPath) ? null : FindElement(document, action.NextSiblingPath);
        if (next != null && next.Parent == parent)
        {
            next.AddBeforeSelf(new XText(InferChildIndent(parent)), element);
            return;
        }

        parent.Add(new XText(InferChildIndent(parent)), element);
    }

    private static string InferChildIndent(XElement parent)
    {
        var sample = parent
            .Nodes()
            .OfType<XText>()
            .Select(text => text.Value)
            .FirstOrDefault(value => value.Contains('\n', StringComparison.Ordinal));
        if (!string.IsNullOrEmpty(sample))
        {
            var lastNewLine = sample.LastIndexOf('\n');
            return "\n" + sample[(lastNewLine + 1)..];
        }

        return "\n  ";
    }

    private static XElement? FindElement(XDocument document, string path)
    {
        if (document.Root == null)
        {
            return null;
        }

        var built = new Dictionary<string, XmlElementSnapshot>(StringComparer.Ordinal);
        var fields = new Dictionary<string, XmlFieldSnapshot>(StringComparer.Ordinal);
        BuildElementIndex(document.Root, "", "", "", built, fields);
        return built.TryGetValue(path, out var snapshot) ? snapshot.Element : null;
    }

    private static bool SameFieldState(XmlFieldSnapshot? left, XmlFieldSnapshot? right, bool leftExists, bool rightExists)
    {
        return leftExists == rightExists &&
            string.Equals(left?.Value ?? "", right?.Value ?? "", StringComparison.Ordinal);
    }

    private static bool SameElementState(XmlElementSnapshot? left, XmlElementSnapshot? right, bool leftExists, bool rightExists)
    {
        return leftExists == rightExists &&
            string.Equals(left?.Canonical ?? "", right?.Canonical ?? "", StringComparison.Ordinal);
    }

    private static bool HasChangedAncestor(string path, HashSet<string> changedPaths)
    {
        var current = ParentPath(path);
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (changedPaths.Contains(current))
            {
                return true;
            }

            current = ParentPath(current);
        }

        return false;
    }

    private static string CanonicalElement(XElement element)
    {
        var builder = new StringBuilder();
        AppendCanonical(element, builder);
        return builder.ToString();
    }

    private static void AppendCanonical(XElement element, StringBuilder builder)
    {
        builder.Append('<').Append(ElementDisplayName(element));
        foreach (var attribute in element.Attributes()
            .Where(attribute => !attribute.IsNamespaceDeclaration)
            .OrderBy(AttributeDisplayName, StringComparer.Ordinal))
        {
            builder.Append(' ').Append(AttributeDisplayName(attribute)).Append('=').Append(NormalizeValue(attribute.Value));
        }

        builder.Append('>');
        var children = element.Elements().ToList();
        if (children.Count == 0)
        {
            builder.Append(NormalizeValue(element.Value));
        }
        else
        {
            foreach (var child in children)
            {
                AppendCanonical(child, builder);
            }
        }

        builder.Append("</").Append(ElementDisplayName(element)).Append('>');
    }

    private static string NormalizeValue(string? value)
    {
        return (value ?? "")
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
    }

    private static string ElementDisplayName(XElement element)
    {
        return element.Name.LocalName;
    }

    private static string AttributeDisplayName(XAttribute attribute)
    {
        return attribute.Name.LocalName;
    }

    private static string EscapePathValue(string value)
    {
        return value
            .Replace("%", "%25", StringComparison.Ordinal)
            .Replace("/", "%2F", StringComparison.Ordinal)
            .Replace("]", "%5D", StringComparison.Ordinal)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);
    }

    private static int Depth(string path)
    {
        return path.Count(character => character == '/');
    }

    private static string ParentPath(string path)
    {
        var index = path.LastIndexOf('/');
        return index <= 0 ? "" : path[..index];
    }

    private static string FieldElementPath(string fieldKey)
    {
        var index = fieldKey.LastIndexOf('/');
        return index <= 0 ? "" : fieldKey[..index];
    }

    private static string ElementDisplayPath(string path)
    {
        return path;
    }

    private sealed record XmlMergeDocument(
        XDocument Document,
        Dictionary<string, XmlElementSnapshot> Elements,
        Dictionary<string, XmlFieldSnapshot> Fields);

    private sealed record XmlElementSnapshot(
        string Path,
        string ParentPath,
        string PreviousSiblingPath,
        string NextSiblingPath,
        XElement Element,
        string Canonical);

    private sealed record XmlFieldSnapshot(
        string Key,
        string ElementPath,
        XmlMergeActionKind Kind,
        string Name,
        string Value,
        bool Exists);

    private sealed record XmlElementKey(string Label, string Value);
}
