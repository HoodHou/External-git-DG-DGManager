using System.Text;
using System.Xml.Linq;

namespace SVNManager;

internal static class SpreadsheetXmlFormat
{
    public const string SpreadsheetNamespaceName = "urn:schemas-microsoft-com:office:spreadsheet";

    public static bool IsSpreadsheetXmlFile(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            var document = XDocument.Load(stream, LoadOptions.None);
            return IsSpreadsheetWorkbook(document.Root);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsSpreadsheetWorkbook(XElement? root)
    {
        if (root == null || !IsNamed(root, "Workbook"))
        {
            return false;
        }

        if (root.Name.NamespaceName == SpreadsheetNamespaceName)
        {
            return true;
        }

        return Elements(root, "Worksheet")
            .Select(worksheet => Element(worksheet, "Table"))
            .Where(table => table != null)
            .Any(table => Elements(table!, "Row").Any(row => Elements(row, "Cell").Any()));
    }

    public static IEnumerable<XElement> Elements(XContainer container, string localName)
    {
        return container.Elements().Where(element => IsNamed(element, localName));
    }

    public static IEnumerable<XElement> Descendants(XContainer container, string localName)
    {
        return container.Descendants().Where(element => IsNamed(element, localName));
    }

    public static XElement? Element(XContainer container, string localName)
    {
        return Elements(container, localName).FirstOrDefault();
    }

    public static string? AttributeValue(XElement element, string localName)
    {
        return element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == localName)?.Value;
    }

    public static void SetAttributeValue(XElement element, string localName, string? value)
    {
        element.SetAttributeValue(AttributeName(element, localName), value);
    }

    public static XElement CreateChild(XElement parent, string localName)
    {
        return new XElement(ElementName(parent, localName));
    }

    public static string ReadCellText(XElement dataElement)
    {
        return NormalizeCellText(ExtractText(dataElement));
    }

    public static string NormalizeCellText(string? value)
    {
        var normalized = NormalizeLineEndings(value);
        if (!normalized.Contains('\n', StringComparison.Ordinal))
        {
            return normalized.Trim();
        }

        var builder = new StringBuilder(normalized.Length);
        foreach (var line in normalized.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                builder.Append(trimmed);
            }
        }

        return builder.ToString();
    }

    private static bool IsNamed(XElement element, string localName)
    {
        return string.Equals(element.Name.LocalName, localName, StringComparison.Ordinal);
    }

    private static string ExtractText(XElement element)
    {
        if (!element.Elements().Any())
        {
            return element.Value;
        }

        var parts = new List<string>();
        AppendNodeText(element.Nodes(), parts);
        return string.Concat(parts);
    }

    private static void AppendNodeText(IEnumerable<XNode> nodes, List<string> parts)
    {
        foreach (var node in nodes)
        {
            if (node is XText text)
            {
                var value = NormalizeTextNode(text.Value);
                if (!string.IsNullOrEmpty(value))
                {
                    parts.Add(value);
                }

                continue;
            }

            if (node is XElement element)
            {
                AppendNodeText(element.Nodes(), parts);
            }
        }
    }

    private static string NormalizeTextNode(string? value)
    {
        var normalized = NormalizeLineEndings(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "";
        }

        if (!normalized.Contains('\n', StringComparison.Ordinal))
        {
            return normalized;
        }

        var builder = new StringBuilder(normalized.Length);
        foreach (var line in normalized.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                builder.Append(trimmed);
            }
        }

        return builder.ToString();
    }

    private static string NormalizeLineEndings(string? value)
    {
        return (value ?? "")
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
    }

    private static XName ElementName(XElement parent, string localName)
    {
        var namespaceName = parent.Name.NamespaceName;
        return string.IsNullOrWhiteSpace(namespaceName)
            ? XName.Get(localName)
            : XName.Get(localName, namespaceName);
    }

    private static XName AttributeName(XElement element, string localName)
    {
        var existing = element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == localName);
        if (existing != null)
        {
            return existing.Name;
        }

        var inherited = element
            .AncestorsAndSelf()
            .SelectMany(ancestor => ancestor.Attributes())
            .FirstOrDefault(attribute =>
                attribute.Name.LocalName == localName &&
                attribute.Name.NamespaceName == SpreadsheetNamespaceName);
        if (inherited != null)
        {
            return inherited.Name;
        }

        var usesSpreadsheetNamespace = element
            .AncestorsAndSelf()
            .Any(ancestor => ancestor.Name.NamespaceName == SpreadsheetNamespaceName);
        return usesSpreadsheetNamespace
            ? XName.Get(localName, SpreadsheetNamespaceName)
            : XName.Get(localName);
    }
}
