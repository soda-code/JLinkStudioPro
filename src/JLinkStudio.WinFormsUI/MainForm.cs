using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using JLinkStudio.Core;
using JLinkStudio.Core.Models;
using JLinkStudio.Core.Services;
using JLinkStudio.WinFormsUI.Controls;
using JLinkStudio.WinFormsUI.Navigation;
using JLinkStudio.WinFormsUI.Pages;
using JLinkStudio.WinFormsUI.Theme;

namespace JLinkStudio.WinFormsUI;

/// <summary>主窗体：顶部全局状态栏 + 左侧折叠导航 + 右侧子页面容器 + 底部遥测状态栏。</summary>
public sealed class MainForm : Form
{
    private const int WM_NCHITTEST = 0x0084;
    private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
        HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    private readonly AppHost _host;
    private readonly NavSidebar _nav = new();
    private readonly Panel _pageHost = new();
    private readonly Panel _pageHeader = new();
    private readonly Panel _headerBar = new();
    private readonly PageRouter _router;

    private readonly FlatButton _btnConnect = new();
    private readonly FlatButton _btnReset = new();
    private readonly ConnectionPanel _connectionPanel;
    private readonly Label _lblPageTitle = new();
    private readonly Label _lblPageSubtitle = new();
    private readonly Label _lblStatusLeft = new();
    private readonly Label _lblStatusMiddle = new();
    private readonly Label _lblStatusRight = new();

    /// <summary>顶栏品牌位：显示当前芯片型号 / 接口 / 速率。</summary>
    private Label _brand = null!;
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 500 };

    private readonly string? _initialPage;
    private string? _initialSubPage;
    private PageSettings? _settingsPage;
    private long _lastBytesRead;
    private long _lastTick;

    /// <summary>窗口外框圆角半径（逻辑像素，按 DPI 折算）。</summary>
    private const int WindowRadius = 16;

    private int RadiusPx => (int)Math.Round(WindowRadius * DeviceDpi / 96.0);

    public MainForm(AppHost host, string? initialPage = null)
    {
        _host = host;
        _initialPage = initialPage;
        _router = new PageRouter(_pageHost);
        _connectionPanel = new ConnectionPanel(host);
        _host.ModeChanged += (_, _) =>
        {
            // 模式组在左侧菜单顶部，切换后同步高亮与底部提示
            _nav.SetMode(_host.Mode == StudioMode.Inspect ? "inspect" : "debug");
            RefreshShell();
        };

        // 需要人工介入时（按复位、重新上电、检查接线）由 Skill / 系统请求弹框
        Context.PromptHandler = ShowUiPromptAsync;

        // 记住用户调整后的窗口大小，下次启动沿用
        ResizeEnd += (_, _) => SaveWindowSize();
        FormClosing += (_, _) => SaveWindowSize();

        SuspendLayout();
        ConfigureWindow();
        // 注意：WinForms 停靠按“后加入先停靠”的顺序计算。
        // 顺序保证：固定侧边栏贯穿全高（含品牌区），顶栏与状态栏只占用右侧内容区。
        BuildPageHost();
        BuildStatusBar();
        BuildTopBar();
        BuildNavigation();

        // 连接配置子界面：无边框浮层窗口，显示时定位到连接按钮下方
        _connectionPanel.ExcludeControl = _btnConnect;
        _connectionPanel.SessionChanged += (_, _) => RefreshShell();

        ResumeLayout(true);

        _uiTimer.Tick += (_, _) => RefreshShell();
        _uiTimer.Start();
    }

    private StudioContext Context => _host.Context;

    /// <summary>把 Skill / 系统的提示请求编组到 UI 线程弹框，返回用户确认状态与所选选项。</summary>
    private Task<UiPromptResult> ShowUiPromptAsync(UiPromptRequest request)
    {
        var completion = new TaskCompletionSource<UiPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Show()
        {
            NotifyDialog? dialog = null;
            System.Windows.Forms.Timer? timer = null;

            try
            {
                dialog = new NotifyDialog(request);

                // 超时自动关闭：Skill 已放弃等待时不能让模态框一直卡住界面
                int timeout = Math.Clamp(request.TimeoutMs, 1000, 300_000);
                timer = new System.Windows.Forms.Timer { Interval = timeout };
                timer.Tick += (_, _) =>
                {
                    timer!.Stop();
                    if (dialog is { IsDisposed: false })
                    {
                        dialog.DialogResult = DialogResult.Cancel;
                        dialog.Close();
                    }
                };
                timer.Start();

                var result = dialog.ShowDialog(this);
                completion.TrySetResult(new UiPromptResult
                {
                    Delivered = true,
                    Confirmed = result == DialogResult.OK,
                    Selected = result == DialogResult.OK ? dialog.SelectedOption : null,
                });
            }
            catch (Exception ex)
            {
                Context.Audit.Add("ui", "PROMPT-ERROR", "/api/v1/ui/prompt", ex.Message, 0, false);
                completion.TrySetResult(UiPromptResult.NotDelivered);
            }
            finally
            {
                timer?.Stop();
                timer?.Dispose();
                dialog?.Dispose();
            }
        }

        if (IsDisposed) return Task.FromResult(UiPromptResult.NotDelivered);

        try
        {
            if (InvokeRequired) BeginInvoke(Show);
            else Show();
        }
        catch (Exception)
        {
            completion.TrySetResult(UiPromptResult.NotDelivered);
        }

        return completion.Task;
    }

    private void ConfigureWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;

        // 窗口尺寸：优先沿用上次关闭时的大小，其次用默认尺寸（当前软件窗口大小）
        var workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1480, 920);
        float dpiScale = Math.Max(1f, DeviceDpi / 96f);
        int availableWidth = Math.Max(900, (int)(workArea.Width / dpiScale) - 32);
        int availableHeight = Math.Max(560, (int)(workArea.Height / dpiScale) - 32);

        Size wanted = LoadWindowSize() ?? DefaultClientSize;
        int width = Math.Min(wanted.Width, availableWidth);
        int height = Math.Min(wanted.Height, availableHeight);

        ClientSize = new Size(width, height);
        // 最小窗口尺寸放小一档：菜单 220 + 内容区留出约 800 宽，窄屏 / 分屏下也能缩到合适大小
        MinimumSize = new Size(Math.Min(1060, width), Math.Min(600, height));
        BackColor = DarkGeekTheme.WindowBack;
        ForeColor = DarkGeekTheme.TextPrimary;
        Font = DarkGeekTheme.UiFont;
        Text = $"{AppInfo.ProductName}  {AppInfo.DisplayVersion}";
        Icon = LoadAppIcon();
        DoubleBuffered = true;
        // 留 2px 边：1px 给圆角描边，1px 防止内容压到裁剪边缘
        Padding = new Padding(2);
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    // ── 窗口大小记忆 ──

    /// <summary>默认窗口大小（以当前软件窗口尺寸为基准）。</summary>
    private static readonly Size DefaultClientSize = new(1184, 634);

    private static string WindowSizeFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JLinkStudioPro", "window-size.txt");

    /// <summary>读取上次保存的窗口大小；不存在或格式异常时返回 null。</summary>
    private static Size? LoadWindowSize()
    {
        try
        {
            if (!File.Exists(WindowSizeFile)) return null;

            string[] parts = File.ReadAllText(WindowSizeFile).Trim().Split('x', 'X', '*', ',');
            if (parts.Length != 2) return null;

            if (!int.TryParse(parts[0].Trim(), out int width) || !int.TryParse(parts[1].Trim(), out int height))
                return null;

            // 过小的尺寸多半是异常数据（最小化 / 屏幕切换），忽略
            if (width < 900 || height < 560) return null;

            return new Size(width, height);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把当前窗口大小写入本地配置，下次启动沿用。</summary>
    private void SaveWindowSize()
    {
        try
        {
            if (WindowState != FormWindowState.Normal) return;

            string? directory = Path.GetDirectoryName(WindowSizeFile);
            if (directory != null) Directory.CreateDirectory(directory);

            File.WriteAllText(WindowSizeFile, $"{ClientSize.Width}x{ClientSize.Height}");
        }
        catch
        {
            // 保存失败只影响下次启动的默认尺寸
        }
    }

    /// <summary>读取内嵌应用图标，统一窗口标题栏 / 任务栏 / Alt+Tab 的显示。</summary>
    private static Icon? LoadAppIcon()
    {
        try
        {
            using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("JLinkStudio.app.ico");
            return stream == null ? null : new Icon(stream);
        }
        catch
        {
            return null;
        }
    }

    private void BuildTopBar()
    {
        _headerBar.Dock = DockStyle.Top;
        // 顶栏高度按“连接/复位”上移并放大后的位置 + 底部留白计算，其余控件位置保持不变
        _headerBar.Height = 65;
        _headerBar.BackColor = DarkGeekTheme.WindowBack;
        _headerBar.MouseDown += HeaderDrag;

        var collapse = new FlatButton
        {
            Text = "≡",
            Kind = ButtonKind.Ghost,
            Location = new Point(10, 8),
            Size = new Size(30, 28),
            Font = DarkGeekTheme.CreateFontPublic(11f),
        };
        collapse.Click += (_, _) => _nav.ToggleCollapse();

        // 右上角按钮组：普通 Panel + 手工从右往左排布。
        // FlowLayoutPanel 在「RTL + 不换行 + 内容宽度贴近面板宽度」时会把按钮顶出右边界，
        // 窄窗口/高 DPI 下会让「复位」「连接」被裁掉一半。
        var windowButtons = new Panel
        {
            Dock = DockStyle.Right,
            BackColor = Color.Transparent,
        };

        var btnClose = new FlatButton { Text = "✕", Kind = ButtonKind.Ghost, Bordered = false, ForeColor = DarkGeekTheme.TextSecondary, Size = new Size(38, 28), Margin = new Padding(4, 2, 0, 0) };
        btnClose.Click += (_, _) => Close();
        var btnMax = new FlatButton { Text = "□", Kind = ButtonKind.Ghost, Bordered = false, ForeColor = DarkGeekTheme.TextSecondary, Size = new Size(38, 28), Margin = new Padding(4, 2, 0, 0) };
        btnMax.Click += (_, _) => WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        var btnMin = new FlatButton { Text = "─", Kind = ButtonKind.Ghost, Bordered = false, ForeColor = DarkGeekTheme.TextSecondary, Size = new Size(38, 28), Margin = new Padding(4, 2, 0, 0) };
        btnMin.Click += (_, _) => WindowState = FormWindowState.Minimized;

        // 右上角：J-Link 复位（连接按钮右侧）+ 连接配置浮层入口
        _btnReset.Text = "⟲ 复位";
        _btnReset.Kind = ButtonKind.Ghost;
        _btnReset.Size = new Size(120, 40);
        // 右外边距 24 = 与窗口控制键保持 24px 间距（较默认再左移 20px）
        _btnReset.Margin = new Padding(10, 10, 24, 0);
        // 大圆角 + 浅灰底：在白顶栏上也能看出按钮边界
        _btnReset.Radius = 14;
        _btnReset.BackOverride = DarkGeekTheme.CardBackHover;
        _btnReset.ForeColor = DarkGeekTheme.TextSecondary;
        _btnReset.Font = DarkGeekTheme.UiFont;
        _btnReset.Click += (_, _) => ResetTarget();

        _btnConnect.Text = "🔗 连接";
        _btnConnect.Kind = ButtonKind.Primary;
        _btnConnect.Size = new Size(160, 40);
        _btnConnect.Margin = new Padding(10, 10, 0, 0);
        // 大圆角 + 蓝色渐变底（上浅下深），悬停整体提亮
        _btnConnect.Radius = 14;
        _btnConnect.GradientTop = DarkGeekTheme.PrimarySoft;
        _btnConnect.GradientBottom = DarkGeekTheme.Primary;
        _btnConnect.Font = DarkGeekTheme.UiFont;
        _btnConnect.Click += (_, _) => ToggleConnectionPanel();

        // 右 → 左：关闭 / 最大化 / 最小化 / 复位 / 连接；Gap 为该按钮左侧间距（沿用原视觉）
        var topCluster = new (FlatButton Button, int Top, int Gap)[]
        {
            (btnClose, 8, 4), (btnMax, 8, 4), (btnMin, 8, 24), (_btnReset, 16, 10), (_btnConnect, 16, 0),
        };
        foreach (var (button, _, _) in topCluster) windowButtons.Controls.Add(button);

        void LayoutTopCluster()
        {
            const int rightPadding = 8;
            // 每次排布都按按钮当前实际宽度回算面板宽度（高 DPI 缩放下按钮尺寸会变），
            // 保证「右留白 + 各按钮宽度与间距」恰好等于面板宽度，不会溢出被裁。
            int needed = rightPadding;
            foreach (var (button, _, gap) in topCluster) needed += button.Width + gap;
            if (windowButtons.Width != needed) windowButtons.Width = needed;

            int right = windowButtons.Width - rightPadding;
            foreach (var (button, top, gap) in topCluster)
            {
                button.Location = new Point(right - button.Width, top);
                right -= button.Width + gap;
            }
        }

        // 首次排布；面板加入顶栏后 Resize / 高 DPI 变化会再次触发，宽度随之自适应
        LayoutTopCluster();
        windowButtons.Resize += (_, _) => LayoutTopCluster();
        windowButtons.HandleCreated += (_, _) => LayoutTopCluster();

        // 顶栏左侧：应用图标 + 品牌名
        var logo = new PictureBox
        {
            Location = new Point(48, 19),
            Size = new Size(26, 26),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
            Image = LoadAppIcon()?.ToBitmap(),
        };

        // 品牌位不再写死软件名，改为显示当前连接的芯片 / 接口 / 速率
        _brand = new Label
        {
            Text = "未连接",
            Location = new Point(80, 20),
            AutoSize = true,
            BackColor = Color.Transparent,
            ForeColor = DarkGeekTheme.TextMuted,
            Font = DarkGeekTheme.CreateFontPublic(10.5f, FontStyle.Bold),
        };

        _headerBar.Controls.AddRange(new Control[] { collapse, logo, _brand, windowButtons });

        var headerLine = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = DarkGeekTheme.Border };
        _headerBar.Controls.Add(headerLine);

        Controls.Add(_headerBar);
    }

    private void HeaderDrag(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, 0xA1, 0x2, 0);
    }

    private void BuildStatusBar()
    {
        var bar = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 30,
            BackColor = DarkGeekTheme.WindowBack,
            Padding = new Padding(12, 0, 12, 0),
        };
        bar.Paint += (_, e) =>
        {
            using var pen = new Pen(DarkGeekTheme.Border);
            e.Graphics.DrawLine(pen, 0, 0, bar.Width, 0);
        };

        _lblStatusLeft.Dock = DockStyle.Left;
        _lblStatusLeft.AutoSize = false;
        // 固定宽度收窄一档，给窄窗口下的「最近操作」留出空间；超长内容用省略号截断
        _lblStatusLeft.Width = 330;
        _lblStatusLeft.AutoEllipsis = true;
        _lblStatusLeft.TextAlign = ContentAlignment.MiddleLeft;
        _lblStatusLeft.ForeColor = DarkGeekTheme.TextSecondary;
        _lblStatusLeft.Font = DarkGeekTheme.UiFontSmall;
        _lblStatusLeft.Text = "● 未连接";

        _lblStatusMiddle.Dock = DockStyle.Left;
        _lblStatusMiddle.AutoSize = false;
        _lblStatusMiddle.Width = 470;
        _lblStatusMiddle.AutoEllipsis = true;
        _lblStatusMiddle.TextAlign = ContentAlignment.MiddleLeft;
        _lblStatusMiddle.ForeColor = DarkGeekTheme.TextMuted;
        _lblStatusMiddle.Font = DarkGeekTheme.UiFontSmall;
        _lblStatusMiddle.Text = "内存吞吐: 0.0 KB/s | RTT: 0 行 | 采样: 0Hz";

        _lblStatusRight.Dock = DockStyle.Fill;
        _lblStatusRight.AutoSize = false;
        _lblStatusRight.TextAlign = ContentAlignment.MiddleRight;
        _lblStatusRight.ForeColor = DarkGeekTheme.TextMuted;
        _lblStatusRight.Font = DarkGeekTheme.UiFontSmall;
        _lblStatusRight.Text = "最近操作: -";

        bar.Controls.AddRange(new Control[] { _lblStatusRight, _lblStatusMiddle, _lblStatusLeft });
        Controls.Add(bar);
    }

    private void BuildNavigation()
    {
        _nav.Dock = DockStyle.Left;
        _nav.AddItem(new NavItem { Key = "rtt", Icon = "💻", Title = "打印", Group = "调试工作台" });
        _nav.AddItem(new NavItem { Key = "watch", Icon = "👀", Title = "变量监控", Group = "调试工作台" });
        _nav.AddItem(new NavItem { Key = "svd", Icon = "⚙", Title = "寄存器", Group = "调试工作台" });
        _nav.AddItem(new NavItem { Key = "memory", Icon = "💾", Title = "内存", Group = "调试工作台" });
        _nav.AddItem(new NavItem { Key = "download", Icon = "⬇", Title = "程序下载", Group = "调试工作台" });
        _nav.AddItem(new NavItem { Key = "log", Icon = "📋", Title = "运行日志", Group = "调试工作台" });
        _nav.AddItem(new NavItem { Key = "settings", Icon = "🛠", Title = "设置", Group = "系统" });
        _nav.UserName = string.IsNullOrWhiteSpace(Environment.UserName) ? "JLink Engineer" : Environment.UserName;
        _nav.UserRole = "嵌入式工程师";
        // 版本号常驻侧边栏底部，任何子界面（含主页）都能看到当前构建版本
        _nav.VersionText = AppInfo.DisplayVersion;
        _nav.ItemSelected += (_, key) => Navigate(key);
        // 左侧顶部模式组：切换调试/查看模式（不销毁当前子界面）
        _nav.ModeRequested += (_, key) =>
        {
            _host.Mode = key == "inspect" ? StudioMode.Inspect : StudioMode.Debug;
            Context.Audit.Add("ui", "MODE", "/shell/mode",
                _host.Mode == StudioMode.Debug ? "切换到调试模式" : "切换到查看模式");
        };
        _nav.SetMode(_host.Mode == StudioMode.Inspect ? "inspect" : "debug");
        Controls.Add(_nav);
    }

    private void BuildPageHost()
    {
        var container = new Panel { Dock = DockStyle.Fill, BackColor = DarkGeekTheme.PageBack, Padding = new Padding(0) };

        var pageHeader = _pageHeader;
        pageHeader.Dock = DockStyle.Top;
        pageHeader.Height = 52;
        pageHeader.BackColor = DarkGeekTheme.PageBack;
        // 所有子页面都不再显示顶部标题行，内容直接铺满
        pageHeader.Visible = false;
        _lblPageTitle.AutoSize = true;
        _lblPageTitle.Location = new Point(20, 14);
        _lblPageTitle.Font = DarkGeekTheme.TitleFont;
        _lblPageTitle.ForeColor = DarkGeekTheme.TextPrimary;
        _lblPageTitle.BackColor = Color.Transparent;

        _lblPageSubtitle.AutoSize = false;
        _lblPageSubtitle.Dock = DockStyle.Right;
        _lblPageSubtitle.Width = 520;
        _lblPageSubtitle.TextAlign = ContentAlignment.MiddleRight;
        _lblPageSubtitle.Padding = new Padding(0, 0, 22, 0);
        _lblPageSubtitle.Font = DarkGeekTheme.UiFontSmall;
        _lblPageSubtitle.ForeColor = DarkGeekTheme.TextMuted;
        _lblPageSubtitle.BackColor = Color.Transparent;

        pageHeader.Controls.AddRange(new Control[] { _lblPageSubtitle, _lblPageTitle });

        _pageHost.Dock = DockStyle.Fill;
        _pageHost.BackColor = DarkGeekTheme.PageBack;

        container.Controls.Add(_pageHost);
        container.Controls.Add(pageHeader);
        Controls.Add(container);

        _settingsPage = new PageSettings(_host);
        var pages = new UserControl[]
        {
            new PageRttView(_host),
            new PageWatchView(_host),
            new PageSvdView(_host),
            new PageMemoryView(_host),
            new PageDownloadView(_host),
            new PageLogView(_host),
            _settingsPage,
        };

        string startPage = ResolveStartPage();

        foreach (var page in pages)
        {
            _router.RegisterPage(page);
            if (page is ISubPageView view && view.PageKey == startPage)
            {
                _lblPageTitle.Text = view.PageTitle;
            }
        }

        _router.PageChanged += (_, page) =>
        {
            if (page is not ISubPageView view) return;
            _lblPageTitle.Text = view.PageTitle;
            _lblPageSubtitle.Text = view is PageRttView
                ? "全屏沉浸式日志分析 · 后台采集不因切页中断"
                : view is PageWatchView
                    ? "结构体树状监控 · 高频波形实时投射"
                    : view is PageSvdView
                        ? "外设寄存器位域调试 · 安全 Read-Modify-Write"
                        : view is PageMemoryView
                            ? "RAM / 内部 Flash / 外部 Flash 十六进制查看与导出"
                            : view is PageDownloadView
                            ? "固件烧写 · J-Link Flash 算法 · .bin / .hex / .elf"
                            : view is PageLogView
                            ? "连接日志 · 工程事件 · 操作审计"
                            : view is PageSettings
                                ? "设备与工程配置 · 驱动与关于"
                                : "工程初始化 · 硬件连接与符号挂载";
        };

        _router.NavigateTo(startPage);
        _nav.SelectedKey = startPage;
        if (startPage == "settings" && !string.IsNullOrEmpty(_initialSubPage))
            _settingsPage.NavigateTo(_initialSubPage);
    }

    /// <summary>解析启动页：兼容历史的 --page=config（现迁移到设置页），支持 settings:device 指定设置子页。</summary>
    private string ResolveStartPage()
    {
        if (string.IsNullOrWhiteSpace(_initialPage)) return "rtt";

        var parts = _initialPage.Split(':', 2, StringSplitOptions.TrimEntries);
        _initialSubPage = parts.Length > 1 ? parts[1] : null;
        return parts[0].Equals("config", StringComparison.OrdinalIgnoreCase) ? "settings" : parts[0];
    }

    private void Navigate(string key)
    {
        _router.NavigateTo(key);
        _nav.SelectedKey = key;
    }

    /// <summary>打开设置页，可直连到指定子页（mode / device / integration / about）。</summary>
    private void OpenSettings(string? subPage = null)
    {
        Navigate("settings");
        if (!string.IsNullOrEmpty(subPage)) _settingsPage?.NavigateTo(subPage);
    }

    /// <summary>右上角连接按钮：在按钮下方展开连接配置子界面。</summary>
    private void ToggleConnectionPanel()
    {
        if (_connectionPanel.Visible)
        {
            _connectionPanel.Hide();
            return;
        }

        ShowConnectionPanel();
    }

    /// <summary>展开连接配置浮层（连接按钮下方，右边缘对齐）。</summary>
    internal void ShowConnectionPanel()
    {
        var corner = _btnConnect.PointToScreen(new Point(_btnConnect.Width, _btnConnect.Height));
        var area = Screen.FromControl(this).WorkingArea;

        int x = Math.Max(area.Left + 8, corner.X - _connectionPanel.Width);
        // 弹层加宽后仍需保证右边缘不越出屏幕
        x = Math.Min(x, Math.Max(area.Left + 8, area.Right - _connectionPanel.Width - 8));
        int y = corner.Y + 8;
        // 浮层比原来高，靠近屏幕底部时上移，避免底部被截断
        if (y + _connectionPanel.Height > area.Bottom)
            y = Math.Max(area.Top + 8, area.Bottom - _connectionPanel.Height - 8);

        _connectionPanel.ShowPanel(this, new Point(x, y));
    }

    /// <summary>J-Link 复位：向目标下发复位指令。</summary>
    private void ResetTarget()
    {
        var link = Context.Device.Link;
        if (link == null || !link.IsConnected)
        {
            MessageBox.Show(this, "目标未连接，无法执行复位。\r\n请先在「连接」中建立会话。",
                "J-Link 复位", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        bool ok = link.Reset(out _);
        Context.Audit.Add("ui", "RESET", "/jlink/reset", ok ? "复位指令已下发" : "复位失败");

        if (!ok)
        {
            MessageBox.Show(this, "复位指令下发失败，请检查调试器连接状态。",
                "J-Link 复位", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyWindowRegion();
        RefreshShell();
    }

    private void RefreshShell()
    {
        try
        {
            var device = Context.Device;

            long now = Environment.TickCount64;
            long bytes = device.Link?.TotalBytesRead ?? 0;
            double elapsed = Math.Max(0.001, (now - _lastTick) / 1000.0);
            double kbPerSecond = _lastTick == 0 ? 0 : (bytes - _lastBytesRead) / elapsed / 1024.0;
            _lastBytesRead = bytes;
            _lastTick = now;

            _lblStatusLeft.Text = device.IsConnected
                ? $"● {device.LinkDescription}"
                : $"● 未连接{(string.IsNullOrEmpty(device.LastError) ? string.Empty : " · " + device.LastError)}";
            _lblStatusLeft.ForeColor = device.IsConnected ? DarkGeekTheme.Success : DarkGeekTheme.TextMuted;

            // 顶栏：芯片型号 · 接口 · 速率（未连接时用灰色，仍显示已配置的目标）
            var config = device.Config;
            string chip = string.IsNullOrWhiteSpace(config.Device) ? "未选择芯片" : config.Device;
            string speed = config.SpeedKhz >= 1000
                ? $"{config.SpeedKhz / 1000.0:0.##} MHz"
                : $"{config.SpeedKhz} kHz";
            string transport = config.Interface.ToString();

            string brandText = $"{chip} · {transport} · {speed}";
            if (_brand.Text != brandText) _brand.Text = brandText;
            _brand.ForeColor = device.IsConnected ? DarkGeekTheme.TextPrimary : DarkGeekTheme.TextMuted;

            // 侧边栏品牌区同步显示：主标题=芯片，副标题=接口·速率
            string sidebarSubtitle = $"{transport} · {speed}";
            if (_nav.BrandTitle != chip || _nav.BrandSubtitle != sidebarSubtitle)
            {
                _nav.BrandTitle = chip;
                _nav.BrandSubtitle = sidebarSubtitle;
                _nav.Invalidate();
            }

            var recentErrors = Context.Rtt.Recent(300).Count(l => l.Level == RttLevel.Error);
            _lblStatusMiddle.Text =
                $"内存吞吐: {kbPerSecond:F1} KB/s | RTT: {Context.Rtt.TotalLines} 行 | 采样: {Context.Variables.SampleRateHz:F0}Hz | " +
                $"监控: {Context.Variables.ActiveLeafCount} 项 | 缓冲告警: {recentErrors}";

            var lastAudit = Context.Audit.Recent(1).FirstOrDefault();
            _lblStatusRight.Text = lastAudit != null
                ? $"最近操作: {lastAudit.Method} {lastAudit.Path} · {lastAudit.TimeText}"
                : "最近操作: -";

            _nav.SetBadge("rtt", recentErrors > 0 ? recentErrors.ToString() : null);
        }
        catch
        {
            // 界面刷新失败不应影响主流程
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // 窗口尺寸变化时收起连接浮层，避免锚点错位
        if (_connectionPanel is { Visible: true }) _connectionPanel.Visible = false;
        ApplyWindowRegion();
    }

    /// <summary>把无边框窗口裁成圆角；最大化时恢复直角，避免屏幕边缘出现缺口。</summary>
    private void ApplyWindowRegion()
    {
        if (!IsHandleCreated || IsDisposed) return;

        if (WindowState == FormWindowState.Maximized)
        {
            var old = Region;
            Region = null;
            old?.Dispose();
            Invalidate();
            return;
        }

        using var path = DarkGeekTheme.RoundedRect(new Rectangle(0, 0, Width, Height), RadiusPx);
        var previous = Region;
        Region = new Region(path);
        previous?.Dispose();
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // 最大化时整块铺底，避免四角出现缺口
        e.Graphics.Clear(DarkGeekTheme.WindowBack);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // 无边框窗口没有系统边框，自绘 1px 圆角描边
        if (WindowState == FormWindowState.Maximized) return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(DarkGeekTheme.Border);

        // 描边内缩 1px：与 Region 裁剪边界错开，避免圆角处描边被裁掉半条形成毛边
        int radius = Math.Max(2, RadiusPx - 1);
        using var path = DarkGeekTheme.RoundedRect(
            new Rectangle(1, 1, Math.Max(2, Width - 3), Math.Max(2, Height - 3)), radius);
        e.Graphics.DrawPath(pen, path);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            // 无边框窗口补一层系统阴影：圆角边缘有柔和过渡，减轻 Region 硬裁剪的锯齿感
            parameters.ClassStyle |= 0x20000; // CS_DROPSHADOW
            return parameters;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST && WindowState == FormWindowState.Normal)
        {
            base.WndProc(ref m);
            if ((int)m.Result == 1)
            {
                // WM_NCHITTEST 的屏幕坐标为有符号 16 位（负值出现在主屏左侧/上方显示器），
                // 必须做符号扩展，否则边缘判定在多显示器下错位
                int ncX = (short)(m.LParam.ToInt64() & 0xFFFF);
                int ncY = (short)((m.LParam.ToInt64() >> 16) & 0xFFFF);
                var pos = PointToClient(new Point(ncX, ncY));
                const int grip = 6;
                bool left = pos.X <= grip, right = pos.X >= ClientSize.Width - grip;
                bool top = pos.Y <= grip, bottom = pos.Y >= ClientSize.Height - grip;

                m.Result = (IntPtr)((left && top) ? HTTOPLEFT
                    : (top && right) ? HTTOPRIGHT
                    : (left && bottom) ? HTBOTTOMLEFT
                    : (right && bottom) ? HTBOTTOMRIGHT
                    : left ? HTLEFT
                    : right ? HTRIGHT
                    : top ? HTTOP
                    : bottom ? HTBOTTOM
                    : 1);
            }
            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _uiTimer.Stop();
        _uiTimer.Dispose();
        try
        {
            _host.Dispose();
        }
        catch
        {
            // ignore
        }
        base.OnFormClosed(e);
    }
}
