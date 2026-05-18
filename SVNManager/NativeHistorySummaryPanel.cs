using System.Text;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace SVNManager;

internal sealed class HistorySummaryPanel : UserControl
{
    private const int MaxRenderedChangedFiles = 240;
    private const int MaxRenderedCommits = 120;

    public event Action<HistorySummaryChangedFile>? FileClicked;
    public HistorySummaryPanel(HistorySummaryData data)
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(250, 250, 250); // Almost white back
        AutoScroll = true;

        if (data.IsPlainText)
        {
            Controls.Add(new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                BorderStyle = BorderStyle.None,
                BackColor = Color.FromArgb(250, 250, 250),
                Text = data.PlainText,
            });
            return;
        }

        var root = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(24, 20, 24, 20), // Wider padding for modern look
            BackColor = Color.FromArgb(250, 250, 250), 
        };
        Controls.Add(root);

        root.Controls.Add(CreateTitle(data));
        
        var metadata = CreateMetadataFlow(data);
        if (metadata != null)
        {
            root.Controls.Add(metadata);
        }

        if (!string.IsNullOrWhiteSpace(data.Message))
        {
            root.Controls.Add(CreateMessage(data.Message));
        }

        if (data.ChangedFiles.Count > 0)
        {
            root.Controls.Add(CreateChangedFilesTitle(data.ChangedFiles));
            foreach (var file in data.ChangedFiles.Take(MaxRenderedChangedFiles))
            {
                root.Controls.Add(CreateChangedFileRow(file));
            }

            if (data.ChangedFiles.Count > MaxRenderedChangedFiles)
            {
                root.Controls.Add(CreateOverflowHint($"还有 {data.ChangedFiles.Count - MaxRenderedChangedFiles} 个文件未在摘要里展开。左侧 Changed files 树仍可搜索和操作全部文件。"));
            }
        }

        if (data.Commits.Count > 0)
        {
            root.Controls.Add(CreateSectionTitle($"提交记录 ({data.Commits.Count})"));
            foreach (var commit in data.Commits.Take(MaxRenderedCommits))
            {
                root.Controls.Add(CreateCommitRow(commit));
            }

            if (data.Commits.Count > MaxRenderedCommits)
            {
                root.Controls.Add(CreateOverflowHint($"还有 {data.Commits.Count - MaxRenderedCommits} 条提交未在摘要里展开。历史列表仍保留完整选择范围。"));
            }
        }
    }

    private static Control CreateTitle(HistorySummaryData data)
    {
        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 16),
            Width = 720
        };
        
        var titleLabel = new Label
        {
            AutoSize = true,
            Text = data.Title,
            Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold),
            ForeColor = Color.FromArgb(15, 23, 42), // Slate 900
            Margin = new Padding(0, 0, 16, 2),
            Cursor = Cursors.Hand,
        };
        
        var tip = new ToolTip();
        tip.SetToolTip(titleLabel, "点击复制");
        panel.Disposed += (_, _) => tip.Dispose();
        titleLabel.Click += (s, e) => {
            if (!string.IsNullOrWhiteSpace(data.Title)) Clipboard.SetText(data.Title.Replace("r", ""));
            titleLabel.ForeColor = Color.FromArgb(16, 185, 129); 
            Task.Delay(300).ContinueWith(_ => titleLabel.Invoke(() => titleLabel.ForeColor = Color.FromArgb(15, 23, 42)));
        };
        panel.Controls.Add(titleLabel);

        foreach (var badge in data.Badges)
        {
            var isBranch = badge.Tone == "branch";
            panel.Controls.Add(new Label
            {
                AutoSize = true,
                Text = badge.Text,
                BackColor = isBranch ? Color.FromArgb(236, 253, 245) : (badge.Tone == "current" ? Color.FromArgb(239, 246, 255) : Color.FromArgb(241, 245, 249)),
                ForeColor = isBranch ? Color.FromArgb(5, 150, 105) : (badge.Tone == "current" ? Color.FromArgb(37, 99, 235) : Color.FromArgb(71, 85, 105)),
                BorderStyle = BorderStyle.None,
                Padding = new Padding(8, 4, 8, 4),
                Margin = new Padding(0, 4, 8, 2),
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            });
        }

        return panel;
    }

    private static Control? CreateMetadataFlow(HistorySummaryData data)
    {
        if (string.IsNullOrEmpty(data.Author)) return null;

        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 24),
            WrapContents = false,
        };

        var avatar = new PictureBox { Width = 26, Height = 26, Margin = new Padding(0, 0, 8, 0) };
        avatar.Paint += (s, e) => {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var color = GetAvatarColor(data.Author);
            using var brush = new SolidBrush(color);
            e.Graphics.FillEllipse(brush, 0, 0, 25, 25);
            var initial = data.Author.Length > 0 ? data.Author.Substring(data.Author.Length - 1) : "?";
            var textBrush = Brushes.White;
            using var font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            var size = e.Graphics.MeasureString(initial, font);
            e.Graphics.DrawString(initial, font, textBrush, 12.5f - size.Width / 2, 12.5f - size.Height / 2);
        };
        panel.Controls.Add(avatar);

        panel.Controls.Add(new Label
        {
            AutoSize = true,
            Text = $"{data.Author} committed on {data.DateText}",
            Font = new Font("Microsoft YaHei UI", 10.5F),
            ForeColor = Color.FromArgb(71, 85, 105),
            Margin = new Padding(0, 3, 0, 0),
        });

        return panel;
    }

    private static Color GetAvatarColor(string author)
    {
        var colors = new[] { 
            Color.FromArgb(16, 185, 129), // Emerald
            Color.FromArgb(59, 130, 246), // Blue
            Color.FromArgb(244, 63, 94),  // Rose
            Color.FromArgb(245, 158, 11), // Amber
            Color.FromArgb(139, 92, 246), // Violet
            Color.FromArgb(20, 184, 166)  // Teal
        };
        var hash = 0;
        foreach (var c in author) hash = (hash * 31) + c;
        return colors[Math.Abs(hash) % colors.Length];
    }

    private static Control CreateMessage(string message)
    {
        var panel = new Panel
        {
            Width = 720,
            AutoSize = true,
            Padding = new Padding(16, 12, 12, 12),
            Margin = new Padding(0, 0, 0, 24),
            BackColor = Color.FromArgb(248, 250, 252), // subtle background
        };
        panel.Paint += (s, e) => {
            using var brush = new SolidBrush(Color.FromArgb(203, 213, 225)); // Slate 300 accent border
            e.Graphics.FillRectangle(brush, 0, 0, 4, panel.Height);
        };

        var label = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(690, 0),
            Text = message,
            Font = new Font("Consolas", 10F),
            ForeColor = Color.FromArgb(30, 41, 59),
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
        };
        panel.Controls.Add(label);
        return panel;
    }

    private static Control CreateChangedFilesTitle(IReadOnlyList<HistorySummaryChangedFile> files)
    {
        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 12),
        };
        panel.Controls.Add(new Label
        {
            AutoSize = true,
            Text = $"Changed files ({files.Count})",
            Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
            ForeColor = Color.FromArgb(15, 23, 42),
            Margin = new Padding(0, 0, 16, 0),
        });

        var added = files.Count(f => f.Action == "A");
        var modified = files.Count(f => f.Action == "M");
        var deleted = files.Count(f => f.Action == "D");

        if (added > 0) panel.Controls.Add(CreateStatBadge($"+{added} added", Color.FromArgb(22, 163, 74)));
        if (modified > 0) panel.Controls.Add(CreateStatBadge($"~{modified} modified", Color.FromArgb(217, 119, 6)));
        if (deleted > 0) panel.Controls.Add(CreateStatBadge($"-{deleted} deleted", Color.FromArgb(220, 38, 38)));

        return panel;
    }

    private static Label CreateStatBadge(string text, Color color)
    {
        return new Label
        {
            AutoSize = true,
            Text = text,
            ForeColor = color,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular),
            Margin = new Padding(0, 2, 12, 0),
        };
    }

    private static Control CreateSectionTitle(string text)
    {
        return new Label
        {
            AutoSize = true,
            Text = text,
            Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
            ForeColor = Color.FromArgb(15, 23, 42),
            Margin = new Padding(0, 8, 0, 8),
        };
    }

    private Control CreateChangedFileRow(HistorySummaryChangedFile file)
    {
        var panel = new TableLayoutPanel
        {
            Width = 720,
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(0, 0, 0, 4),
            BackColor = Color.FromArgb(250, 250, 250),
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        
        var actionLabel = new Label
        {
            Text = file.Action,
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Top,
            Height = 22,
            BackColor = ActionColor(file.Action),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 8F, FontStyle.Bold),
            Margin = new Padding(0, 3, 8, 0),
        };
        
        panel.Controls.Add(actionLabel, 0, 0);

        var pathLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(660, 0),
            Text = string.IsNullOrWhiteSpace(file.RepositoryPath)
                ? file.TreePath
                : $"{file.TreePath}{Environment.NewLine}{file.RepositoryPath}",
            Font = new Font("Consolas", 9.5F),
            ForeColor = Color.FromArgb(51, 65, 85),
            Margin = new Padding(0, 3, 0, 3),
            Cursor = Cursors.Hand,
        };
        panel.Controls.Add(pathLabel, 1, 0);

        void OnFileClick(object? sender, EventArgs e) => FileClicked?.Invoke(file);
        void OnMouseEnter(object? sender, EventArgs e) {
            panel.BackColor = Color.FromArgb(241, 245, 249); // hover Slate 100
            pathLabel.ForeColor = Color.FromArgb(37, 99, 235); // Blue 600
        }
        void OnMouseLeave(object? sender, EventArgs e) {
            panel.BackColor = Color.FromArgb(250, 250, 250);
            pathLabel.ForeColor = Color.FromArgb(51, 65, 85);
        }

        panel.Cursor = Cursors.Hand;
        panel.Click += OnFileClick;
        pathLabel.Click += OnFileClick;
        
        panel.MouseEnter += OnMouseEnter;
        panel.MouseLeave += OnMouseLeave;
        pathLabel.MouseEnter += OnMouseEnter;
        pathLabel.MouseLeave += OnMouseLeave;

        return panel;
    }

    private static Control CreateCommitRow(HistorySummaryCommit commit)
    {
        return new Label
        {
            AutoSize = true,
            MaximumSize = new Size(720, 0),
            Text = $"r{commit.Revision}  {commit.DateText}  {commit.Author}{Environment.NewLine}{commit.Message}",
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(8),
            Margin = new Padding(0, 0, 0, 6),
            Font = new Font("Microsoft YaHei UI", 9.5F),
            ForeColor = Color.FromArgb(39, 50, 64),
        };
    }

    private static Control CreateOverflowHint(string text)
    {
        return new Label
        {
            AutoSize = true,
            MaximumSize = new Size(720, 0),
            Text = text,
            Padding = new Padding(8),
            Margin = new Padding(0, 6, 0, 14),
            BackColor = Color.FromArgb(241, 245, 249),
            ForeColor = Color.FromArgb(71, 85, 105),
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
        };
    }

    private static Color ActionColor(string action)
    {
        return action switch
        {
            "A" => Color.FromArgb(16, 185, 129), // Emerald 500
            "M" => Color.FromArgb(245, 158, 11), // Amber 500
            "D" => Color.FromArgb(239, 68, 68),  // Red 500
            "C" => Color.FromArgb(168, 85, 247), // Purple 500
            "R" => Color.FromArgb(99, 102, 241), // Indigo 500
            _ => Color.FromArgb(148, 163, 184),  // Slate 400
        };
    }
}

internal sealed record HistorySummaryData(
    bool IsPlainText,
    string PlainText,
    string Title,
    string Author,
    string DateText,
    IReadOnlyList<HistorySummaryBadge> Badges,
    string Message,
    IReadOnlyList<HistorySummaryChangedFile> ChangedFiles,
    IReadOnlyList<HistorySummaryCommit> Commits)
{
    public static HistorySummaryData Plain(string text)
    {
        return new HistorySummaryData(true, text, "", "", "", [], "", [], []);
    }

    public static HistorySummaryData FromLog(SvnLogEntry log)
    {
        if (log.IsUncommitted)
        {
            return new HistorySummaryData(
                false, "", "Uncommitted changes", "本地工作副本", "",
                [new HistorySummaryBadge("本地未提交", "current")],
                "", ToHistoryFiles(log.ChangedFiles), []);
        }

        var badges = new List<HistorySummaryBadge>();
        if (log.IsWorkingCopyRevision)
        {
            badges.Add(new HistorySummaryBadge("当前工作副本位置", "current"));
        }

        var branchBadge = ExtractBranch(log.ChangedFiles);
        if (!string.IsNullOrEmpty(branchBadge))
        {
            badges.Add(new HistorySummaryBadge(branchBadge, "branch"));
        }

        return new HistorySummaryData(
            false, "", $"r{log.Revision}", log.Author, log.LocalDateText,
            badges, log.Message, ToHistoryFiles(log.ChangedFiles), []);
    }

    public static HistorySummaryData FromRange(IReadOnlyList<SvnLogEntry> logs, IReadOnlyList<ChangedFileEntry> changedFiles)
    {
        var ordered = logs.Where(log => !log.IsUncommitted).OrderBy(log => log.Revision).ToList();
        if (ordered.Count == 0)
        {
            return Plain("当前选择只包含未提交改动，请单选 Uncommitted changes 查看。");
        }

        var first = ordered.First();
        var last = ordered.Last();
        
        var badges = new List<HistorySummaryBadge> { new HistorySummaryBadge($"r{first.Revision} -> r{last.Revision}", "current") };
        var branchBadge = ExtractBranch(changedFiles);
        if (!string.IsNullOrEmpty(branchBadge)) badges.Add(new HistorySummaryBadge(branchBadge, "branch"));

        return new HistorySummaryData(
            false, "", $"Selected commits ({ordered.Count})", "", $"{first.LocalDateText} to {last.LocalDateText}",
            badges, "", ToHistoryFiles(changedFiles),
            ordered.OrderByDescending(log => log.Revision)
                .Select(log => new HistorySummaryCommit(log.Revision, log.LocalDateText, log.Author, log.ShortMessage))
                .ToList());
    }

    private static string ExtractBranch(IEnumerable<ChangedFileEntry> files)
    {
        var paths = files.Select(f => f.RepositoryPath).Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (paths.Count == 0) return "";

        var branches = paths.Select(p => {
            if (p.StartsWith("branch/")) {
                var parts = p.Split('/');
                if (parts.Length >= 2) return $"{parts[0]}/{parts[1]}";
            }
            if (p.StartsWith("trunk")) return "trunk";
            return "";
        }).Where(b => !string.IsNullOrEmpty(b)).Distinct().ToList();

        if (branches.Count == 1) return branches[0];
        if (branches.Count > 1) return "Multiple Branches";
        return "";
    }

    public string ToPlainText()
    {
        if (IsPlainText) return PlainText;

        var builder = new StringBuilder();
        builder.AppendLine(Title);
        builder.AppendLine($"Author: {Author}");
        builder.AppendLine($"Date: {DateText}");

        if (!string.IsNullOrWhiteSpace(Message))
        {
            builder.AppendLine();
            builder.AppendLine(Message);
        }

        if (ChangedFiles.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"Changed files ({ChangedFiles.Count})");
            foreach (var file in ChangedFiles)
            {
                builder.AppendLine($"{file.Action} {file.RepositoryPath}".TrimEnd());
            }
        }

        if (Commits.Count > 0)
        {
            builder.AppendLine();
            foreach (var commit in Commits)
            {
                builder.AppendLine($"r{commit.Revision}  {commit.DateText}  {commit.Author}  {commit.Message}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static IReadOnlyList<HistorySummaryChangedFile> ToHistoryFiles(IEnumerable<ChangedFileEntry> files)
    {
        return files
            .Select(file => new HistorySummaryChangedFile(file.Action, file.TreePath, file.RepositoryPath, file.RelativePath))
            .ToList();
    }
}

internal sealed record HistorySummaryBadge(string Text, string Tone);

internal sealed record HistorySummaryChangedFile(string Action, string TreePath, string RepositoryPath, string RelativePath);

internal sealed record HistorySummaryCommit(long Revision, string DateText, string Author, string Message);
