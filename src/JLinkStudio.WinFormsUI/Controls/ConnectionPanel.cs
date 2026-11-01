using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using JLinkStudio.Core.Models;
using JLinkStudio.Core.Services;
using JLinkStudio.WinFormsUI.Pages;
using JLinkStudio.WinFormsUI.Theme;

namespace JLinkStudio.WinFormsUI.Controls;

/// <summary>
/// 右上角「连接」按键唤出的连接配置浮层，版式参考 J-Flash 的连接设置：
/// 左侧对齐标签 + 分区小标题的紧凑表单，底部右对齐操作按钮。点击外部自动收起。
/// </summary>
public sealed class ConnectionPanel : Form
{
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int CS_DROPSHADOW = 0x00020000;

    // 版式尺寸（固定尺寸弹层，绝对定位保证像素级对齐）
    private const int PanelWidth = 452;
    private const int PanelHeight = 294;
    private const int PadLeft = 16;
    private const int LabelWidth = 68;
    private const int FieldLeft = PadLeft + LabelWidth + 8;   // 92
    private const int RowHeight = 32;

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder buffer, int maxCount);

    private readonly AppHost _host;
    private readonly RoundedSelect _cmbDevice = new();
    private readonly RoundedSelect _cmbSpeed = new();
    private readonly FlatButton _btnPick;
    private readonly FlatButton _btnSwd;
    private readonly FlatButton _btnJtag;
    private readonly StatusPill _pill = new();
    private readonly Label _lblSummary = new();
    private readonly FlatButton _btnToggle;
    private readonly DismissFilter _filter;
    private bool _jtag;

    public ConnectionPanel(AppHost host)
    {
        _host = host;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        // 以 96 DPI 为基准做等比缩放：高 DPI 下控件与文字同步放大，不会出现文字挤出边框
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = DarkGeekTheme.CardBack;
        ClientSize = new Size(PanelWidth, PanelHeight);
        KeyPreview = true;
        DoubleBuffered = true;

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);

        // ── 控件 ──
        _cmbSpeed.AddRange(PageDeviceConfig.SpeedPresets.Select(p => p.Name).ToArray());
        _cmbSpeed.Size = new Size(180, RowHeight);
        _cmbSpeed.SelectedIndex = 4;

        // 目标芯片：可输入的下拉框，下拉只保留最近使用的 5 个型号
        _cmbDevice.Size = new Size(268, RowHeight);
        _cmbDevice.Editable = true;
        LoadRecentDevices();
        RebuildDeviceCombo(null);
        _cmbDevice.SelectedIndexChanged += (_, _) => OnDevicePicked();

        // 下拉右侧独立的「选择」按键：弹出带 RAM/Flash 的图形化芯片列表
        _btnPick = Ui.Button("选择 ▾", ButtonKind.Ghost, null, 62);
        _btnPick.Click += (_, _) => DevicePicker.Show(_btnPick, device =>
        {
            RememberDevice(device);
            RebuildDeviceCombo(device);
        });

        _btnSwd = Ui.Button("SWD", ButtonKind.Primary, (_, _) => SelectInterface(false), 80);
        _btnJtag = Ui.Button("JTAG", ButtonKind.Ghost, (_, _) => SelectInterface(true), 80);

        // 连接 / 断开合并为同一个按钮，按当前状态切换文案与动作
        _btnToggle = Ui.Button("连接目标", ButtonKind.Primary, async (_, _) => await ToggleConnectionAsync(), 150);

        BuildLayout();

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) Hide();
        };

        _filter = new DismissFilter(this);
    }

    private StudioContext Context => _host.Context;

    /// <summary>需要排除在「点击外部收起」判定之外的控件（例如唤出本浮层的按钮）。</summary>
    public Control? ExcludeControl { get; set; }

    public event EventHandler? SessionChanged;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= CS_DROPSHADOW;
            return cp;
        }
    }

    // ── 版式 ───────────────────────────────────────────────────────────────

    private void BuildLayout()
    {
        var title = new Label
        {
            Text = "连接配置",
            AutoSize = false,
            Location = new Point(PadLeft, 12),
            Size = new Size(120, 24),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = DarkGeekTheme.TextPrimary,
            BackColor = Color.Transparent,
            Font = DarkGeekTheme.CreateFontPublic(11f, FontStyle.Bold),
        };

        var subtitle = new Label
        {
            Text = "J-Link / J-Trace 连接设置",
            AutoSize = true,
            Location = new Point(PadLeft + 1, 37),
            ForeColor = DarkGeekTheme.TextMuted,
            BackColor = Color.Transparent,
            Font = DarkGeekTheme.UiFontSmall,
        };

        var close = new FlatButton
        {
            Text = "✕",
            Kind = ButtonKind.Ghost,
            Bordered = false,
            ForeColor = DarkGeekTheme.TextSecondary,
            Location = new Point(PanelWidth - 38, 12),
            Size = new Size(26, 26),
        };
        close.Click += (_, _) => Hide();

        _pill.Location = new Point(PanelWidth - 138, 34);

        // 只保留三项配置：目标 / 接口 / 速率
        Controls.AddRange(new Control[]
        {
            title, subtitle, close, _pill,
            Hairline(58),

            Caption("目标", 72),
            Place(_cmbDevice, FieldLeft, 72, 268),
            Place(_btnPick, FieldLeft + 276, 72, 62),

            Caption("接口", 112),
            Place(_btnSwd, FieldLeft, 112),
            Place(_btnJtag, FieldLeft + 86, 112),

            Caption("速率", 152),
            Place(_cmbSpeed, FieldLeft, 152),
            Hint("kHz", FieldLeft + 188, 152, 60),

            Hairline(196),

            _lblSummary.With(c =>
            {
                c.Location = new Point(PadLeft, 204);
                c.Size = new Size(PanelWidth - PadLeft * 2, 34);
                c.AutoSize = false;
                c.ForeColor = DarkGeekTheme.TextMuted;
                c.BackColor = Color.Transparent;
                c.Font = DarkGeekTheme.UiFontSmall;
                c.Text = "提示：连接前请插好 J-Link 调试器并给目标板上电。";
            }),

            Place(_btnToggle, PanelWidth - PadLeft - 150, 246, 150, 34),
        });
    }

    private static Panel Hairline(int y) => new()
    {
        Location = new Point(PadLeft, y),
        Size = new Size(PanelWidth - PadLeft * 2, 1),
        BackColor = DarkGeekTheme.Border,
    };

    private static Label Caption(string text, int y) => new()
    {
        Text = text,
        AutoSize = false,
        Location = new Point(PadLeft, y),
        Size = new Size(LabelWidth, RowHeight),
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = DarkGeekTheme.TextSecondary,
        BackColor = Color.Transparent,
        Font = DarkGeekTheme.UiFont,
    };

    private static Label Hint(string text, int x, int y, int width) => new()
    {
        Text = text,
        AutoSize = false,
        Location = new Point(x, y),
        Size = new Size(width, RowHeight),
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = DarkGeekTheme.TextMuted,
        BackColor = Color.Transparent,
        Font = DarkGeekTheme.UiFontSmall,
    };

    /// <summary>定位并统一控件高度与边距，保证每行底边严格对齐。</summary>
    // ── 最近使用的目标型号（最多 5 个，持久化到本地文件）──
    private const int MaxRecentDevices = 5;
    private static readonly List<string> RecentDevices = new();
    private static bool _recentLoaded;
    private bool _syncingDevice;

    private static string RecentDevicesFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JLinkStudioPro", "recent-devices.txt");

    private static void LoadRecentDevices()
    {
        if (_recentLoaded) return;
        _recentLoaded = true;

        try
        {
            if (!File.Exists(RecentDevicesFile)) return;

            foreach (string line in File.ReadAllLines(RecentDevicesFile))
            {
                string device = line.Trim();
                if (device.Length == 0) continue;
                if (RecentDevices.Contains(device, StringComparer.OrdinalIgnoreCase)) continue;

                RecentDevices.Add(device);
                if (RecentDevices.Count >= MaxRecentDevices) break;
            }
        }
        catch
        {
            // 读取历史记录失败不影响连接功能
        }
    }

    /// <summary>记录一次型号选择：最新置顶，最多保留 5 个。</summary>
    private static void RememberDevice(string device)
    {
        device = device.Trim();
        if (device.Length == 0) return;

        LoadRecentDevices();

        RecentDevices.RemoveAll(d => string.Equals(d, device, StringComparison.OrdinalIgnoreCase));
        RecentDevices.Insert(0, device);
        while (RecentDevices.Count > MaxRecentDevices)
            RecentDevices.RemoveAt(RecentDevices.Count - 1);

        try
        {
            string? dir = Path.GetDirectoryName(RecentDevicesFile);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllLines(RecentDevicesFile, RecentDevices);
        }
        catch
        {
            // 写入失败只影响下次启动的历史记录
        }
    }

    /// <summary>按最近使用列表重建下拉项；当前型号不在列表时补入，避免显示为空。</summary>
    private void RebuildDeviceCombo(string? current)
    {
        _syncingDevice = true;
        try
        {
            var items = new List<string>(RecentDevices);
            if (items.Count == 0) items.Add("STM32H743VI");

            if (!string.IsNullOrWhiteSpace(current) &&
                !items.Contains(current, StringComparer.OrdinalIgnoreCase))
            {
                items.Insert(0, current);
            }

            _cmbDevice.Items.Clear();
            _cmbDevice.AddRange(items.ToArray());

            if (!string.IsNullOrWhiteSpace(current)) _cmbDevice.Text = current;
        }
        finally
        {
            _syncingDevice = false;
        }
    }

    private void OnDevicePicked()
    {
        if (_syncingDevice) return;
        if (_cmbDevice.SelectedItem is not { } picked) return;

        RememberDevice(picked);
        RebuildDeviceCombo(picked);
    }

    private static Control Place(Control control, int x, int y, int width = 0, int height = RowHeight)
    {
        if (width > 0) control.Width = width;
        control.Height = height;
        control.Margin = new Padding(0);
        control.Location = new Point(x, y);
        return control;
    }

    private void SelectInterface(bool jtag)
    {
        _jtag = jtag;
        _btnSwd.Kind = jtag ? ButtonKind.Ghost : ButtonKind.Primary;
        _btnJtag.Kind = jtag ? ButtonKind.Primary : ButtonKind.Ghost;
        _btnSwd.ToggleState = !jtag;
        _btnJtag.ToggleState = jtag;
        _btnSwd.Invalidate();
        _btnJtag.Invalidate();
    }

    /// <summary>从当前会话回填控件，然后显示在指定屏幕位置。</summary>
    public void ShowPanel(Form owner, Point screenLocation)
    {
        SeedFromContext();
        RefreshState();
        Location = screenLocation;
        Show(owner);
        BringToFront();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) Application.AddMessageFilter(_filter);
        else Application.RemoveMessageFilter(_filter);
    }

    private void SeedFromContext()
    {
        var config = Context.Device.Config;
        string device = string.IsNullOrWhiteSpace(config.Device) ? "STM32H743VI" : config.Device;
        RebuildDeviceCombo(device);
        SelectInterface(config.Interface == JLinkInterface.JTAG);

        int speedIndex = Array.FindIndex(PageDeviceConfig.SpeedPresets, p => p.Khz == config.SpeedKhz);
        _cmbSpeed.SelectedIndex = speedIndex >= 0 ? speedIndex : 4;
    }

    private void RefreshState()
    {
        var device = Context.Device;
        _pill.SetState(
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

        var diagnosis = device.LastDiagnosis;
        if (!device.IsConnected && diagnosis != null)
        {
            _lblSummary.ForeColor = DarkGeekTheme.Danger;
            _lblSummary.Text = $"连接异常：{diagnosis.Causes.FirstOrDefault()}\r\n建议：{diagnosis.Suggestions.FirstOrDefault()}";
        }
        else
        {
            _lblSummary.ForeColor = DarkGeekTheme.TextMuted;
            _lblSummary.Text = device.IsConnected
                ? $"链路：{device.LinkDescription} · 符号 {device.Symbols.Count} 个 · 外设 {device.Svd?.Peripherals.Count ?? 0} 个"
                : $"驱动：{DeviceManager.DriverVersion ?? "未找到 J-Link SDK"}";
        }

        // 同一个按钮：已连接显示「断开连接」，否则显示「连接目标」
        _btnToggle.Text = device.IsConnected ? "断开连接" : "连接目标";
        _btnToggle.Kind = device.IsConnected ? ButtonKind.Ghost : ButtonKind.Primary;
        _btnToggle.Enabled = device.State != ConnectionState.Connecting;
        _btnToggle.Invalidate();
    }

    /// <summary>连接 / 断开共用按钮：已连接则断开，否则按当前表单参数建立会话。</summary>
    private async Task ToggleConnectionAsync()
    {
        if (Context.Device.IsConnected)
        {
            Context.StopSession();
            RefreshState();
            SessionChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        await ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        var current = Context.Device.Config;
        var config = new DeviceSessionConfig
        {
            Device = string.IsNullOrWhiteSpace(_cmbDevice.Text) ? "STM32H743VI" : _cmbDevice.Text.Trim(),
            Interface = _jtag ? JLinkInterface.JTAG : JLinkInterface.SWD,
            SpeedKhz = PageDeviceConfig.SpeedPresets[Math.Max(0, _cmbSpeed.SelectedIndex)].Khz,
            SerialNumber = current.SerialNumber,
            ElfPath = current.ElfPath,
            SvdPath = current.SvdPath,
            RttSearchMode = current.RttSearchMode,
            RttControlBlockAddress = current.RttControlBlockAddress,
        };

        // 连接即视为一次选择：记录到最近使用列表（最多 5 个）
        RememberDevice(config.Device);
        RebuildDeviceCombo(config.Device);

        _btnToggle.Enabled = false;
        await Context.StartSessionAsync(config);
        _btnToggle.Enabled = true;

        RefreshState();
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Width <= 0 || Height <= 0) return;
        using var path = DarkGeekTheme.RoundedRect(new Rectangle(0, 0, Width, Height), 10);
        var old = Region;
        Region = new Region(path);
        old?.Dispose();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(DarkGeekTheme.CardBack);

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = DarkGeekTheme.RoundedRect(rect, 10);
        using var pen = new Pen(DarkGeekTheme.Border);
        g.DrawPath(pen, path);
    }

    /// <summary>点击浮层外部时自动收起；忽略菜单/下拉弹层自身的窗口。</summary>
    private sealed class DismissFilter : IMessageFilter
    {
        private readonly ConnectionPanel _owner;

        public DismissFilter(ConnectionPanel owner) => _owner = owner;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg is not (WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)) return false;
            if (!_owner.Visible) return false;

            var position = Control.MousePosition;
            if (_owner.Bounds.Contains(position)) return false;
            if (_owner.ExcludeControl is { Visible: true } exclude &&
                exclude.RectangleToScreen(exclude.ClientRectangle).Contains(position)) return false;
            // 本浮层弹出的子窗口（如芯片选择窗口）内部的点击不收起浮层
            if (IsInsideOwnedForm(position)) return false;
            if (IsPopupWindow(position)) return false;

            _owner.Hide();
            return false;
        }

        private bool IsInsideOwnedForm(Point screenPoint)
        {
            foreach (Form owned in _owner.OwnedForms)
            {
                if (owned is { Visible: true } && owned.Bounds.Contains(screenPoint)) return true;
            }
            return false;
        }

        private static bool IsPopupWindow(Point screenPoint)
        {
            var handle = WindowFromPoint(screenPoint);
            if (handle == IntPtr.Zero) return false;

            var buffer = new System.Text.StringBuilder(64);
            GetClassName(handle, buffer, buffer.Capacity);
            // #32768 = 系统菜单 / ContextMenuStrip 弹层，避免选择下拉项时收起浮层
            return buffer.ToString() == "#32768";
        }
    }
}

/// <summary>链式配置小工具，便于在一处集中声明控件属性。</summary>
internal static class ControlChainExtensions
{
    public static T With<T>(this T control, Action<T> configure) where T : Control
    {
        configure(control);
        return control;
    }
}
