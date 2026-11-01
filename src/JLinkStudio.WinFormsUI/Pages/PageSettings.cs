using JLinkStudio.Core.Services;
using JLinkStudio.WinFormsUI.Controls;
using JLinkStudio.WinFormsUI.Navigation;
using JLinkStudio.WinFormsUI.Theme;

namespace JLinkStudio.WinFormsUI.Pages;

/// <summary>
/// 设置子界面：左侧二级页签 + 右侧内容区。
/// 子页签依次为「设备与工程配置 → 驱动与关于」。
/// </summary>
public sealed class PageSettings : UserControl, ISubPageView
{
    private const string KeyDevice = "device";
    private const string KeyAbout = "about";

    private readonly AppHost _host;
    private readonly SubTabStrip _tabs = new();
    private readonly Panel _contentHost = new();
    private readonly PageDeviceConfig _devicePage;
    private readonly Dictionary<string, Control> _views = new(StringComparer.OrdinalIgnoreCase);

    private Label _lblDriver = null!;

    public PageSettings(AppHost host)
    {
        _host = host;
        Dock = DockStyle.Fill;
        BackColor = DarkGeekTheme.PageBack;
        Padding = new Padding(16, 4, 16, 12);

        _devicePage = new PageDeviceConfig(host);

        _tabs.Dock = DockStyle.Top;
        // 运行模式已上移到左侧菜单顶部，设置页不再保留该页签
        _tabs.AddItem(new SubTabItem { Key = KeyDevice, Icon = "🔌", Title = "设备与工程配置" });
        _tabs.AddItem(new SubTabItem { Key = KeyAbout, Icon = "ℹ", Title = "驱动与关于" });
        _tabs.SelectedKey = KeyDevice;
        _tabs.ItemSelected += (_, key) => NavigateTo(key);

        _contentHost.Dock = DockStyle.Fill;
        _contentHost.BackColor = Color.Transparent;
        _contentHost.Padding = new Padding(0, 10, 0, 0);

        RegisterView(KeyDevice, BuildDeviceView());
        RegisterView(KeyAbout, BuildAboutView());

        // 整页只有一张卡片：页签与内容区都放在卡片内部，内部分区代替多张卡片
        var pageCard = Ui.PageCard();
        pageCard.Controls.Add(_contentHost);
        pageCard.Controls.Add(_tabs);
        Controls.Add(pageCard);

        ShowView(KeyDevice);
    }

    private StudioContext Ctx => _host.Context;

    public string PageKey => "settings";

    public string PageTitle => "⚙ 设置";

    public void OnPageActivated()
    {
        RefreshDynamic();
        if (_tabs.SelectedKey == KeyDevice) _devicePage.OnPageActivated();
    }

    public void OnPageHidden() { }

    /// <summary>跳转到指定子页签（供右上角连接菜单等外部入口使用）。</summary>
    public void NavigateTo(string key)
    {
        if (!_views.ContainsKey(key)) return;
        _tabs.SelectedKey = key;
        ShowView(key);
    }

    private void RegisterView(string key, Control view)
    {
        view.Dock = DockStyle.Fill;
        view.Visible = false;
        _views[key] = view;
        _contentHost.Controls.Add(view);
    }

    private void ShowView(string key)
    {
        _contentHost.SuspendLayout();
        try
        {
            foreach (var (name, view) in _views)
                view.Visible = string.Equals(name, key, StringComparison.OrdinalIgnoreCase);

            if (string.Equals(key, KeyDevice, StringComparison.OrdinalIgnoreCase))
                _devicePage.OnPageActivated();

            RefreshDynamic();
        }
        finally
        {
            _contentHost.ResumeLayout(true);
        }
    }

    // ── 子页 1：设备与工程配置 ─────────────────────────────────────────────

    private Control BuildDeviceView()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        host.Controls.Add(_devicePage);
        return host;
    }

    // ── 子页 2：驱动与关于 ─────────────────────────────────────────────────

    private Control BuildAboutView()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
        };
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 200));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var driverCard = Ui.Section("J-Link 驱动", "JLink_x64.dll");
        driverCard.Margin = new Padding(0, 0, 0, 10);
        var body = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };

        _lblDriver = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            ForeColor = DarkGeekTheme.TextSecondary,
            BackColor = Color.Transparent,
            Font = DarkGeekTheme.MonoFontSmall,
        };

        var rowButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 44,
            WrapContents = false,
            BackColor = Color.Transparent,
        };
        var btnDir = Ui.Button("选择 J-Link 驱动目录…", ButtonKind.Ghost, (_, _) => ChooseJLinkDirectory(), 190);
        btnDir.Height = 34;
        rowButtons.Controls.Add(btnDir);

        body.Controls.Add(_lblDriver);
        body.Controls.Add(rowButtons);
        driverCard.Controls.Add(body);

        var aboutCard = Ui.Section("关于 JLink-Studio-Pro", "v1.0.0");
        aboutCard.Margin = new Padding(0, 10, 0, 0);
        var about = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            ForeColor = DarkGeekTheme.TextSecondary,
            BackColor = Color.Transparent,
            Font = DarkGeekTheme.UiFontSmall,
            Text =
                "J-Link 智能调试上位机\r\n" +
                "· 打印 · 变量监控 · SVD 寄存器视图\r\n\r\n" +
                ".NET 8 · WinForms · ScottPlot 5",
        };
        aboutCard.Controls.Add(about);

        grid.Controls.Add(driverCard, 0, 0);
        grid.Controls.Add(aboutCard, 0, 1);
        return grid;
    }

    private void ChooseJLinkDirectory()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择 SEGGER J-Link 安装目录（目录内需包含 JLink_x64.dll）",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
            SelectedPath = JLinkStudio.Core.Interop.JLinkLibraryLocator.Directory
                           ?? JLinkStudio.Core.Interop.JLinkLibraryLocator.DefaultInstallDirectory,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        JLinkStudio.Core.Interop.JLinkLibraryLocator.Configure(dialog.SelectedPath);
        var path = JLinkStudio.Core.Interop.JLinkLibraryLocator.DllPath;
        MessageBox.Show(this,
            path == null
                ? "所选目录下未找到 JLink_x64.dll，请确认路径是否正确。"
                : $"驱动已切换到：\r\n{path}\r\n\r\n已加载的模块需重新连接后生效。",
            "J-Link 驱动目录", MessageBoxButtons.OK,
            path == null ? MessageBoxIcon.Warning : MessageBoxIcon.Information);

        RefreshDynamic();
    }

    // ── 动态信息刷新 ───────────────────────────────────────────────────────

    private void RefreshDynamic()
    {
        if (_lblDriver != null)
        {
            var driver = DeviceManager.IsDriverAvailable
                ? $"SEGGER J-Link {DeviceManager.DriverVersion} 已就绪"
                : "未找到 J-Link SDK，请在下方指定驱动目录";
            _lblDriver.Text =
                $"驱动：{driver}\r\n" +
                $"路径：{DeviceManager.DriverPath ?? "未加载"}\r\n" +
                $"配置目录：{JLinkStudio.Core.Interop.JLinkLibraryLocator.Directory ?? "未指定（使用自动探测）"}";
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }
}
