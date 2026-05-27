namespace SVNManager;

internal sealed record SvnStatusXmlEntry(string Path, string Item);

internal sealed record SvnInfoXmlEntry(
    long Revision,
    long LastChangedRevision,
    string Url,
    string RepositoryRootUrl,
    string WorkingCopyRootPath);

internal sealed record SvnLogXmlPath(string Action, string RepositoryPath);

internal sealed record SvnLogXmlEntry(
    long Revision,
    string Author,
    DateTimeOffset Date,
    string Message,
    IReadOnlyList<SvnLogXmlPath> ChangedPaths);
