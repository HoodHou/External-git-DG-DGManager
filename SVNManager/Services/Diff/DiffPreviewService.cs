namespace SVNManager;

internal sealed class DiffPreviewService : IDiffPreviewService
{
    public DiffPreviewData Compute(string oldFilePath, string newFilePath, DiffOptions? options = null)
    {
        var oldBinary = BinaryFileDetector.Inspect(oldFilePath);
        var newBinary = BinaryFileDetector.Inspect(newFilePath);
        if (oldBinary.IsBinary || newBinary.IsBinary)
        {
            return DiffPreviewData.FromBinary(new BinaryDiffStatus(
                oldFilePath,
                newFilePath,
                oldBinary.Size,
                newBinary.Size,
                oldBinary.IsBinary,
                newBinary.IsBinary));
        }

        if (DiffFileKindDetector.IsSpreadsheet(oldFilePath) && DiffFileKindDetector.IsSpreadsheet(newFilePath))
        {
            return DiffPreviewData.FromExcel(ExcelDiffService.Compare(oldFilePath, newFilePath));
        }

        return DiffPreviewData.FromText(TextDiffService.CreatePreview(oldFilePath, newFilePath, options));
    }

    public Task<DiffPreviewData> ComputeAsync(
        string oldFilePath,
        string newFilePath,
        DiffOptions? options = null,
        IProgress<DiffProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(DiffProgress.Indeterminate("Reading", "正在读取文件..."));
            var result = Compute(oldFilePath, newFilePath, options);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new DiffProgress("Done", 100, "差异计算完成"));
            return result;
        }, cancellationToken);
    }
}
