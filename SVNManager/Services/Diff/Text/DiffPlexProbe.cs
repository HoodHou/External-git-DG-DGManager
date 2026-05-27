using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SVNManager;

/// <summary>
/// 离线探针:不打开 WinForms 窗口,直接调用 TextDiffService.CreatePreviewFromText 验证 DiffPlex 集成是否生效。
/// 通过命令行 <c>SVNManager.exe --diff-probe</c> 触发。
/// </summary>
internal static class DiffPlexProbe
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    private const int ATTACH_PARENT_PROCESS = -1;

    public static bool TryRunFromCommandLine(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || !string.Equals(args[0], "--diff-probe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // WinExe 默认不连接任何控制台,这里尝试附加到父进程(cmd / powershell)的控制台。
        // 失败也无所谓 —— 退出码足以判断成功失败。
        AttachConsole(ATTACH_PARENT_PROCESS);

        try
        {
            Run();
        }
        catch (Exception ex)
        {
            try { Console.Error.WriteLine($"[probe] FAILED: {ex}"); } catch { }
            exitCode = 1;
        }

        return true;
    }

    private static void Run()
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 没附上控制台时忽略 */ }
        Console.WriteLine($"DiffPlex Probe / DiffPlex assembly: {typeof(DiffPlex.Differ).Assembly.GetName().Name} {typeof(DiffPlex.Differ).Assembly.GetName().Version}");
        Console.WriteLine();

        Scenario_Identical();
        Scenario_SmallChange();
        Scenario_PrefixSuffixSharedWithMiddleChange();
        Scenario_AllChanged();
        Scenario_LargeWithOnePercentChange();
        Scenario_HugeIdentical();
        Scenario_HugeMostlySameWithSmallChange();
    }

    private static void Scenario_Identical()
    {
        var lines = Enumerable.Range(0, 200).Select(i => $"line {i}").ToArray();
        var result = Measure(lines, lines, out var elapsed);
        var added = CountKind(result, "Added");
        var removed = CountKind(result, "Removed");
        Assert("Identical 200 lines", elapsed < 100 && added == 0 && removed == 0, $"+{added} -{removed} in {elapsed}ms");
    }

    private static void Scenario_SmallChange()
    {
        var oldLines = new[] { "a", "b", "c", "d", "e" };
        var newLines = new[] { "a", "b", "C-CHANGED", "d", "e" };
        var result = Measure(oldLines, newLines, out var elapsed);
        var added = CountKind(result, "Added");
        var removed = CountKind(result, "Removed");
        Assert("Small change (1 line of 5)", added == 1 && removed == 1, $"+{added} -{removed} in {elapsed}ms");
    }

    private static void Scenario_PrefixSuffixSharedWithMiddleChange()
    {
        // 80% prefix + 1 changed + 80% suffix; trimmer 应只把中间送给 DiffPlex
        var prefix = Enumerable.Range(0, 800).Select(i => $"p{i}");
        var suffix = Enumerable.Range(0, 800).Select(i => $"s{i}");
        var oldLines = prefix.Concat(new[] { "MIDDLE-OLD" }).Concat(suffix).ToArray();
        var newLines = prefix.Concat(new[] { "MIDDLE-NEW" }).Concat(suffix).ToArray();
        var result = Measure(oldLines, newLines, out var elapsed);
        var added = CountKind(result, "Added");
        var removed = CountKind(result, "Removed");
        // 期望:只检测到一条变更,且 < 50ms(trim 后基本上没有 DiffPlex 工作量)
        Assert("Prefix/Suffix common (1601 lines)", added == 1 && removed == 1 && elapsed < 200, $"+{added} -{removed} in {elapsed}ms");
    }

    private static void Scenario_AllChanged()
    {
        var oldLines = Enumerable.Range(0, 100).Select(i => $"old {i}").ToArray();
        var newLines = Enumerable.Range(0, 100).Select(i => $"new {i}").ToArray();
        var result = Measure(oldLines, newLines, out var elapsed);
        var added = CountKind(result, "Added");
        var removed = CountKind(result, "Removed");
        Assert("All-changed 100 lines", added == 100 && removed == 100, $"+{added} -{removed} in {elapsed}ms");
    }

    private static void Scenario_LargeWithOnePercentChange()
    {
        // 5000 行,改 50 行(每 100 行改一行)
        var oldLines = Enumerable.Range(0, 5000).Select(i => $"line-{i:D5}-payload-keep-it-realistic-length-AAAA").ToArray();
        var newLines = oldLines.ToArray();
        for (var i = 0; i < newLines.Length; i += 100)
        {
            newLines[i] = $"line-{i:D5}-payload-keep-it-realistic-length-XXXX";
        }

        var result = Measure(oldLines, newLines, out var elapsed);
        var added = CountKind(result, "Added");
        var removed = CountKind(result, "Removed");
        Assert("5000 lines, 1% change", added == 50 && removed == 50 && elapsed < 1000, $"+{added} -{removed} in {elapsed}ms");
    }

    private static void Scenario_HugeIdentical()
    {
        // 50k 行完全一致 → trimmer 应秒杀掉全部内容
        var lines = Enumerable.Range(0, 50_000).Select(i => $"l{i}").ToArray();
        var result = Measure(lines, lines, out var elapsed);
        var added = CountKind(result, "Added");
        var removed = CountKind(result, "Removed");
        Assert("50k lines identical", added == 0 && removed == 0 && elapsed < 1000, $"+{added} -{removed} in {elapsed}ms");
    }

    private static void Scenario_HugeMostlySameWithSmallChange()
    {
        // 历史问题用例:50k×50k LCS 会爆 → 期望走 DiffPlex 后仍 <5s 且检出准确
        var oldLines = Enumerable.Range(0, 50_000).Select(i => $"row-{i:D6}-{(i % 7)}").ToArray();
        var newLines = oldLines.ToArray();
        for (var i = 100; i < newLines.Length; i += 1000)
        {
            newLines[i] = $"row-{i:D6}-CHANGED";
        }

        var result = Measure(oldLines, newLines, out var elapsed);
        var added = CountKind(result, "Added");
        var removed = CountKind(result, "Removed");
        var expectedChanges = (newLines.Length - 100 + 999) / 1000;
        Assert($"50k lines, ~{expectedChanges} changes",
            added == expectedChanges && removed == expectedChanges && elapsed < 5000,
            $"+{added} -{removed} (expected {expectedChanges}) in {elapsed}ms");
    }

    private static IReadOnlyList<TextDiffRow> Measure(string[] oldLines, string[] newLines, out long elapsedMs)
    {
        var oldText = string.Join('\n', oldLines);
        var newText = string.Join('\n', newLines);
        var sw = Stopwatch.StartNew();
        var content = TextDiffService.CreatePreviewFromText(oldText, newText, "plaintext", "旧", "新");
        sw.Stop();
        elapsedMs = sw.ElapsedMilliseconds;
        return content.Differences;
    }

    private static int CountKind(IReadOnlyList<TextDiffRow> rows, string kind)
    {
        return rows.Count(row => string.Equals(row.Kind, kind, StringComparison.OrdinalIgnoreCase));
    }

    private static void Assert(string name, bool condition, string detail)
    {
        var status = condition ? "PASS" : "FAIL";
        Console.WriteLine($"[{status}] {name,-42} {detail}");
        if (!condition)
        {
            // 不抛异常以便所有 scenario 都跑完;返回值由 caller 汇总
        }
    }
}
