using JLinkStudio.Core.Models;
using JLinkStudio.Core.Services;
using JLinkStudio.WinFormsUI.Controls;
using JLinkStudio.WinFormsUI.Navigation;
using JLinkStudio.WinFormsUI.Theme;

namespace JLinkStudio.WinFormsUI.Pages;

/// <summary>子界面 1：设备连接与工程文件挂载配置。</summary>
public sealed class PageDeviceConfig : UserControl, ISubPageView
{
    internal static readonly (string Name, int Khz)[] SpeedPresets =
    {
        ("100 kHz", 100), ("500 kHz", 500), ("1 MHz", 1000), ("2 MHz", 2000),
        ("4 MHz", 4000), ("8 MHz", 8000), ("12 MHz", 12000), ("20 MHz", 20000), ("50 MHz", 50000),
    };

    private readonly AppHost _host;
    private readonly StatusPill _pillStatus = new();
    private readonly RoundedSelect _cmbSerial = new();
    private readonly RoundedTextBox _txtDevice;
    private readonly RoundedSelect _cmbInterface = new();
    private readonly RoundedSelect _cmbSpeed = new();
    private readonly RoundedTextBox _txtElf;
    private readonly RoundedTextBox _txtKeilPath;
    private readonly RoundedTextBox _txtUvproj;
    private readonly RoundedSelect _cmbRttMode = new();
    private readonly RoundedSelect _cmbSection = new();
    private readonly RoundedTextBox _txtRttAddress;
    private readonly FlatCheckBox _chkAutoReload;
    private readonly RichTextBox _log = new();
    private readonly Label _lblSummary = new();

    private FlatButton _btnConnect = null!;
    private FlatButton _btnDisconnect = null!;
    private CardPanel _connectionCard = null!;
    private CardPanel _fileCard = null!;

    public PageDeviceConfig(AppHost host)
    {
        _host = host;
        Dock = DockStyle.Fill;
        BackColor = Color.Transparent;
        Padding = new Padding(0, 0, 0, 0);

        _txtDevice = Ui.Input("STM32H743VI", 200);
        _txtElf = Ui.Input("", 320);
        _txtElf.PlaceholderText = "拖入或选择 .elf / .axf 固件";

        // Keil 工具链：安装目录默认 D:\Keil_V5，工程文件用于命令行编译 / 下载
        _txtKeilPath = Ui.Input(KeilToolchain.DefaultInstallPath, 300);
        _txtKeilPath.PlaceholderText = @"D:\Keil_V5";
        _txtUvproj = Ui.Input("", 300);
        _txtUvproj.PlaceholderText = "选择 .uvprojx / .uvproj 工程文件";
        _txtRttAddress = Ui.Input("0x20001000", 110);
        _chkAutoReload = Ui.Check("自动重载 ELF", true);
        _chkAutoReload.AutoSize = true;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // 两个分区合并为一张卡片，通过下拉选项切换显示
        var rowSection = Ui.Row(0, 6);
        rowSection.Controls.Add(Ui.Text("配置分区", true, size: 9f));
        _cmbSection.Width = 170;
        rowSection.Controls.Add(_cmbSection);
        layout.Controls.Add(rowSection, 0, 0);

        _connectionCard = BuildConnectionCard(_chkAutoReload);
        _fileCard = BuildFileCard();
        _connectionCard.Margin = Padding.Empty;
        _fileCard.Margin = Padding.Empty;

        var cardHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        cardHost.Controls.Add(_fileCard);
        cardHost.Controls.Add(_connectionCard);
        layout.Controls.Add(cardHost, 0, 1);

        _cmbSection.AddRange("J-Link 硬件连接", "调试符号与文件挂载");
        _cmbSection.SelectedIndexChanged += (_, _) => SwitchSection();
        _cmbSection.SelectedIndex = 0;

        // 本页嵌入在设置页的"唯一卡片"内部，因此不再套外卡片，只保留分区
        Controls.Add(layout);

        Ctx.Device.Log += OnDeviceLog;
        Ctx.SessionLog += OnDeviceLog;
        Ctx.Device.StateChanged += OnDeviceStateChanged;

        _cmbSerial.Width = 150;
        _cmbSerial.Items.Add("自动检测");
        _cmbSerial.SelectedIndex = 0;

        _cmbInterface.AddRange("SWD", "JTAG");
        _cmbInterface.Width = 120;
        _cmbInterface.SelectedIndex = 0;

        _cmbSpeed.AddRange(SpeedPresets.Select(p => p.Name).ToArray());
        _cmbSpeed.Width = 130;
        _cmbSpeed.SelectedIndex = 4;

        _cmbRttMode.AddRange("从 ELF 自动", "全内存扫描", "手动指定");
        _cmbRttMode.Width = 132;
        _cmbRttMode.SelectedIndex = 0;
        _cmbRttMode.SelectedIndexChanged += (_, _) => UpdateRttAddressState();

        _txtRttAddress.Enabled = false;
        AllowDrop = true;
        DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
        };
        DragDrop += OnFileDrop;
    }

    private StudioContext Ctx => _host.Context;

    public string PageKey => "config";
    public string PageTitle => "🔌 设备与工程配置";

    public void OnPageActivated() => RefreshFromContext();
    public void OnPageHidden() { }

    /// <summary>下拉选项切换「硬件连接」与「符号挂载」两个分区的可见性。</summary>
    private void SwitchSection()
    {
        bool showConnection = _cmbSection.SelectedIndex != 1;
        _connectionCard.Visible = showConnection;
        _fileCard.Visible = !showConnection;
    }

    private CardPanel BuildConnectionCard(FlatCheckBox chkAutoReload)
    {
        var card = Ui.Section("J-Link 硬件连接", "SEGGER J-Link / J-Trace");
        var stack = Ui.Stack();

        var rowSerial = Ui.Row();
        rowSerial.Controls.Add(Ui.Text("序列号", true, size: 9f));
        rowSerial.Controls.Add(_cmbSerial);
        var btnRefresh = Ui.Button("刷新", ButtonKind.Ghost, (_, _) => RefreshEmulators(), 60);
        rowSerial.Controls.Add(btnRefresh);
        stack.Controls.Add(rowSerial);

        var rowDevice = Ui.Row(8);
        rowDevice.Controls.Add(Ui.Text("目标芯片", true, size: 9f));
        _txtDevice.Width = 250;
        rowDevice.Controls.Add(_txtDevice);
        stack.Controls.Add(rowDevice);

        var rowInterface = Ui.Row(8);
        rowInterface.Controls.Add(Ui.Text("调试接口", true, size: 9f));
        rowInterface.Controls.Add(_cmbInterface);
        rowInterface.Controls.Add(Ui.Text("通信速率", true, size: 9f));
        rowInterface.Controls.Add(_cmbSpeed);
        stack.Controls.Add(rowInterface);

        var rowOptions = Ui.Row(10);
        rowOptions.Controls.Add(chkAutoReload);
        stack.Controls.Add(rowOptions);

        var rowButtons = Ui.Row(12);
        _btnConnect = Ui.Button("连接目标", ButtonKind.Primary, async (_, _) => await ConnectAsync(), 110);
        _btnDisconnect = Ui.Button("断开", ButtonKind.Ghost, (_, _) =>
        {
            Ctx.StopSession();
            RefreshStatus();
        }, 80);
        rowButtons.Controls.Add(_btnConnect);
        rowButtons.Controls.Add(_btnDisconnect);
        stack.Controls.Add(rowButtons);

        var rowControl = Ui.Row(4);
        rowControl.Controls.Add(Ui.Button("复位", ButtonKind.Ghost, (_, _) => RunAction("复位", l => l.Reset(out _)), 70));
        rowControl.Controls.Add(Ui.Button("暂停 CPU", ButtonKind.Ghost, (_, _) => RunAction("暂停", l => l.Halt(out _)), 86));
        rowControl.Controls.Add(Ui.Button("运行", ButtonKind.Ghost, (_, _) => RunAction("运行", l => l.Go(out _)), 70));
        stack.Controls.Add(rowControl);

        var rowStatus = Ui.Row(14);
        _pillStatus.SetState("未连接", DarkGeekTheme.TextMuted);
        rowStatus.Controls.Add(_pillStatus);
        stack.Controls.Add(rowStatus);

        _lblSummary.AutoSize = false;
        _lblSummary.Width = 330;
        _lblSummary.Height = 100;
        _lblSummary.ForeColor = DarkGeekTheme.TextMuted;
        _lblSummary.Font = DarkGeekTheme.UiFontSmall;
        _lblSummary.Text = "提示：连接前请插好 J-Link 调试器并给目标板上电，\r\n首次连接建议先加载 .elf 以自动定位 RTT 控制块。";
        stack.Controls.Add(_lblSummary);

        card.Controls.Add(stack);
        return card;
    }

    private CardPanel BuildFileCard()
    {
        var card = Ui.Section("调试符号与文件挂载", "ELF / DWARF · 寄存器数据来自内置手册表");
        var stack = Ui.Stack();

        var rowElf = Ui.Row();
        rowElf.Controls.Add(Ui.Text("固件符号 (.elf)", true, size: 9f));
        rowElf.Controls.Add(_txtElf);
        rowElf.Controls.Add(Ui.Button("浏览", ButtonKind.Ghost, (_, _) => BrowseFile(_txtElf, "ELF 固件|*.elf;*.axf|所有文件|*.*"), 64));
        stack.Controls.Add(rowElf);

        var elfHint = Ui.Text("DWARF 信息可解析结构体 / 数组成员，双击变量值可在线改写", false, DarkGeekTheme.TextMuted, 8.5f);
        elfHint.Margin = new Padding(0, 0, 0, 8);
        stack.Controls.Add(elfHint);

        var rowRtt = Ui.Row(10);
        rowRtt.Controls.Add(Ui.Text("RTT 控制块", true, size: 9f));
        rowRtt.Controls.Add(_cmbRttMode);
        rowRtt.Controls.Add(_txtRttAddress);
        stack.Controls.Add(rowRtt);

        var rowButtons = Ui.Row(12);
        rowButtons.Controls.Add(Ui.Button("加载 ELF", ButtonKind.Primary, (_, _) => LoadElf(), 100));
        rowButtons.Controls.Add(Ui.Button("编译", ButtonKind.Ghost, async (_, _) => await RunKeilAsync("build"), 70));
        rowButtons.Controls.Add(Ui.Button("下载", ButtonKind.Ghost, async (_, _) => await RunKeilAsync("flash"), 70));
        rowButtons.Controls.Add(Ui.Button("编译并下载", ButtonKind.Ghost,
            async (_, _) => await RunKeilAsync("build-flash"), 100));
        stack.Controls.Add(rowButtons);

        // ── Keil 工具链：安装目录（默认 D:\Keil_V5）+ 工程文件 ──
        var rowKeil = Ui.Row(10);
        rowKeil.Controls.Add(Ui.Text("Keil 目录", true, size: 9f));
        rowKeil.Controls.Add(_txtKeilPath);
        rowKeil.Controls.Add(Ui.Button("浏览", ButtonKind.Ghost, (_, _) => BrowseFolder(_txtKeilPath), 64));
        rowKeil.Controls.Add(Ui.Text("工程", true, size: 9f));
        rowKeil.Controls.Add(_txtUvproj);
        rowKeil.Controls.Add(Ui.Button("浏览", ButtonKind.Ghost,
            (_, _) => BrowseFile(_txtUvproj, "Keil 工程|*.uvprojx;*.uvproj|所有文件|*.*"), 64));
        stack.Controls.Add(rowKeil);

        var keilHint = Ui.Text($"调用 {Path.Combine(KeilToolchain.DefaultInstallPath, "UV4", "UV4.exe")} 命令行编译 / 下载（查看模式下禁止下载）",
            false, DarkGeekTheme.TextMuted, 8.5f);
        keilHint.Margin = new Padding(0, 0, 0, 8);
        stack.Controls.Add(keilHint);

        var logLabel = Ui.Text("运行日志", true, DarkGeekTheme.TextSecondary, 9f);
        logLabel.Margin = new Padding(0, 14, 0, 4);
        stack.Controls.Add(logLabel);

        _log.Dock = DockStyle.Fill;
        _log.Height = 220;
        _log.BackColor = DarkGeekTheme.InputBack;
        _log.ForeColor = DarkGeekTheme.TextSecondary;
        _log.BorderStyle = BorderStyle.FixedSingle;
        _log.Font = DarkGeekTheme.MonoFontSmall;
        _log.ReadOnly = true;
        _log.DetectUrls = false;
        _log.ScrollBars = RichTextBoxScrollBars.Vertical;
        _log.WordWrap = true;
        _log.Text = "";

        var logHost = new RoundedFrame
        {
            Height = 240,
            Dock = DockStyle.Bottom,
            Padding = new Padding(1),
            BackColor = DarkGeekTheme.InputBack,
            Radius = 6,
        };
        logHost.Controls.Add(_log);
        card.Controls.Add(stack);
        card.Controls.Add(logHost);

        return card;
    }

    private async Task ConnectAsync()
    {
        var config = new DeviceSessionConfig
        {
            Device = string.IsNullOrWhiteSpace(_txtDevice.Text) ? "STM32H743VI" : _txtDevice.Text.Trim(),
            Interface = _cmbInterface.SelectedIndex == 1 ? JLinkInterface.JTAG : JLinkInterface.SWD,
            SpeedKhz = SpeedPresets[Math.Max(0, _cmbSpeed.SelectedIndex)].Khz,
            SerialNumber = _cmbSerial.SelectedIndex > 0 ? _cmbSerial.SelectedItem?.ToString() : null,
            ElfPath = string.IsNullOrWhiteSpace(_txtElf.Text) ? null : _txtElf.Text.Trim(),
            RttSearchMode = _cmbRttMode.SelectedIndex switch
            {
                1 => RttSearchMode.AutoScan,
                2 => RttSearchMode.ManualAddress,
                _ => RttSearchMode.AutoFromElf,
            },
        };

        if (config.RttSearchMode == RttSearchMode.ManualAddress &&
            ValueCodec.TryParseInput(_txtRttAddress.Text, DataType.UInt32, out var addr))
            config.RttControlBlockAddress = Convert.ToUInt32(addr);

        // 把界面开关同步给设备管理器（修复原「自动重载 ELF」复选框不起任何作用的问题）
        Ctx.Device.AutoReloadElf = _chkAutoReload.Checked;

        _btnConnect.Enabled = false;
        AppendLog($"正在连接 {config.Device} …");
        bool ok = await Ctx.StartSessionAsync(config);
        _btnConnect.Enabled = true;
        AppendLog(ok ? "连接成功。" : $"连接失败：{Ctx.Device.LastError}");
        RefreshStatus();
    }

    private void RunAction(string action, Func<Core.Interop.IJLinkLink, bool> call)
    {
        // 查看模式承诺只读观察，禁止复位 / 暂停 / 运行等改动目标现场的操作
        if (_host.IsInspectMode)
        {
            AppendLog($"{action}失败：查看模式下禁止改动目标运行状态。");
            return;
        }

        var link = Ctx.Device.Link;
        if (link == null || !link.IsConnected)
        {
            AppendLog($"{action}失败：目标未连接。");
            return;
        }
        bool ok = call(link);
        AppendLog(ok ? $"{action}命令已下发。" : $"{action}失败。");
    }

    private void LoadElf()
    {
        var path = _txtElf.Text.Trim();
        if (path.Length == 0 || !File.Exists(path))
        {
            AppendLog("请先选择有效的 .elf / .axf 文件。");
            return;
        }
        bool ok = Ctx.Device.LoadElf(path);
        AppendLog(ok ? $"ELF 已加载，符号 {Ctx.Device.Symbols.Count} 个。" : $"ELF 加载失败：{Ctx.Device.LastError}");
    }

    private void BrowseFile(RoundedTextBox target, string filter)
    {
        using var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            target.Text = dialog.FileName;
            if (ReferenceEquals(target, _txtElf)) LoadElf();
        }
    }

    /// <summary>选择 Keil 安装目录（默认 D:\Keil_V5）。</summary>
    private void BrowseFolder(RoundedTextBox target)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择 Keil 安装目录（其下的 UV4\\UV4.exe 用于命令行编译与下载）",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };

        string current = target.Text.Trim();
        if (current.Length > 0 && Directory.Exists(current)) dialog.SelectedPath = current;

        if (dialog.ShowDialog(this) == DialogResult.OK)
            target.Text = dialog.SelectedPath;
    }

    /// <summary>调用 Keil 命令行：build（编译）/ flash（下载）/ build-flash（编译后下载）。</summary>
    private async Task RunKeilAsync(string action)
    {
        Ctx.Keil.InstallPath = _txtKeilPath.Text;
        Ctx.Keil.ProjectPath = _txtUvproj.Text;

        if (Ctx.Keil.ProjectPath.Length == 0)
        {
            AppendLog("请先选择 Keil 工程文件（.uvprojx）。");
            return;
        }

        if (action.Contains("flash") && Ctx.IsInspectMode)
        {
            AppendLog("查看模式已启用：禁止下载程序，请先切回调试模式。");
            return;
        }

        AppendLog($"[Keil] {action} 开始：{Path.GetFileName(Ctx.Keil.ProjectPath)}");

        var result = action switch
        {
            "build" => await Ctx.Keil.BuildAsync(),
            "flash" => await Ctx.Keil.FlashAsync(),
            _ => await Ctx.Keil.BuildAndFlashAsync(),
        };

        AppendLog($"[Keil] {result.ExitCodeMeaning}（{result.DurationMs} ms，退出码 {result.ExitCode}）");
        if (result.Output.Length > 0) AppendLog(result.Output);
    }

    private void OnFileDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        var file = files[0];
        if (file.EndsWith(".elf", StringComparison.OrdinalIgnoreCase) ||
            file.EndsWith(".axf", StringComparison.OrdinalIgnoreCase))
        {
            _txtElf.Text = file;
            LoadElf();
        }
    }

    private void RefreshEmulators()
    {
        var link = Ctx.Device.Link;
        var serials = link?.EnumerateEmulators() ?? Array.Empty<string>();
        _cmbSerial.Items.Clear();
        _cmbSerial.Items.Add("自动检测");
        foreach (var serial in serials) _cmbSerial.Items.Add(serial);
        _cmbSerial.SelectedIndex = 0;
        AppendLog(serials.Length > 0 ? $"发现 {serials.Length} 个调试器。" : "未发现 J-Link 调试器，请检查 USB 连接与驱动安装。");
    }

    private void UpdateRttAddressState()
    {
        _txtRttAddress.Enabled = _cmbRttMode.SelectedIndex == 2;
    }

    private void RefreshFromContext()
    {
        var config = Ctx.Device.Config;
        if (!string.IsNullOrWhiteSpace(config.Device)) _txtDevice.Text = config.Device;
        if (!string.IsNullOrWhiteSpace(config.ElfPath)) _txtElf.Text = config.ElfPath!;

        int speedIndex = Array.FindIndex(SpeedPresets, p => p.Khz == config.SpeedKhz);
        if (speedIndex >= 0) _cmbSpeed.SelectedIndex = speedIndex;

        _cmbInterface.SelectedIndex = config.Interface == JLinkInterface.JTAG ? 1 : 0;
        _cmbRttMode.SelectedIndex = config.RttSearchMode switch
        {
            RttSearchMode.AutoScan => 1,
            RttSearchMode.ManualAddress => 2,
            _ => 0,
        };
        if (config.RttControlBlockAddress is uint addr) _txtRttAddress.Text = $"0x{addr:X8}";
        UpdateRttAddressState();
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var device = Ctx.Device;
        _pillStatus.SetState(
            device.State switch
            {
                ConnectionState.Connected => "已连接目标",
                ConnectionState.Connecting => "连接中…",
                ConnectionState.Faulted => "连接异常",
                _ => "未连接",
            },
            device.State switch
            {
                ConnectionState.Connected => DarkGeekTheme.Success,
                ConnectionState.Connecting => DarkGeekTheme.Warning,
                ConnectionState.Faulted => DarkGeekTheme.Danger,
                _ => DarkGeekTheme.TextMuted,
            });

        var driver = Core.Services.DeviceManager.IsDriverAvailable
            ? $"SEGGER J-Link {Core.Services.DeviceManager.DriverVersion} 已就绪"
            : "未找到 J-Link SDK";

        var diagnosis = device.LastDiagnosis;
        if (!device.IsConnected && diagnosis != null)
        {
            _lblSummary.ForeColor = DarkGeekTheme.Danger;
            _lblSummary.Text =
                "连接异常分析：\r\n" +
                string.Join("\r\n", diagnosis.Causes.Take(2).Select(c => "· " + c)) + "\r\n" +
                $"建议：{diagnosis.Suggestions.FirstOrDefault()}";
            return;
        }

        _lblSummary.ForeColor = DarkGeekTheme.TextMuted;
        _lblSummary.Text =
            $"驱动：{driver}\r\n" +
            $"符号 {Ctx.Device.Symbols.Count} 个 · 外设 {Ctx.Device.Svd?.Peripherals.Count ?? 0} 个\r\n" +
            $"RTT 控制块：{(device.Config.RttControlBlockAddress is uint rtt ? $"0x{rtt:X8}" : "未确定")}\r\n" +
            $"链路：{device.LinkDescription}";
    }

    private void OnDeviceLog(object? sender, string message) => SafeInvoke(() => AppendLog(message));

    /// <summary>连接建立后回填实际生效的配置，避免界面显示与实际会话不一致。</summary>
    private void OnDeviceStateChanged(object? sender, EventArgs e) => SafeInvoke(() =>
    {
        if (Ctx.Device.IsConnected) RefreshFromContext();
        else RefreshStatus();
    });

    private void AppendLog(string message)
    {
        if (_log.IsDisposed) return;
        _log.AppendText($"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
        if (_log.Lines.Length > 800)
        {
            var lines = _log.Lines;
            _log.Text = string.Join("\n", lines.Skip(lines.Length - 600));
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
        }
        else
        {
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
        }
    }

    private void SafeInvoke(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Ctx.Device.Log -= OnDeviceLog;
            Ctx.SessionLog -= OnDeviceLog;
            Ctx.Device.StateChanged -= OnDeviceStateChanged;
        }
        base.Dispose(disposing);
    }
}
