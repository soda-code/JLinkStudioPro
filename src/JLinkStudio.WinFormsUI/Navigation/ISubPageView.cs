namespace JLinkStudio.WinFormsUI.Navigation;

/// <summary>右侧子界面统一生命周期接口。</summary>
public interface ISubPageView
{
    /// <summary>路由键（与左侧菜单项 Key 对应）。</summary>
    string PageKey { get; }

    /// <summary>子界面标题。</summary>
    string PageTitle { get; }

    /// <summary>切到当前页面时触发（恢复 UI 定时刷新）。</summary>
    void OnPageActivated();

    /// <summary>切走隐藏时触发（暂停 UI 重绘，后台采集继续）。</summary>
    void OnPageHidden();
}
