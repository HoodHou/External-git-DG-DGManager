namespace SVNManager;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, args) => HandleFatalException(args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var exception = args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString() ?? "Unknown fatal exception");
            LogStartup("Unhandled AppDomain exception", exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogStartup("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        try
        {
            LogStartup("Starting SVNManager");
            ApplicationConfiguration.Initialize();
            Application.Run(new Form1());
            LogStartup("SVNManager exited normally");
        }
        catch (Exception ex)
        {
            HandleFatalException(ex);
        }
    }

    private static void HandleFatalException(Exception ex)
    {
        LogStartup("Fatal exception", ex);
        try
        {
            MessageBox.Show(
                "梦境 SVN 管理器遇到运行错误，错误已经写入日志。" + Environment.NewLine + Environment.NewLine +
                StartupLogPath + Environment.NewLine + Environment.NewLine +
                ex.Message,
                "运行错误",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            // If even MessageBox fails, the startup log is still the source of truth.
        }
    }

    private static string StartupLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SVNManager",
        "startup.log");

    private static void LogStartup(string message, Exception? exception = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StartupLogPath)!);
            var lines = new[]
            {
                "------------------------------------------------------------",
                DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"),
                message,
                "Exe: " + Application.ExecutablePath,
                "BaseDirectory: " + AppContext.BaseDirectory,
                "OS: " + Environment.OSVersion,
                "64BitOS: " + Environment.Is64BitOperatingSystem,
                "64BitProcess: " + Environment.Is64BitProcess,
                exception?.ToString() ?? "",
            };
            File.AppendAllText(StartupLogPath, string.Join(Environment.NewLine, lines) + Environment.NewLine);
        }
        catch
        {
            // Startup logging must never create another startup failure.
        }
    }
}
