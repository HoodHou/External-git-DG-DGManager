using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NPOI.SS.UserModel;

namespace SVNManager;

public partial class Form1 : Form
{
    private readonly ComboBox _repositorySelector = new();
    private readonly ConfigView _configView = new();
    private readonly TextBox _outputText = new();
    private readonly FileStatusView _fileStatusView = new();
    private readonly HistoryView _historyView;
    private readonly ConflictView _conflictView = new();
    private readonly AllFilesView _allFilesView = new();
    private readonly TreeView _repositoryTree = new();
    private readonly System.Windows.Forms.Timer _fileTreeLoadDebounceTimer = new();
    private readonly System.Windows.Forms.Timer _treeExpansionSaveTimer = new();
    private readonly TabControl _mainTabs = new ShellTabControl();
    private readonly FlowLayoutPanel _shellNav = new();
    private readonly List<ShellNavButton> _shellNavButtons = [];
    private readonly TabPage _configPage = new("配置");
    private readonly TabPage _statusPage = new("File Status");
    private readonly TabPage _conflictPage = new("冲突");
    private readonly TabPage _historyPage = new("History");
    private readonly SplitContainer _workspaceSplit = new();
    private readonly ContextMenuStrip _fileTreeMenu = new();
    private readonly Button _updateButton = new ModernToolbarButton();
    private readonly Button _statusButton = new ModernToolbarButton();
    private readonly Button _commitButton = new ModernToolbarButton();
    private readonly Button _diffButton = new ModernToolbarButton();
    private readonly Button _externalMergeButton = new ModernToolbarButton();
    private readonly Button _conflictWorkflowButton = new ModernToolbarButton();
    private readonly Button _historyButton = new ModernToolbarButton();
    private readonly Button _moreActionsButton = new ModernToolbarButton();
    private readonly ContextMenuStrip _moreActionsMenu = new();
    private readonly ImageList _treeImages = new();
    private readonly ToolStripStatusLabel _statusLabel = new();
    private readonly ToolStripStatusLabel _localRevisionStatusLabel = new();
    private readonly ToolStripStatusLabel _toolUpdateStatusLabel = new();
    private readonly ToolStripStatusLabel _remoteStatusLabel = new();
    private readonly System.Windows.Forms.Timer _remoteCheckTimer = new();
    private readonly SvnClient _svn = new();
    private readonly AppState _state = new();
    private readonly AppSettings _settings;
    private bool _loadingRepository;
    private bool _loadingCurrentTab;
    private bool _checkingToolUpdate;
    private bool _checkingRemote;
    private WorkingCopyInfo _currentWorkingCopyInfo = WorkingCopyInfo.Empty;
    private SvnLogEntry? _latestRemoteLog;
    private GitUpdateStatus? _lastToolUpdateStatus;
    private string? _lastToolRepositoryRoot;
    private ReleaseUpdateStatus? _lastReleaseUpdateStatus;
    private CancellationTokenSource? _fileTreeLoadCts;
    private readonly DiffPreviewCache _historyDiffPreviewCache;
    private CancellationTokenSource? _historyDiffPreviewCts;
    private const int InitialHistoryLimit = 80;
    private const int HistoryLoadMoreStep = 200;
    private const int HistoryDeepSearchLimit = 1000;
    private const int HistoryRevisionRangeLimit = 5000;
    private const int MaxDiffPreviewCacheEntries = 40;
    private const int MaxFileTreeDisplayFiles = 8000;
    private const int MaxFileTreeAutoExpandFiles = 1200;
    private const int MaxFileTreeExpandAllFiles = 2000;
    private const int FileTreeLoadDebounceMilliseconds = 350;

    public Form1()
    {
        InitializeComponent();
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        _settings = AppSettings.Load();
        _historyDiffPreviewCache = new DiffPreviewCache(
            _settings.DiffCacheCapacity <= 0 ? MaxDiffPreviewCacheEntries : _settings.DiffCacheCapacity,
            Math.Max(16, _settings.DiffCacheMaxMB) * 1024L * 1024L);
        _historyView = new HistoryView(_treeImages, InitialHistoryLimit);
        _fileTreeLoadDebounceTimer.Interval = FileTreeLoadDebounceMilliseconds;
        _fileTreeLoadDebounceTimer.Tick += (_, _) =>
        {
            _fileTreeLoadDebounceTimer.Stop();
            LoadAllFiles();
        };
        _treeExpansionSaveTimer.Interval = 600;
        _treeExpansionSaveTimer.Tick += (_, _) =>
        {
            _treeExpansionSaveTimer.Stop();
            SaveTreeExpansionStateCore();
        };
        BuildUi();
        LoadSettingsIntoUi();
        Shown += async (_, _) =>
        {
            RestoreUiLayout();
            await RunStartupEnvironmentCheckAsync();
            if (ValidateWorkingCopyPathForBackground())
            {
                await LoadRepositoryHistoryAsync();
            }
            else
            {
                WriteOutput("请先在“配置”页导入已有工作副本，或检出一个新的 SVN 库。");
            }
            _remoteCheckTimer.Start();
            await CheckToolUpdatesAsync(showUpToDateMessage: false);
            await CheckRemoteChangesAsync(showUpToDateMessage: false);
        };
        FormClosing += (_, _) =>
        {
            _remoteCheckTimer.Stop();
            CancelFileTreeLoad();
            CancelHistoryDiffPreview();
            SaveUiLayout();
        };
    }

    private static string BuildLockInfoMessage(string relativePath, ProcessResult result)
    {
        if (result.ExitCode != 0)
        {
            return $"读取锁信息失败：{relativePath}{Environment.NewLine}{Environment.NewLine}{result.CombinedOutput}";
        }

        var lines = result.StandardOutput
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var lockLines = lines
            .Where(line =>
                line.StartsWith("Lock Owner:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Lock Created:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Lock Comment", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Lock Token:", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return lockLines.Count == 0
            ? $"当前文件没有检测到 SVN 锁。{Environment.NewLine}{Environment.NewLine}{relativePath}"
            : $"当前文件锁信息：{relativePath}{Environment.NewLine}{Environment.NewLine}{string.Join(Environment.NewLine, lockLines)}";
    }

}
