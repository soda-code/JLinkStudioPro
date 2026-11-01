namespace JLinkStudio.WinFormsUI.Navigation;

/// <summary>右侧子界面路由：单例缓存 + 无闪烁切换（保留滚动位置与波形状态）。</summary>
public sealed class PageRouter
{
    private readonly Control _hostContainer;
    private readonly Dictionary<string, UserControl> _pageCache = new(StringComparer.OrdinalIgnoreCase);
    private UserControl? _currentPage;

    public PageRouter(Control hostContainer)
    {
        _hostContainer = hostContainer;
    }

    public string? CurrentKey { get; private set; }
    public event EventHandler<UserControl>? PageChanged;

    public void RegisterPage(UserControl page)
    {
        var key = page is ISubPageView view ? view.PageKey : page.Name;
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("页面必须提供 PageKey。");

        page.Dock = DockStyle.Fill;
        page.Visible = false;
        _pageCache[key] = page;
        // 第一页需要先显示，因此允许控件在不可见状态下创建句柄
        _hostContainer.Controls.Add(page);
    }

    public void NavigateTo(string key)
    {
        if (!_pageCache.TryGetValue(key, out var targetPage)) return;
        if (ReferenceEquals(_currentPage, targetPage)) return;

        _hostContainer.SuspendLayout();
        try
        {
            if (_currentPage is ISubPageView oldView) oldView.OnPageHidden();
            if (_currentPage != null) _currentPage.Visible = false;

            targetPage.Visible = true;
            targetPage.BringToFront();
            _currentPage = targetPage;
            CurrentKey = key;

            if (targetPage is ISubPageView newView) newView.OnPageActivated();
        }
        finally
        {
            _hostContainer.ResumeLayout(true);
        }

        PageChanged?.Invoke(this, targetPage);
    }

    public T? GetPage<T>(string key) where T : UserControl =>
        _pageCache.TryGetValue(key, out var page) ? page as T : null;
}
