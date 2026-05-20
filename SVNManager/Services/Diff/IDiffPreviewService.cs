namespace SVNManager;

internal interface IDiffPreviewService
{
    DiffPreviewData Compute(string oldFilePath, string newFilePath, DiffOptions? options = null);

    Task<DiffPreviewData> ComputeAsync(
        string oldFilePath,
        string newFilePath,
        DiffOptions? options = null,
        IProgress<DiffProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
