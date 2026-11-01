using System.Text;
using JLinkStudio.Core.Models;
using JLinkStudio.Core.Services;
using JLinkStudio.WinFormsUI.Controls;
using JLinkStudio.WinFormsUI.Navigation;
using JLinkStudio.WinFormsUI.Theme;

namespace JLinkStudio.WinFormsUI.Pages;

/// <summary>子界面 2：RTT 极速终端（彩色日志、级别过滤、正则搜索、指令下发）。</summary>
public sealed class PageRttView : UserControl, ISubPageView
{
    private const int MaxTerminalChars = 400_000;

    // 工具栏行与行内控件尺寸：两行等高（42），控件统一 32 高 + 顶部 8 偏移，保证底部对齐
    private const int ToolbarRowHeight = 42;
    private const int RowItemHeight = 32;
    private const int RowItemTop = 8;

    private readonly AppHost _host;
    private readonly RichTextBox _terminal = new();
    private readonly RoundedTextBox _search = new();
    private readonly RoundedTextBox _sendBox = new();
    private readonly RoundedSelect _cmbSource = new();
    private readonly RoundedSelect _cmbChannel = new();
    // —— 各来源的动态配置控件 ——
    private readonly RoundedSelect _cmbCom = new();
    private readonly RoundedSelect _cmbBaud = new();
    private readonly RoundedSelect _cmbDataBits = new();
    private readonly RoundedSelect _cmbStopBits = new();
    private readonly RoundedSelect _cmbParity = new();
    private readonly RoundedSelect _cmbCom485 = new();
    private readonly RoundedSelect _cmbBaud485 = new();
    private readonly RoundedTextBox _txtAddr485 = new();
    private readonly RoundedTextBox _txtIp = new();
    private readonly RoundedTextBox _txtPort = new();
    private readonly RoundedTextBox _txtRttBlock = new();
    private readonly FlatCheckBox _chkPause = new();
    private readonly FlatCheckBox _chkHex = new();
    private readonly FlatCheckBox _chkAutoScroll = new();
    private CardPanel _card = null!;
    private readonly StatusPill _pillState = new();
    private readonly System.Windows.Forms.Timer _flushTimer = new() { Interval = 30 };

    private readonly List<RttLine> _pending = new();
    private readonly object _pendingLock = new();
    private readonly Dictionary<string, FlatButton> _levelButtons = new();
    // 来源配置分组（切换来源时切换可见性）
    private FlowLayoutPanel _panelSerial = null!;
    private FlowLayoutPanel _panelRs485 = null!;
    private FlowLayoutPanel _panelNet = null!;
    private FlowLayoutPanel _panelRtt = null!;

    private int _channel;
    private RttLevel? _levelFilter;
    private bool _activated;
    private long _droppedLines;

    public PageRttView(AppHost host)
    {
        _host = host;
        Dock = DockStyle.Fill;
        BackColor = DarkGeekTheme.PageBack;
        Padding = new Padding(16, 4, 16, 12);

        // 不显示卡片标题行，顶部留白收窄（缓冲/丢弃等统计移到底部状态胶囊）
        _card = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(16, 10, 16, 14) };
        var card = _card;
        var body = new Panel { Dock = DockStyle.Fill, BackColor = DarkGeekTheme.CardBack };

        BuildToolbar(out var toolbar);
        BuildTerminal();
        BuildSendBar(out var sendBar);

        var terminalFrame = Ui.WrapDisplay(_terminal, DarkGeekTheme.TerminalBack, 8);
        terminalFrame.Margin = new Padding(0);

        body.Controls.Add(terminalFrame);
        body.Controls.Add(sendBar);
        body.Controls.Add(toolbar);

        card.Controls.Add(body);
        Controls.Add(card);

        Ctx.Rtt.LinesReceived += OnLinesReceived;
        _flushTimer.Tick += (_, _) => FlushPending();
        _flushTimer.Start();

        _channel = 0;
        UpdateLevelButtons();
    }

    private StudioContext Ctx => _host.Context;

    public string PageKey => "rtt";
    public string PageTitle => "💻 打印";

    public void OnPageActivated()
    {
        _activated = true;
        RenderChannelHistory();
    }

    public void OnPageHidden()
    {
        _activated = false;
    }

    private void BuildToolbar(out Panel toolbar)
    {
        toolbar = new Panel { Dock = DockStyle.Top, Height = ToolbarRowHeight * 2, BackColor = DarkGeekTheme.CardBack };

        var row1 = CreateToolbarRow();
        var row2 = CreateToolbarRow();

        // ── 第一行：打印来源 + 来源配置（配置项紧随下拉框后）──
        row1.Controls.Add(RowLabel("来源"));
        _cmbSource.AddRange("串口", "485", "USB", "网口", "RTT");
        _cmbSource.SelectedIndex = 0;
        _cmbSource.SelectedIndexChanged += (_, _) =>
        {
            if (_cmbSource.SelectedIndex < 0) return;
            string source = _cmbSource.Items[_cmbSource.SelectedIndex];
            Ctx.Audit.Add("ui", "PRINT-SOURCE", "/print/source", $"打印来源切换为 {source}");
            UpdateSourceConfig();
        };
        row1.Controls.Add(RowItem(_cmbSource, 96));

        InitSourceConfigs();
        _panelSerial = BuildConfigGroup(("COM", _cmbCom, 90), ("波特率", _cmbBaud, 104),
            ("数据位", _cmbDataBits, 64), ("停止位", _cmbStopBits, 64), ("校验", _cmbParity, 84));
        _panelRs485 = BuildConfigGroup(("COM", _cmbCom485, 90), ("波特率", _cmbBaud485, 104), ("从站地址", _txtAddr485, 84));
        _panelNet = BuildConfigGroup(("IP", _txtIp, 140), ("端口", _txtPort, 80));
        _panelRtt = BuildConfigGroup(("通道", _cmbChannel, 96), ("控制块", _txtRttBlock, 120));

        // 启动 / 停止 RTT 仅在选择「RTT」来源时可见
        _panelRtt.Controls.Add(RowItem(Ui.Button("启动 RTT", ButtonKind.Primary, (_, _) =>
        {
            bool ok = Ctx.Rtt.Start(out var error);
            AppendSystemLine(ok ? "RTT 采集已启动。" : $"RTT 启动失败：{error}");
        }, 90), 90));
        _panelRtt.Controls.Add(RowItem(Ui.Button("停止", ButtonKind.Ghost, (_, _) =>
        {
            Ctx.Rtt.Stop();
            AppendSystemLine("RTT 采集已停止。");
        }, 64), 64));

        row1.Controls.Add(_panelSerial);
        row1.Controls.Add(_panelRs485);
        row1.Controls.Add(_panelNet);
        row1.Controls.Add(_panelRtt);
        UpdateSourceConfig();

        // ── 第二行：级别 / 过滤 / 滚动 / 导出 ──
        row2.Controls.Add(RowLabel("级别"));
        foreach (var level in new[] { "ALL", "INFO", "WARN", "ERROR" })
        {
            string key = level;
            var button = Ui.Button(level, ButtonKind.Ghost, (_, _) =>
            {
                _levelFilter = key switch
                {
                    "INFO" => RttLevel.Info,
                    "WARN" => RttLevel.Warn,
                    "ERROR" => RttLevel.Error,
                    _ => null,
                };
                UpdateLevelButtons();
                RenderChannelHistory();
            }, 64);
            _levelButtons[key] = button;
            row2.Controls.Add(RowItem(button));
        }

        _search.PlaceholderText = "正则过滤 / 高亮关键字";
        _search.TextChanged += (_, _) => RenderChannelHistory();
        row2.Controls.Add(RowItem(_search, 220));

        _chkPause.Text = "暂停滚屏";
        DarkGeekTheme.StyleCheck(_chkPause);
        row2.Controls.Add(RowItem(_chkPause));

        _chkAutoScroll.Text = "自动滚动";
        _chkAutoScroll.Checked = true;
        DarkGeekTheme.StyleCheck(_chkAutoScroll);
        row2.Controls.Add(RowItem(_chkAutoScroll));

        row2.Controls.Add(RowItem(Ui.Button("清空", ButtonKind.Ghost, (_, _) =>
        {
            Ctx.Rtt.Clear(_channel);
            _terminal.Clear();
        }, 64)));
        row2.Controls.Add(RowItem(Ui.Button("导出 .log", ButtonKind.Ghost, (_, _) => ExportLog(), 92)));
        row2.Controls.Add(RowItem(Ui.Button("🧹 清屏", ButtonKind.Ghost, (_, _) => _terminal.Clear(), 76)));

        toolbar.Controls.Add(row2);
        toolbar.Controls.Add(row1);
    }

    /// <summary>初始化各来源的下拉项与默认值。</summary>
    private void InitSourceConfigs()
    {
        // RTT 通道（复用第一行原有通道选择逻辑）
        _cmbChannel.AddRange("Ch0", "Ch1", "Ch2");
        _cmbChannel.SelectedIndex = 0;
        _cmbChannel.SelectedIndexChanged += (_, _) =>
        {
            if (_cmbChannel.SelectedIndex < 0) return;
            _channel = _cmbChannel.SelectedIndex;
            RenderChannelHistory();
        };

        // 串口
        _cmbCom.AddRange("COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8");
        _cmbBaud.AddRange("9600", "19200", "38400", "57600", "115200", "230400", "460800", "921600");
        _cmbBaud.SelectedIndex = 4; // 115200
        _cmbDataBits.AddRange("5", "6", "7", "8");
        _cmbDataBits.SelectedIndex = 3; // 8
        _cmbStopBits.AddRange("1", "1.5", "2");
        _cmbStopBits.SelectedIndex = 0;
        _cmbParity.AddRange("无", "奇校验", "偶校验");
        _cmbParity.SelectedIndex = 0;

        // 485
        _cmbCom485.AddRange("COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8");
        _cmbBaud485.AddRange("9600", "19200", "38400", "57600", "115200", "230400", "460800", "921600");
        _cmbBaud485.SelectedIndex = 4;
        _txtAddr485.Text = "1";

        // 网口
        _txtIp.Text = "192.168.1.100";
        _txtPort.Text = "502";

        // RTT 控制块
        _txtRttBlock.Text = "0x20001000";
    }

    /// <summary>根据当前「来源」显示对应的配置分组（USB 免配置，全部隐藏）。</summary>
    private void UpdateSourceConfig()
    {
        string source = _cmbSource.SelectedItem ?? string.Empty;
        _panelSerial.Visible = source == "串口";
        _panelRs485.Visible = source == "485";
        _panelNet.Visible = source == "网口";
        _panelRtt.Visible = source == "RTT";
    }

    /// <summary>构建一个来源配置分组：标签 + 控件水平排列。</summary>
    private static FlowLayoutPanel BuildConfigGroup(params (string Label, Control Control, int Width)[] items)
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        foreach (var (text, control, width) in items)
        {
            panel.Controls.Add(RowLabel(text));
            panel.Controls.Add(RowItem(control, width));
        }
        return panel;
    }

    /// <summary>工具栏行：固定 42px，两行等高。</summary>
    private static FlowLayoutPanel CreateToolbarRow()
    {
        return new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = ToolbarRowHeight,
            AutoSize = false,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
    }

    /// <summary>行内标签：宽度按文字实测，避免中文被裁切；高度与按钮底部对齐。</summary>
    private static Label RowLabel(string text)
    {
        var label = Ui.Text(text, true, size: 9f);
        label.AutoSize = false;
        label.AutoEllipsis = false;
        // 固定宽度会裁掉第二个汉字，这里按实际文字宽度测量后再留 10px 余量
        label.Width = TextRenderer.MeasureText(text, label.Font).Width + 10;
        label.Height = RowItemHeight;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.Margin = new Padding(0, RowItemTop, 6, 0);
        return label;
    }

    /// <summary>行内控件：统一高度与上边距，保证每行底部对齐。</summary>
    private static Control RowItem(Control control, int width = 0)
    {
        if (width > 0) control.Width = width;
        control.Height = RowItemHeight;
        control.Margin = new Padding(0, RowItemTop, 8, 0);
        return control;
    }

    private void BuildTerminal()
    {
        _terminal.Dock = DockStyle.Fill;
        _terminal.BackColor = DarkGeekTheme.TerminalBack;
        _terminal.ForeColor = DarkGeekTheme.TextPrimary;
        _terminal.Font = DarkGeekTheme.MonoFont;
        _terminal.BorderStyle = BorderStyle.None;
        _terminal.ReadOnly = true;
        _terminal.WordWrap = false;
        _terminal.HideSelection = false;
        _terminal.DetectUrls = false;
        _terminal.ScrollBars = RichTextBoxScrollBars.Both;
        _terminal.Margin = new Padding(0);
    }

    private void BuildSendBar(out Panel bar)
    {
        bar = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = DarkGeekTheme.CardBack, Padding = new Padding(0, 8, 0, 0) };

        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
        };

        _sendBox.Width = 320;
        _sendBox.PlaceholderText = "输入指令后回车下发到目标板…";
        _sendBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                SendCommand();
            }
        };
        row.Controls.Add(_sendBox);

        _chkHex.Text = "HEX";
        _chkHex.AutoSize = true;
        DarkGeekTheme.StyleCheck(_chkHex);
        _chkHex.Margin = new Padding(0, 8, 10, 0);
        row.Controls.Add(_chkHex);

        row.Controls.Add(Ui.Button("发送", ButtonKind.Primary, (_, _) => SendCommand(), 72));

        row.Controls.Add(Ui.Text("快捷宏：", false, DarkGeekTheme.TextMuted, 8.75f));
        row.Controls.Add(Ui.Button("软复位", ButtonKind.Ghost, (_, _) => QuickSend("reboot"), 72));
        row.Controls.Add(Ui.Button("触发故障", ButtonKind.Ghost, (_, _) => QuickSend("fault"), 84));
        row.Controls.Add(Ui.Button("转速=1000", ButtonKind.Ghost, (_, _) => QuickSend("set_target=1000"), 92));

        _pillState.SetState("采集未启动", DarkGeekTheme.TextMuted);
        _pillState.Margin = new Padding(10, 2, 0, 0);
        row.Controls.Add(_pillState);

        bar.Controls.Add(row);
    }

    private void SendCommand()
    {
        var text = _sendBox.Text.Trim();
        if (text.Length == 0) return;

        if (BlockedByInspectMode()) return;

        bool ok = _chkHex.Checked ? Ctx.Rtt.SendHex(text, _channel) : Ctx.Rtt.Send(text, _channel);
        AppendSystemLine(ok ? $">>> {text}" : "发送失败：目标未连接。");
        if (ok) _sendBox.Text = string.Empty;
    }

    private void QuickSend(string text)
    {
        if (BlockedByInspectMode()) return;

        bool ok = Ctx.Rtt.Send(text, _channel);
        AppendSystemLine(ok ? $">>> {text}" : "发送失败：目标未连接。");
    }

    /// <summary>查看模式下拦截向目标板下发指令，避免误操作改变现场。</summary>
    private bool BlockedByInspectMode()
    {
        if (!_host.IsInspectMode) return false;
        AppendSystemLine("查看模式：已拦截向目标板下发的指令（可在「设置 → 调试和查看模式」切回调试模式）。");
        return true;
    }

    private void OnLinesReceived(object? sender, List<RttLine> lines)
    {
        lock (_pendingLock)
        {
            _pending.AddRange(lines);
            if (_pending.Count > 20000)
            {
                _droppedLines += _pending.Count - 10000;
                _pending.RemoveRange(0, _pending.Count - 10000);
            }
        }
    }

    private void FlushPending()
    {
        if (!_activated || IsDisposed || !IsHandleCreated) return;
        if (_chkPause.Checked) return;

        List<RttLine> batch;
        lock (_pendingLock)
        {
            if (_pending.Count == 0)
            {
                UpdateStats();
                return;
            }
            batch = new List<RttLine>(_pending);
            _pending.Clear();
        }

        var matched = batch.Where(l => l.Channel == _channel && MatchFilter(l)).ToList();
        if (matched.Count > 0) AppendLines(matched);
        UpdateStats();
    }

    private bool MatchFilter(RttLine line)
    {
        if (_levelFilter.HasValue && line.Level < _levelFilter.Value) return false;

        var search = _search.Text.Trim();
        if (search.Length == 0) return true;

        try
        {
            return System.Text.RegularExpressions.Regex.IsMatch(line.Text, search,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        catch (ArgumentException)
        {
            return line.Text.Contains(search, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void AppendLines(List<RttLine> lines)
    {
        _terminal.SuspendLayout();
        try
        {
            if (_terminal.TextLength > MaxTerminalChars)
            {
                _terminal.Clear();
                AppendSystemLine("（日志过多，已自动截断最旧内容）");
            }

            foreach (var line in lines)
            {
                AppendSegment($"[{line.TimeText}] ", DarkGeekTheme.TextSecondary);
                AppendSegment($"[Ch{line.Channel}] ", DarkGeekTheme.TextSecondary);
                AppendSegment(line.Text + "\n", LevelColor(line.Level));
            }

            if (_chkAutoScroll.Checked)
            {
                _terminal.SelectionStart = _terminal.TextLength;
                _terminal.ScrollToCaret();
            }
        }
        finally
        {
            _terminal.ResumeLayout();
        }
    }

    private void AppendSegment(string text, Color color)
    {
        _terminal.SelectionStart = _terminal.TextLength;
        _terminal.SelectionLength = 0;
        _terminal.SelectionColor = color;
        _terminal.AppendText(text);
        _terminal.SelectionColor = _terminal.ForeColor;
    }

    private void AppendSystemLine(string text)
    {
        AppendSegment($"[{DateTime.Now:HH:mm:ss.fff}] ", DarkGeekTheme.TextMuted);
        AppendSegment($"[HOST] {text}\n", DarkGeekTheme.Primary);
        if (_chkAutoScroll.Checked)
        {
            _terminal.SelectionStart = _terminal.TextLength;
            _terminal.ScrollToCaret();
        }
    }

    private static Color LevelColor(RttLevel level) => level switch
    {
        RttLevel.Error => DarkGeekTheme.Danger,
        RttLevel.Warn => DarkGeekTheme.Warning,
        RttLevel.Trace => DarkGeekTheme.TextMuted,
        _ => DarkGeekTheme.TextPrimary,
    };

    private void RenderChannelHistory()
    {
        _terminal.Clear();
        var lines = Ctx.Rtt.Query(_channel, 300).Where(MatchFilter).ToList();
        AppendLines(lines);
        AppendSystemLine($"已切换至通道 {_channel}，显示最近 {lines.Count} 行。");
    }

    private void UpdateLevelButtons()
    {
        var active = _levelFilter switch
        {
            RttLevel.Info => "INFO",
            RttLevel.Warn => "WARN",
            RttLevel.Error => "ERROR",
            _ => "ALL",
        };

        foreach (var (key, button) in _levelButtons)
        {
            button.Kind = key == active ? ButtonKind.Primary : ButtonKind.Ghost;
            button.Invalidate();
        }
    }

    private void UpdateStats()
    {
        if (!_activated) return;
        int buffered;
        lock (_pendingLock) buffered = _pending.Count;

        string bufferInfo = buffered > 0 ? $" · 缓冲 {buffered} 行" : string.Empty;
        if (_droppedLines > 0) bufferInfo += $" · 丢弃 {_droppedLines}";

        if (Ctx.Rtt.IsRunning)
            _pillState.SetState($"采集中 · 控制块 {(Ctx.Rtt.ControlBlockAddress is uint a ? $"0x{a:X8}" : "-")}{bufferInfo}", DarkGeekTheme.Success);
        else if (Ctx.Device.IsConnected)
            _pillState.SetState("已连接 · RTT 未启动", DarkGeekTheme.Warning);
        else
            _pillState.SetState("未连接", DarkGeekTheme.TextMuted);
    }

    private void ExportLog()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "日志文件|*.log|文本文件|*.txt",
            FileName = $"rtt-ch{_channel}-{DateTime.Now:yyyyMMdd-HHmmss}.log",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var sb = new StringBuilder();
        foreach (var line in Ctx.Rtt.Query(_channel, 5000))
            sb.AppendLine($"[{line.TimeText}] [Ch{line.Channel}] [{line.Level}] {line.Text}");

        File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8);
        AppendSystemLine($"日志已导出：{dialog.FileName}");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Ctx.Rtt.LinesReceived -= OnLinesReceived;
            _flushTimer.Stop();
            _flushTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
