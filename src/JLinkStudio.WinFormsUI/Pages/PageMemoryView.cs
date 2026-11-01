using System.Diagnostics;
using System.Globalization;
using System.Text;
using JLinkStudio.Core.Services;
using JLinkStudio.WinFormsUI.Controls;
using JLinkStudio.WinFormsUI.Navigation;
using JLinkStudio.WinFormsUI.Theme;

namespace JLinkStudio.WinFormsUI.Pages;

/// <summary>
/// 子界面：内存查看。按地址读取目标 RAM / 内部 Flash / 外部 Flash，
/// 以十六进制 + ASCII 形式展示，可导出为 .bin 备份。
/// </summary>
public sealed class PageMemoryView : UserControl, ISubPageView
{
    /// <summary>单次读取上限：64 KiB，避免一次拉太多导致界面假死。</summary>
    private const int MaxBytes = 0x10000;

    /// <summary>分块大小：每次调 J-Link 读取 4 KiB，失败时能给出已读到的偏移。</summary>
    private const int ChunkSize = 4096;

    private readonly AppHost _host;
    private readonly RoundedSelect _cboRegion = Ui.Combo("RAM", "内部 Flash", "外部 Flash", "自定义");
    private readonly RoundedTextBox _txtAddress = Ui.Input("0x20000000", 150);
    private readonly RoundedTextBox _txtLength = Ui.Input("0x100", 110);
    private readonly FlatCheckBox _chkAuto = Ui.Check("自动刷新", false);
    private readonly RoundedSelect _cboInterval = Ui.Combo("500 ms", "1 s", "2 s");
    private readonly RichTextBox _txtHex = new();
    private readonly Label _lblResult = new();
    private readonly Label _lblRegionHint = new();
    private readonly System.Windows.Forms.Timer _timer = new();

    // 在 BuildLayout 中创建，因此不能声明为 readonly
    private FlatButton _btnRead = null!;
    private FlatButton _btnExport = null!;
    private FlatButton _btnCopy = null!;

    private byte[] _lastBytes = Array.Empty<byte>();
    private uint _lastAddress;
    private bool _reading;

    public PageMemoryView(AppHost host)
    {
        _host = host;
        Dock = DockStyle.Fill;
        BackColor = DarkGeekTheme.PageBack;
        Padding = new Padding(16, 4, 16, 12);

        Controls.Add(BuildLayout());

        Ctx.Device.StateChanged += (_, _) => SafeInvoke(RefreshConnectionHint);
        _cboRegion.SelectedIndexChanged += (_, _) => SafeInvoke(ApplyRegionPreset);
        _chkAuto.CheckedChanged += (_, _) => SafeInvoke(ApplyAutoRefresh);

        _timer.Tick += async (_, _) => await ReadAsync();
        _timer.Interval = 1000;

        RefreshConnectionHint();
        ApplyRegionPreset();
    }

    private StudioContext Ctx => _host.Context;

    public string PageKey => "memory";

    public string PageTitle => "💾 内存";

    public void OnPageActivated()
    {
        RefreshConnectionHint();
        _btnRead.Enabled = !_reading;
    }

    public void OnPageHidden()
    {
        // 切走时停掉定时刷新，避免后台无意义地占用调试总线
        _timer.Stop();
    }

    private Control BuildLayout()
    {
        var card = Ui.PageCard(new Padding(16, 10, 16, 14));
        var body = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };

        // 区域行：RAM / 内部 Flash / 外部 Flash / 自定义
        var rowRegion = Ui.Row(0, 6);
        rowRegion.Controls.Add(Ui.Text("内存区域", true));
        _cboRegion.Width = 140;
        rowRegion.Controls.Add(_cboRegion);
        rowRegion.Controls.Add(Ui.Text("起始地址", true));
        rowRegion.Controls.Add(_txtAddress);
        rowRegion.Controls.Add(Ui.Text("长度", true));
        rowRegion.Controls.Add(_txtLength);
        rowRegion.Controls.Add(Ui.Text("字节（支持 0x 十六进制）", false, DarkGeekTheme.TextMuted, 8.5f));

        // 区域说明行
        var rowHint = Ui.Row(0, 6);
        _lblRegionHint.AutoSize = true;
        _lblRegionHint.Margin = new Padding(0, 6, 8, 0);
        _lblRegionHint.Font = DarkGeekTheme.UiFontSmall;
        _lblRegionHint.ForeColor = DarkGeekTheme.TextMuted;
        rowHint.Controls.Add(_lblRegionHint);

        // 操作行
        var rowActions = Ui.Row(0, 12);
        _btnRead = Ui.Button("🔍 读取", ButtonKind.Primary, async (_, _) => await ReadAsync(), 108);
        rowActions.Controls.Add(_btnRead);
        _btnCopy = Ui.Button("复制", ButtonKind.Ghost, (_, _) => CopyHex(), 72);
        rowActions.Controls.Add(_btnCopy);
        _btnExport = Ui.Button("导出 BIN", ButtonKind.Ghost, (_, _) => ExportBinary(), 96);
        rowActions.Controls.Add(_btnExport);
        _chkAuto.Margin = new Padding(8, 8, 8, 0);
        rowActions.Controls.Add(_chkAuto);
        _cboInterval.Width = 100;
        _cboInterval.SelectedIndex = 1;
        rowActions.Controls.Add(_cboInterval);
        _lblResult.AutoSize = true;
        _lblResult.Margin = new Padding(8, 10, 8, 0);
        _lblResult.Font = DarkGeekTheme.UiFontSmall;
        _lblResult.ForeColor = DarkGeekTheme.TextMuted;
        rowActions.Controls.Add(_lblResult);

        // 数据区
        _txtHex.BorderStyle = BorderStyle.None;
        _txtHex.BackColor = DarkGeekTheme.InputBack;
        _txtHex.ForeColor = DarkGeekTheme.TextPrimary;
        _txtHex.Font = DarkGeekTheme.MonoFont;
        _txtHex.ReadOnly = true;
        _txtHex.WordWrap = false;
        _txtHex.DetectUrls = false;
        _txtHex.ScrollBars = RichTextBoxScrollBars.Both;

        // Dock 顺序：先加 Fill，再自下而上加 Top 行（后加的排在上面）
        body.Controls.Add(Ui.WrapDisplay(_txtHex));
        body.Controls.Add(rowActions);
        body.Controls.Add(rowHint);
        body.Controls.Add(rowRegion);
        card.Controls.Add(body);
        return card;
    }

    /// <summary>切换区域时只改起始地址，长度保留用户输入（默认 0x100）。</summary>
    private void ApplyRegionPreset()
    {
        switch (_cboRegion.SelectedIndex)
        {
            case 0:
                _txtAddress.Text = "0x20000000";
                _lblRegionHint.Text = "RAM：STM32 等 Cortex-M 器件通常从 0x20000000 起，长度超出 RAM 容量会读失败。";
                break;
            case 1:
                _txtAddress.Text = "0x08000000";
                _lblRegionHint.Text = "内部 Flash：多数 Cortex-M 的片内 Flash 从 0x08000000 起，读回即为当前烧写的固件内容。";
                break;
            case 2:
                _txtAddress.Text = "0x90000000";
                _lblRegionHint.Text = "外部 Flash：基址随芯片与外部存储器接口而异，常见 0x90000000（QSPI）/ 0x60000000（FMC）/ 0xC0000000（SDRAM 映射），请按原理图修改。";
                break;
            default:
                _lblRegionHint.Text = "自定义：任何可访问地址均可读取；外设寄存器与未映射区域可能读取失败。";
                break;
        }
    }

    private void ApplyAutoRefresh()
    {
        if (_chkAuto.Checked)
        {
            _timer.Interval = _cboInterval.SelectedIndex switch
            {
                0 => 500,
                2 => 2000,
                _ => 1000,
            };
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    private async Task ReadAsync()
    {
        if (_reading) return;

        var link = Ctx.Device.Link;
        if (link is null || !link.IsConnected)
        {
            Report("目标未连接：请先在「设备与工程配置」连接 J-Link", DarkGeekTheme.Warning);
            _timer.Stop();
            _chkAuto.Checked = false;
            return;
        }

        if (!TryParseNumber(_txtAddress.Text, out uint address))
        {
            Report($"起始地址无法识别：{_txtAddress.Text.Trim()}", DarkGeekTheme.Danger);
            return;
        }

        if (!TryParseNumber(_txtLength.Text, out uint length) || length == 0)
        {
            Report($"长度无法识别：{_txtLength.Text.Trim()}", DarkGeekTheme.Danger);
            return;
        }

        if (length > MaxBytes)
        {
            Report($"单次最多读取 {MaxBytes} 字节（0x{MaxBytes:X}），当前请求 {length} 字节", DarkGeekTheme.Danger);
            return;
        }

        _reading = true;
        _btnRead.Enabled = false;

        var stopwatch = Stopwatch.StartNew();
        var (ok, read, data, error) = await Task.Run(() =>
        {
            var buffer = new byte[length];
            int offset = 0;
            while (offset < buffer.Length)
            {
                int chunk = Math.Min(ChunkSize, buffer.Length - offset);
                var piece = new byte[chunk];
                int rc = link.ReadMemory((uint)(address + offset), piece);
                if (rc < 0)
                    return (false, offset, buffer, $"地址 0x{address + offset:X8} 读取失败（已读 {offset} 字节）");

                Buffer.BlockCopy(piece, 0, buffer, offset, chunk);
                offset += chunk;
            }

            return (true, offset, buffer, string.Empty);
        });
        stopwatch.Stop();

        // 失败时也保留已读到的部分，便于对照波形 / 寄存器定位越界位置
        _lastAddress = address;
        _lastBytes = read > 0 ? data[..read] : Array.Empty<byte>();

        RenderHex(address, _lastBytes, read);

        if (ok)
        {
            Report($"读取成功 · {read} 字节 · {stopwatch.ElapsedMilliseconds} ms", DarkGeekTheme.Success);
        }
        else
        {
            Report($"读取中断：{error}", DarkGeekTheme.Danger);
            Ctx.LogSession($"内存读取失败：0x{address:X8} + {length} 字节 · {error}");
        }

        _reading = false;
        _btnRead.Enabled = true;
    }

    /// <summary>把字节渲染成 16 字节/行的十六进制 + ASCII 视图。</summary>
    private void RenderHex(uint address, byte[] data, int count)
    {
        if (count <= 0 || data.Length == 0)
        {
            _txtHex.Text = string.Empty;
            return;
        }

        var sb = new StringBuilder(count / 16 * 78 + 128);
        for (int i = 0; i < count; i += 16)
        {
            int lineLength = Math.Min(16, count - i);
            sb.Append((address + i).ToString("X8")).Append("  ");

            for (int j = 0; j < 16; j++)
            {
                sb.Append(j < lineLength ? data[i + j].ToString("X2") : "  ");
                sb.Append(j == 7 ? "  " : " ");
            }

            sb.Append(" |");
            for (int j = 0; j < lineLength; j++)
            {
                byte b = data[i + j];
                sb.Append(b >= 32 && b < 127 ? (char)b : '.');
            }

            sb.Append('|').AppendLine();
        }

        _txtHex.Text = sb.ToString();
        _txtHex.SelectionStart = 0;
        _txtHex.ScrollToCaret();
    }

    private void CopyHex()
    {
        if (_txtHex.TextLength == 0)
        {
            Report("没有可复制的内容，请先读取", DarkGeekTheme.Warning);
            return;
        }

        Clipboard.SetText(_txtHex.Text);
        Report("已复制到剪贴板", DarkGeekTheme.Success);
    }

    private void ExportBinary()
    {
        if (_lastBytes.Length == 0)
        {
            Report("没有可导出的数据，请先读取", DarkGeekTheme.Warning);
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = "导出内存数据",
            Filter = "二进制文件 (*.bin)|*.bin|所有文件 (*.*)|*.*",
            FileName = $"mem_0x{_lastAddress:X8}.bin",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            File.WriteAllBytes(dialog.FileName, _lastBytes);
            Report($"已导出 {_lastBytes.Length} 字节 → {Path.GetFileName(dialog.FileName)}", DarkGeekTheme.Success);
            Ctx.LogSession($"内存导出：0x{_lastAddress:X8} + {_lastBytes.Length} 字节 → {dialog.FileName}");
        }
        catch (Exception ex)
        {
            Report($"导出失败：{ex.Message}", DarkGeekTheme.Danger);
        }
    }

    /// <summary>解析地址 / 长度：带 0x 前缀按十六进制，纯数字按十进制，含 A-F 时按十六进制。</summary>
    private static bool TryParseNumber(string text, out uint value)
    {
        value = 0;
        text = text.Trim();
        if (text.Length == 0) return false;

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

        if (text.Any(ch => "abcdefABCDEF".Contains(ch)))
            return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

        return uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private void RefreshConnectionHint()
    {
        if (_reading) return;
        bool connected = Ctx.Device.Link is { IsConnected: true };
        Report(connected ? "目标已连接，可读取内存" : "目标未连接：请先在「设备与工程配置」连接 J-Link",
            connected ? DarkGeekTheme.TextMuted : DarkGeekTheme.Warning);
    }

    private void Report(string message, Color color)
    {
        _lblResult.Text = message;
        _lblResult.ForeColor = color;
    }

    private void SafeInvoke(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }
}
