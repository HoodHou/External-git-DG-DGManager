using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SVNManager;

internal static class SpreadsheetMergeWorker
{
    private const string WorkerSwitch = "--spreadsheet-merge-worker";
    private const string BuildPlanCommand = "build-plan";
    private const string ApplyWritesCommand = "apply-writes";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static bool TryRunFromCommandLine(string[] args, out int exitCode)
    {
        exitCode = 0;
        var effectiveArgs = args.Length > 0
            ? args
            : Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (effectiveArgs.Length == 0 || !string.Equals(effectiveArgs[0], WorkerSwitch, StringComparison.Ordinal))
        {
            return false;
        }

        exitCode = RunWorker(effectiveArgs);
        return true;
    }

    public static async Task<SpreadsheetMergePlan> BuildPlanAsync(
        string baseFilePath,
        string localFilePath,
        string remoteFilePath,
        CancellationToken cancellationToken = default)
    {
        SpreadsheetThreeWayMergeService.ValidateMergeInputs(baseFilePath, localFilePath, remoteFilePath);

        var requestPath = DiffTempFileTracker.NewTempFile("SVNManager_MergeRequest", ".json");
        var responsePath = DiffTempFileTracker.NewTempFile("SVNManager_MergeResponse", ".json");
        try
        {
            var request = new SpreadsheetMergeBuildPlanRequest(baseFilePath, localFilePath, remoteFilePath);
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request, JsonOptions), Encoding.UTF8, cancellationToken);
            var response = await RunWorkerCommandAsync(BuildPlanCommand, requestPath, responsePath, cancellationToken);
            if (!response.Success || response.Plan == null)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(response.Error)
                    ? "表格合并 Worker 没有返回可用计划。"
                    : response.Error);
            }

            return response.Plan;
        }
        finally
        {
            TryDelete(requestPath);
            TryDelete(responsePath);
        }
    }

    public static async Task ApplyWritesAsync(
        string localFilePath,
        IReadOnlyList<SpreadsheetMergeWrite> writes,
        CancellationToken cancellationToken = default)
    {
        if (writes.Count == 0)
        {
            return;
        }

        SpreadsheetThreeWayMergeService.ValidateMergeInputs(localFilePath);
        var requestPath = DiffTempFileTracker.NewTempFile("SVNManager_MergeWriteRequest", ".json");
        var responsePath = DiffTempFileTracker.NewTempFile("SVNManager_MergeWriteResponse", ".json");
        try
        {
            var request = new SpreadsheetMergeApplyWritesRequest(localFilePath, writes.ToList());
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request, JsonOptions), Encoding.UTF8, cancellationToken);
            var response = await RunWorkerCommandAsync(ApplyWritesCommand, requestPath, responsePath, cancellationToken);
            if (!response.Success)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(response.Error)
                    ? "表格合并 Worker 写入失败。"
                    : response.Error);
            }
        }
        finally
        {
            TryDelete(requestPath);
            TryDelete(responsePath);
        }
    }

    private static int RunWorker(string[] args)
    {
        if (args.Length != 4)
        {
            return 2;
        }

        var command = args[1];
        var responsePath = args[3];
        try
        {
            var requestJson = File.ReadAllText(args[2], Encoding.UTF8);
            if (string.Equals(command, BuildPlanCommand, StringComparison.Ordinal))
            {
                var request = JsonSerializer.Deserialize<SpreadsheetMergeBuildPlanRequest>(requestJson, JsonOptions)
                    ?? throw new InvalidOperationException("表格合并 Worker 请求为空。");
                var plan = SpreadsheetThreeWayMergeService.BuildPlan(
                    request.BaseFilePath,
                    request.LocalFilePath,
                    request.RemoteFilePath);
                WriteResponse(responsePath, new SpreadsheetMergeWorkerResponse(true, "", plan));
                return 0;
            }

            if (string.Equals(command, ApplyWritesCommand, StringComparison.Ordinal))
            {
                var request = JsonSerializer.Deserialize<SpreadsheetMergeApplyWritesRequest>(requestJson, JsonOptions)
                    ?? throw new InvalidOperationException("表格合并写入 Worker 请求为空。");
                SpreadsheetThreeWayMergeService.ApplyWrites(request.LocalFilePath, request.Writes);
                WriteResponse(responsePath, new SpreadsheetMergeWorkerResponse(true, "", null));
                return 0;
            }

            WriteResponse(responsePath, new SpreadsheetMergeWorkerResponse(false, $"未知表格合并 Worker 命令：{command}", null));
            return 2;
        }
        catch (OutOfMemoryException ex)
        {
            WriteResponse(responsePath, new SpreadsheetMergeWorkerResponse(
                false,
                "表格过大，合并 Worker 内存不足。请先缩小文件范围、拆分表格，或调高 64 位运行环境可用内存。" + Environment.NewLine + ex.Message,
                null));
            return 1;
        }
        catch (Exception ex)
        {
            WriteResponse(responsePath, new SpreadsheetMergeWorkerResponse(false, ex.Message, null));
            return 1;
        }
    }

    private static async Task<SpreadsheetMergeWorkerResponse> RunWorkerCommandAsync(
        string command,
        string requestPath,
        string responsePath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveWorkerExecutablePath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(WorkerSwitch);
        startInfo.ArgumentList.Add(command);
        startInfo.ArgumentList.Add(requestPath);
        startInfo.ArgumentList.Add(responsePath);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动表格合并 Worker 进程。");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (!File.Exists(responsePath))
        {
            throw new InvalidOperationException(
                "表格合并 Worker 没有返回结果。" + Environment.NewLine +
                BuildWorkerOutput(process.ExitCode, stdout, stderr));
        }

        var responseJson = await File.ReadAllTextAsync(responsePath, Encoding.UTF8, cancellationToken);
        var response = JsonSerializer.Deserialize<SpreadsheetMergeWorkerResponse>(responseJson, JsonOptions);
        if (response == null)
        {
            throw new InvalidOperationException("表格合并 Worker 返回了无法解析的结果。");
        }

        if (!response.Success && string.IsNullOrWhiteSpace(response.Error))
        {
            return response with { Error = BuildWorkerOutput(process.ExitCode, stdout, stderr) };
        }

        return response;
    }

    private static string ResolveWorkerExecutablePath()
    {
        var assemblyLocation = typeof(SpreadsheetMergeWorker).Assembly.Location;
        if (!string.IsNullOrWhiteSpace(assemblyLocation))
        {
            var siblingExe = Path.ChangeExtension(assemblyLocation, ".exe");
            if (File.Exists(siblingExe))
            {
                return siblingExe;
            }
        }

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
        {
            return processPath;
        }

        if (!string.IsNullOrWhiteSpace(Application.ExecutablePath) && File.Exists(Application.ExecutablePath))
        {
            return Application.ExecutablePath;
        }

        throw new InvalidOperationException("无法定位表格合并 Worker 可执行文件。");
    }

    private static void WriteResponse(string responsePath, SpreadsheetMergeWorkerResponse response)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(responsePath)!);
        File.WriteAllText(responsePath, JsonSerializer.Serialize(response, JsonOptions), Encoding.UTF8);
    }

    private static string BuildWorkerOutput(int exitCode, string stdout, string stderr)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Worker exit code: {exitCode}");
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            builder.AppendLine(stdout.Trim());
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            builder.AppendLine(stderr.Trim());
        }

        return builder.ToString().Trim();
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private sealed record SpreadsheetMergeBuildPlanRequest(
        string BaseFilePath,
        string LocalFilePath,
        string RemoteFilePath);

    private sealed record SpreadsheetMergeApplyWritesRequest(
        string LocalFilePath,
        List<SpreadsheetMergeWrite> Writes);

    private sealed record SpreadsheetMergeWorkerResponse(
        bool Success,
        string Error,
        SpreadsheetMergePlan? Plan);
}
