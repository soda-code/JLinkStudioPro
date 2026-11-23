using System.Diagnostics;
using System.Globalization;
using JLinkStudio.Core.Models;
using JLinkStudio.Core.Services;
using JLinkStudio.WinFormsUI.Controls;
using JLinkStudio.WinFormsUI.Navigation;
using JLinkStudio.WinFormsUI.Theme;

namespace JLinkStudio.WinFormsUI.Pages;

/// <summary>
/// 子界面：程序下载。通过 J-Link 内置 Flash 算法把固件烧写到目标芯片，
/// 支持 .bin（需指定起始地址）、.hex / .elf / .axf（使用文件自带地址）。
/// </summary>
public sealed class PageDownloadView : UserControl, ISubPageView
{
    private const string InternalBaseText = "0x08000000";
    private const string ExternalBaseText = "0x90000000";

    private readonly AppHost _host;
    private readonly RoundedTextBox _txtFile = Ui.Input("", 470);
    private readonly RoundedTextBox _txtAddress = Ui.Input(InternalBaseText, 150);
    private readonly RoundedSelect _cmbTarget = Ui.Combo("片内 Flash", "片外 Flash");
    private readonly RoundedTextBox _txtExternalDevice = Ui.Input("", 260);
    private readonly RoundedTextBox _txtExternalBase = Ui.Input(ExternalBaseText, 150);
    private readonly FlatCheckBox _chkReset = Ui.Check("下载后复位运行", true);
    private readonly RichTextBox _log = new();
    private readonly Label _lblResult = new();
    // 在 BuildLayout 中创建，因此不能声明为 readonly
    private Label _lblAddressHint = null!;
    private FlowLayoutPanel _rowExternal = null!;
    private FlatButton _btnDownload = null!;
    private bool _downloading;

    public PageDownloadView(AppHost host)
    {
        _host = host;
        Dock = DockStyle.Fill;
        BackColor = DarkGeekTheme.PageBack;
        Padding = new Padding(16, 4, 16, 12);

        Controls.Add(BuildLayout());

        // 连接 / 断开目标时刷新提示文案
        Ctx.Device.StateChanged += (_, _) => SafeInvoke(RefreshConnectionHint);

        // 文件格式决定地址是否必填，切换后缀时同步提示
        _txtFile.TextChanged += (_, _) => SafeInvoke(RefreshAddressHint);

        // 下载模式切换：片外模式展开型号 / 基址，起始地址跟着切默认值
        _cmbTarget.SelectedIndexChanged += (_, _) => ApplyTargetMode();

        RefreshConnectionHint();
        ApplyTargetMode();
    }

    private StudioContext Ctx => _host.Context;

    public string PageKey => "download";

    public string PageTitle => "⬇ 程序下载";

    public void OnPageActivated()
    {
        RefreshConnectionHint();
        _btnDownload.Enabled = !_downloading;
    }

    public void OnPageHidden()
    {
        // 无后台采集，切走后无需暂停任何东西
    }

    private Control BuildLayout()
    {
        var card = Ui.PageCard(new Padding(16, 10, 16, 14));
        var body = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };

        // 固件文件行
        var rowFile = Ui.Row(0, 6);
        rowFile.Controls.Add(Ui.Text("固件文件", true));
        rowFile.Controls.Add(_txtFile);
        rowFile.Controls.Add(Ui.Button("浏览…", ButtonKind.Ghost, (_, _) => BrowseFirmware(), 76));

        // 下载模式行：片内 Flash / 片外 Flash
        var rowMode = Ui.Row(0, 6);
        rowMode.Controls.Add(Ui.Text("下载模式", true));
        _cmbTarget.Width = 130;
        rowMode.Controls.Add(_cmbTarget);
        rowMode.Controls.Add(Ui.Text("片内走芯片 Flash 算法；片外需选带外部存储支持的型号",
            false, DarkGeekTheme.TextMuted, 8.5f));

        // 片外设置行：仅片外模式显示
        var rowExternal = Ui.Row(0, 6);
        rowExternal.Controls.Add(Ui.Text("片外型号", true));
        _txtExternalDevice.Width = 260;
        rowExternal.Controls.Add(_txtExternalDevice);
        rowExternal.Controls.Add(Ui.Text("片外基址", true));
        rowExternal.Controls.Add(_txtExternalBase);
        rowExternal.Controls.Add(Ui.Text("（型号如 STM32H743XI_QSPI；留空则沿用当前型号）",
            false, DarkGeekTheme.TextMuted, 8.5f));
        _rowExternal = rowExternal;
        _rowExternal.Visible = false;

        // 起始地址行
        var rowAddress = Ui.Row(0, 6);
        rowAddress.Controls.Add(Ui.Text("起始地址", true));
        rowAddress.Controls.Add(_txtAddress);
        _lblAddressHint = Ui.Text("（.bin 必填；.hex / .elf 使用文件自带地址）", false, DarkGeekTheme.TextMuted, 8.5f);
        rowAddress.Controls.Add(_lblAddressHint);

        // 操作行
        var rowActions = Ui.Row(0, 12);
        _btnDownload = Ui.Button("⬇ 开始下载", ButtonKind.Primary, async (_, _) => await DownloadAsync(), 120);
        rowActions.Controls.Add(_btnDownload);
        _chkReset.Margin = new Padding(8, 8, 16, 0);
        rowActions.Controls.Add(_chkReset);
        _lblResult.AutoSize = true;
        _lblResult.Margin = new Padding(0, 10, 8, 0);
        _lblResult.Font = DarkGeekTheme.UiFontSmall;
        _lblResult.ForeColor = DarkGeekTheme.TextMuted;
        rowActions.Controls.Add(_lblResult);

        // 日志区
        _log.BorderStyle = BorderStyle.None;
        _log.BackColor = DarkGeekTheme.InputBack;
        _log.ForeColor = DarkGeekTheme.TextPrimary;
        _log.Font = DarkGeekTheme.MonoFont;
        _log.ReadOnly = true;
        _log.WordWrap = false;
        _log.DetectUrls = false;
        _log.ScrollBars = RichTextBoxScrollBars.Both;

        // Dock 顺序：先加 Fill，再自下而上加 Top 行（后加的排在上面）
        body.Controls.Add(Ui.WrapDisplay(_log));
        body.Controls.Add(rowActions);
        body.Controls.Add(rowAddress);
        body.Controls.Add(rowExternal);
        body.Controls.Add(rowMode);
        body.Controls.Add(rowFile);
        card.Controls.Add(body);
        return card;
    }

    private void BrowseFirmware()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择要下载的固件",
            Filter = "固件文件 (*.bin;*.hex;*.elf;*.axf;*.s19)|*.bin;*.hex;*.elf;*.axf;*.s19|" +
                     "二进制镜像 (*.bin)|*.bin|HEX / ELF (*.hex;*.elf;*.axf)|*.hex;*.elf;*.axf|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        string current = _txtFile.Text.Trim();
        if (current.Length > 0 && File.Exists(current))
            dialog.InitialDirectory = Path.GetDirectoryName(current);

        if (dialog.ShowDialog(this) == DialogResult.OK) _txtFile.Text = dialog.FileName;
    }

    private async Task DownloadAsync()
    {
        if (_host.IsInspectMode)
        {
            Report("查看模式下禁止烧写目标 Flash。", DarkGeekTheme.Warning);
            AppendLog("下载被拒绝：查看模式只允许观察，不允许修改目标。");
            return;
        }

        var link = Ctx.Device.Link;
        if (link is null || !link.IsConnected)
        {
            Report("目标未连接，请先在「设备与工程配置」里连接 J-Link。", DarkGeekTheme.Danger);
            AppendLog("下载失败：目标未连接。");
            return;
        }

        string path = _txtFile.Text.Trim();
        if (path.Length == 0 || !File.Exists(path))
        {
            Report("请先选择有效的固件文件（.bin / .hex / .elf）。", DarkGeekTheme.Danger);
            return;
        }

        if (!TryReadAddress(out uint address))
        {
            Report($"起始地址无法识别：{_txtAddress.Text.Trim()}", DarkGeekTheme.Danger);
            return;
        }

        bool external = IsExternalMode;
        uint externalBase = 0;
        if (external && !TryReadExternalBase(out externalBase))
        {
            Report($"片外基址无法识别：{_txtExternalBase.Text.Trim()}", DarkGeekTheme.Danger);
            return;
        }

        bool isBinary = string.Equals(Path.GetExtension(path), ".bin", StringComparison.OrdinalIgnoreCase);
        if (isBinary && address == 0)
        {
            Report(external
                ? ".bin 固件必须填写片外 Flash 起始地址（例如 0x90000000）。"
                : ".bin 固件必须填写起始地址（例如 0x08000000）。", DarkGeekTheme.Danger);
            return;
        }

        string fileName = Path.GetFileName(path);
        bool resetAfter = _chkReset.Checked;
        string externalDevice = external ? _txtExternalDevice.Text.Trim() : string.Empty;
        var options = new FlashDownloadOptions
        {
            Target = external ? FlashTarget.External : FlashTarget.Internal,
            ExternalDevice = externalDevice,
            ExternalBase = externalBase,
        };
        string modeLabel = external
            ? $"片外 Flash（{(externalDevice.Length == 0 ? "沿用当前型号" : externalDevice)}）"
            : "片内 Flash";

        _downloading = true;
        _btnDownload.Enabled = false;
        Report($"正在下载 {fileName} …", DarkGeekTheme.Warning);
        AppendLog($"开始下载 {fileName} → {(address == 0 ? "文件自带地址" : $"0x{address:X8}")} · {modeLabel}");

        var stopwatch = Stopwatch.StartNew();
        var (ok, error) = await Task.Run(() =>
        {
            bool done = link.DownloadFile(path, address, options, out string message);
            return (done, message);
        });
        stopwatch.Stop();

        string suffix = string.Empty;
        if (ok && resetAfter)
        {
            bool resetOk = await Task.Run(() => link.Reset(out _));
            suffix = resetOk ? "，已复位运行" : "，复位失败";
        }

        if (ok)
        {
            Report($"下载成功 · {stopwatch.ElapsedMilliseconds} ms{suffix}", DarkGeekTheme.Success);
            AppendLog($"✓ 下载成功：{fileName}，耗时 {stopwatch.ElapsedMilliseconds} ms{suffix}");
            Ctx.LogSession($"程序下载成功：{fileName}，耗时 {stopwatch.ElapsedMilliseconds} ms{suffix}");
        }
        else
        {
            Report($"下载失败：{error}", DarkGeekTheme.Danger);
            AppendLog($"✗ 下载失败（{stopwatch.ElapsedMilliseconds} ms）：{error}");
            Ctx.LogSession($"程序下载失败：{fileName} · {error}");
        }

        Ctx.Audit.Add("ui", ok ? "DOWNLOAD" : "DOWNLOAD_ERR", "/jlink/download",
            $"{fileName} → {(address == 0 ? "file" : $"0x{address:X8}")} · {modeLabel} · " +
            $"{stopwatch.ElapsedMilliseconds} ms · {(ok ? "成功" : error)}");

        _downloading = false;
        _btnDownload.Enabled = true;
    }

    /// <summary>解析起始地址：留空表示使用文件自带地址；支持 0x 前缀十六进制与纯十六进制写法。</summary>
    private bool TryReadAddress(out uint address)
    {
        address = 0;
        string text = _txtAddress.Text.Trim();
        if (text.Length == 0) return true;

        string digits = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        return uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
    }

    /// <summary>解析片外 Flash 基址；留空按 0 处理，由核心层回退到默认基址。</summary>
    private bool TryReadExternalBase(out uint address)
    {
        address = 0;
        string text = _txtExternalBase.Text.Trim();
        if (text.Length == 0) return true;

        string digits = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        return uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
    }

    private bool IsExternalMode => _cmbTarget.SelectedIndex == 1;

    /// <summary>切换下载模式：展开 / 收起片外参数，并把起始地址同步成对应的默认映射地址。</summary>
    private void ApplyTargetMode()
    {
        bool external = IsExternalMode;
        if (_rowExternal != null) _rowExternal.Visible = external;

        string text = _txtAddress.Text.Trim();
        if (external && (text.Length == 0 || IsSameAddress(text, InternalBaseText)))
            _txtAddress.Text = ExternalBaseText;
        else if (!external && IsSameAddress(text, ExternalBaseText))
            _txtAddress.Text = InternalBaseText;

        RefreshAddressHint();
    }

    private static bool IsSameAddress(string left, string right) =>
        string.Equals(left.Trim(), right, StringComparison.OrdinalIgnoreCase);

    /// <summary>.bin 没有地址信息，未填地址时给出更醒目的提示。</summary>
    private void RefreshAddressHint()
    {
        bool isBinary = string.Equals(Path.GetExtension(_txtFile.Text.Trim()), ".bin", StringComparison.OrdinalIgnoreCase);
        string example = IsExternalMode ? ExternalBaseText : InternalBaseText;

        _lblAddressHint.Text = isBinary
            ? $"（.bin 必须填写烧写起始地址，例如 {example}）"
            : "（.bin 必填；.hex / .elf 使用文件自带地址）";
        _lblAddressHint.ForeColor = isBinary ? DarkGeekTheme.Warning : DarkGeekTheme.TextMuted;
    }


    private void RefreshConnectionHint()
    {
        if (_downloading) return;
        bool connected = Ctx.Device.Link is { IsConnected: true };
        Report(connected ? "目标已连接，可开始下载" : "目标未连接：请先在「设备与工程配置」连接 J-Link",
            connected ? DarkGeekTheme.TextMuted : DarkGeekTheme.Warning);
    }

    private void Report(string message, Color color)
    {
        _lblResult.Text = message;
        _lblResult.ForeColor = color;
    }

    private void AppendLog(string message)
    {
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        if (_log.TextLength > 200_000) _log.Clear();
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private void SafeInvoke(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }
}
