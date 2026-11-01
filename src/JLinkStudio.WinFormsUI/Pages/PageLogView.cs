using System.Collections.Concurrent;
using System.Text;
using JLinkStudio.Core.Models;
using JLinkStudio.WinFormsUI.Controls;
using JLinkStudio.WinFormsUI.Navigation;
using JLinkStudio.WinFormsUI.Theme;

namespace JLinkStudio.WinFormsUI.Pages;

/// <summary>子界面：运行日志中心，聚合连接/工程事件与 Skill 审计流。</summary>
public sealed class PageLogView : UserControl, ISubPageView
{
    private const int MaxEntries = 5000;
    private const int MaxRows = 2000;

    private sealed record LogEntry(DateTime Time, string Kind, string Source, string Text, bool IsError);

    private readonly AppHost _host;
    private readonly DataGridView _grid;
    private readonly RoundedSelect _cmbKind = new();
    private readonly RoundedTextBox _txtFilter;
    private readonly FlatCheckBox _chkAutoScroll = new("自动滚动", true);
    private readonly List<LogEntry> _all = new();
    private readonly ConcurrentQueue<LogEntry> _pending = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 300 };

    public PageLogView(AppHost host)
    {
        _host = host;
        Dock = DockStyle.Fill;
        BackColor = DarkGeekTheme.PageBack;
        Padding = new Padding(16, 4, 16, 12);

        _grid = Ui.Grid(
            ("时间", "time", 14, true),
            ("类别", "kind", 10, true),
            ("来源", "source", 18, true),
            ("内容", "text", 58, true));
        _grid.RowTemplate.Height = 24;

        _txtFilter = Ui.Input("", 220);
        _txtFilter.PlaceholderText = "按关键字过滤…";
        _txtFilter.TextChanged += (_, _) => RebuildGrid();

        _cmbKind.AddRange("全部类别", "运行与连接", "操作审计");
        _cmbKind.SelectedIndex = 0;
        _cmbKind.Width = 130;
        _cmbKind.SelectedIndexChanged += (_, _) => RebuildGrid();

        _chkAutoScroll.AutoSize = true;
        _chkAutoScroll.Margin = new Padding(4, 8, 8, 0);

        // 不显示卡片标题行，顶部留白收窄
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 10, 16, 14),
        };

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 42,
            WrapContents = false,
            BackColor = Color.Transparent,
        };
        toolbar.Controls.Add(_cmbKind);
        toolbar.Controls.Add(_txtFilter);
        toolbar.Controls.Add(_chkAutoScroll);
        toolbar.Controls.Add(Ui.Button("清空", ButtonKind.Ghost, (_, _) => ClearLog(), 72));
        toolbar.Controls.Add(Ui.Button("导出…", ButtonKind.Ghost, (_, _) => ExportLog(), 84));

        card.Controls.Add(Ui.WrapDisplay(_grid));
        card.Controls.Add(toolbar);
        Controls.Add(card);

        Ctx.Device.Log += OnDeviceLog;
        Ctx.SessionLog += OnDeviceLog;
        Ctx.Audit.EntryAdded += OnAuditEntry;

        _timer.Tick += (_, _) => Flush();
        _timer.Start();
    }

    private Core.Services.StudioContext Ctx => _host.Context;

    public string PageKey => "log";
    public string PageTitle => "📋 运行日志";

    public void OnPageActivated()
    {
        RebuildGrid();
        _timer.Start();
    }

    public void OnPageHidden() { }

    private void OnDeviceLog(object? sender, string message) => Enqueue(new LogEntry(
        DateTime.Now, "连接", "device", message, IsErrorText(message)));

    private void OnAuditEntry(object? sender, SkillAuditEntry entry) => Enqueue(new LogEntry(
        DateTimeOffset.FromUnixTimeMilliseconds(entry.TimestampMs).LocalDateTime,
        "操作",
        entry.Source,
        $"{entry.Method} {entry.Path} · {entry.Detail}",
        !entry.Success));

    private static bool IsErrorText(string text) =>
        text.Contains("失败", StringComparison.Ordinal) ||
        text.Contains("异常", StringComparison.Ordinal) ||
        text.Contains("错误", StringComparison.Ordinal);

    private void Enqueue(LogEntry entry) => _pending.Enqueue(entry);

    private void Flush()
    {
        if (_pending.IsEmpty) return;

        bool trimmed = false;
        var fresh = new List<LogEntry>();
        while (_pending.TryDequeue(out var entry))
        {
            _all.Add(entry);
            fresh.Add(entry);
        }

        while (_all.Count > MaxEntries)
        {
            _all.RemoveAt(0);
            trimmed = true;
        }

        if (!Visible || IsDisposed) return;

        if (trimmed)
        {
            RebuildGrid();
            return;
        }

        AddingRows = true;
        try
        {
            foreach (var entry in fresh)
            {
                if (Matches(entry)) AppendRow(entry);
            }
        }
        finally
        {
            AddingRows = false;
        }

        ScrollToEnd();
    }

    private bool AddingRows { get; set; }

    private bool Matches(LogEntry entry)
    {
        if (_cmbKind.SelectedIndex == 1 && entry.Kind != "连接") return false;
        if (_cmbKind.SelectedIndex == 2 && entry.Kind != "操作") return false;

        var keyword = _txtFilter.Text.Trim();
        if (keyword.Length == 0) return true;
        return entry.Text.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               entry.Source.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private void AppendRow(LogEntry entry)
    {
        int index = _grid.Rows.Add(entry.Time.ToString("HH:mm:ss.fff"), entry.Kind, entry.Source, entry.Text);
        var row = _grid.Rows[index];
        if (entry.IsError) row.DefaultCellStyle.ForeColor = DarkGeekTheme.Danger;
    }

    private void ScrollToEnd()
    {
        if (!_chkAutoScroll.Checked || _grid.Rows.Count == 0) return;
        try
        {
            _grid.FirstDisplayedScrollingRowIndex = _grid.Rows.Count - 1;
        }
        catch
        {
            // 行尚未完成布局时忽略
        }
    }

    private void RebuildGrid()
    {
        if (IsDisposed) return;
        if (AddingRows) return;

        _grid.SuspendLayout();
        try
        {
            _grid.Rows.Clear();
            var matched = _all.Where(Matches).ToList();
            foreach (var entry in matched.Skip(Math.Max(0, matched.Count - MaxRows)))
                AppendRow(entry);
        }
        finally
        {
            _grid.ResumeLayout();
        }

        ScrollToEnd();
    }

    private void ClearLog()
    {
        _all.Clear();
        while (_pending.TryDequeue(out _)) { }
        _grid.Rows.Clear();
        Ctx.Audit.Add("ui", "CLEAR", "/log", "界面日志已清空");
    }

    private void ExportLog()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "文本文件|*.txt|所有文件|*.*",
            FileName = $"jlink-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var builder = new StringBuilder();
        foreach (var entry in _all.Where(Matches))
        {
            builder.Append(entry.Time.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append('\t')
                .Append(entry.Kind).Append('\t')
                .Append(entry.Source).Append('\t')
                .AppendLine(entry.Text.Replace('\n', ' '));
        }

        try
        {
            File.WriteAllText(dialog.FileName, builder.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导出失败：{ex.Message}", "运行日志", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
            Ctx.Device.Log -= OnDeviceLog;
            Ctx.SessionLog -= OnDeviceLog;
            Ctx.Audit.EntryAdded -= OnAuditEntry;
        }
        base.Dispose(disposing);
    }
}
