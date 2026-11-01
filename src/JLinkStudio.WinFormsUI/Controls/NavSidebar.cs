using System.Drawing.Drawing2D;
using JLinkStudio.WinFormsUI.Theme;

namespace JLinkStudio.WinFormsUI.Controls;

public sealed class NavItem
{
    public string Key { get; init; } = string.Empty;
    public string Icon { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    /// <summary>分组标题；相邻且同组的菜单项会显示在同一个分组下。</summary>
    public string? Group { get; init; }
    public string? Badge { get; set; }
    public Color BadgeColor { get; set; } = DarkGeekTheme.Danger;
}

/// <summary>
/// 固定侧边栏（Fixed Sidebar）：220px 常驻 / 64px 折叠，
/// 四分区结构「品牌 → 分组导航 → 用户区」，
/// 激活态为淡蓝底 + 2px 左侧实心强调线，动效仅保留 2px 级微位移。
/// </summary>
public sealed class NavSidebar : Control
{
    private const int BrandHeight = 64;
    private const int NavTopPadding = 10;
    private const int ItemHeight = 38;
    private const int ItemGap = 4;
    private const int GroupHeight = 26;
    private const int UserHeight = 66;

    /// <summary>版本号用的加粗小字号，静态复用避免每帧新建字体。</summary>
    private static readonly Font VersionFont = DarkGeekTheme.CreateFontPublic(8f, FontStyle.Bold);
    private const int IconShift = 2;
    private const int IconSize = 17;

    /// <summary>导航图标字号（比品牌图标小一号，与 9.75pt 的菜单文字更协调）。</summary>
    private static readonly Font NavIconFont = DarkGeekTheme.CreateFontPublic(9f, FontStyle.Regular);

    // 模式组（顶部）：调试模式 / 查看模式，互斥
    private const int ModeCardTop = 10;
    private const int ModeRowHeight = 32;
    private const int ModeCardHeight = ModeRowHeight * 2 + 12;
    private const int ModeCardGap = 6;

    private readonly List<NavItem> _items = new();
    private readonly List<(NavItem Item, Rectangle Bounds)> _hitAreas = new();
    private Rectangle _userRect;
    private Rectangle _modeCardRect;
    private Rectangle _modeDebugRect;
    private Rectangle _modeInspectRect;
    private int _hoverIndex = -1;
    private string _selectedKey = string.Empty;

    public NavSidebar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = DarkGeekTheme.SidebarBack;
        Width = 220;
    }

    public IReadOnlyList<NavItem> Items => _items;
    public bool Collapsed { get; private set; }
    public int ExpandedWidth { get; set; } = 220;
    public int CollapsedWidth { get; set; } = 64;
    public string UserName { get; set; } = "JLink Engineer";
    public string UserRole { get; set; } = "Embedded Dev";

    /// <summary>侧边栏底部右侧的版本标识（空则不绘制）。</summary>
    public string VersionText { get; set; } = string.Empty;

    /// <summary>品牌区主标题（默认软件名，运行时改为当前芯片型号）。</summary>
    public string BrandTitle { get; set; } = "JLink-Studio-Pro";

    /// <summary>品牌区副标题（运行时显示「接口 · 速率」）。</summary>
    public string BrandSubtitle { get; set; } = string.Empty;

    public event EventHandler<string>? ItemSelected;

    /// <summary>用户点击模式组（debug / inspect）。</summary>
    public event EventHandler<string>? ModeRequested;

    /// <summary>当前工作模式：debug / inspect，与 <c>AppHost.Mode</c> 保持同步。</summary>
    public string ModeKey { get; private set; } = "debug";

    public void SetMode(string key)
    {
        if (ModeKey == key) return;
        ModeKey = key;
        Invalidate();
    }

    public string SelectedKey
    {
        get => _selectedKey;
        set
        {
            if (_selectedKey == value) return;
            _selectedKey = value;
            Invalidate();
        }
    }

    public void AddItem(NavItem item)
    {
        _items.Add(item);
        RebuildLayout();
        Invalidate();
    }

    public void SetBadge(string key, string? badge, Color? color = null)
    {
        var item = _items.FirstOrDefault(i => i.Key == key);
        if (item == null) return;
        if (item.Badge == badge && color == null) return;
        item.Badge = badge;
        if (color.HasValue) item.BadgeColor = color.Value;
        Invalidate();
    }

    public void ToggleCollapse()
    {
        Collapsed = !Collapsed;
        Width = Collapsed ? CollapsedWidth : ExpandedWidth;
        RebuildLayout();
        Invalidate();
    }

    private void RebuildLayout()
    {
        _hitAreas.Clear();

        // 模式组占据品牌区下方固定高度，导航从它下面开始
        _modeCardRect = new Rectangle(8, BrandHeight + ModeCardTop, Math.Max(24, Width - 16), ModeCardHeight);
        int rowLeft = _modeCardRect.X + 4;
        int rowWidth = Math.Max(16, _modeCardRect.Width - 8);
        _modeDebugRect = new Rectangle(rowLeft, _modeCardRect.Y + 4, rowWidth, ModeRowHeight);
        _modeInspectRect = new Rectangle(rowLeft, _modeDebugRect.Bottom + 4, rowWidth, ModeRowHeight);

        int y = _modeCardRect.Bottom + ModeCardGap + NavTopPadding;
        string? currentGroup = null;

        foreach (var item in _items)
        {
            if (!Collapsed && !string.Equals(currentGroup, item.Group, StringComparison.Ordinal))
            {
                currentGroup = item.Group;
                if (item.Group != null) y += GroupHeight;
            }

            _hitAreas.Add((item, new Rectangle(8, y, Width - 16, ItemHeight)));
            y += ItemHeight + ItemGap;
        }

        _userRect = new Rectangle(0, Height - UserHeight, Width, UserHeight);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        RebuildLayout();
    }

    private int HitIndex(Point point)
    {
        for (int i = 0; i < _hitAreas.Count; i++)
        {
            if (_hitAreas[i].Bounds.Contains(point)) return i;
        }
        return -1;
    }

    private bool IsOverMode(Point point) =>
        _modeDebugRect.Contains(point) || _modeInspectRect.Contains(point);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int index = HitIndex(e.Location);
        bool overMode = IsOverMode(e.Location);
        if (index != _hoverIndex || Cursor != (index >= 0 || overMode ? Cursors.Hand : Cursors.Default))
        {
            _hoverIndex = index;
            Cursor = index >= 0 || overMode ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hoverIndex = -1;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        // 模式组优先响应：切换工作模式（不销毁当前子界面）
        if (IsOverMode(e.Location))
        {
            string key = _modeDebugRect.Contains(e.Location) ? "debug" : "inspect";
            if (ModeKey != key)
            {
                ModeKey = key;
                Invalidate();
            }
            ModeRequested?.Invoke(this, key);
            base.OnMouseClick(e);
            return;
        }

        int index = HitIndex(e.Location);
        if (index >= 0)
        {
            var item = _hitAreas[index].Item;
            SelectedKey = item.Key;
            ItemSelected?.Invoke(this, item.Key);
        }
        base.OnMouseClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        RebuildLayout();
        DrawBrand(g);
        DrawModeSection(g);

        // 分组小标题在每组首个菜单项上方绘制
        string? lastGroup = null;
        for (int i = 0; i < _hitAreas.Count; i++)
        {
            var (item, bounds) = _hitAreas[i];
            if (!Collapsed && !string.Equals(lastGroup, item.Group, StringComparison.Ordinal))
            {
                lastGroup = item.Group;
                if (item.Group != null)
                    DrawGroupHeader(g, item.Group, bounds.Y - GroupHeight + 6);
            }

            DrawItem(g, item, bounds, i == _hoverIndex, item.Key == _selectedKey);
        }

        DrawUser(g);

        using var borderPen = new Pen(DarkGeekTheme.SidebarBorder);
        g.DrawLine(borderPen, Width - 1, 0, Width - 1, Height);
    }

    private void DrawBrand(Graphics g)
    {
        if (Collapsed)
        {
            TextRenderer.DrawText(g, "⚡", DarkGeekTheme.IconFont,
                new Rectangle(0, 14, Width, 36), DarkGeekTheme.Primary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        else
        {
            TextRenderer.DrawText(g, "⚡", DarkGeekTheme.IconFont,
                new Rectangle(14, 14, 28, 36), DarkGeekTheme.Primary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            // 主标题：芯片型号；有副标题时上移，副标题显示接口与速率
            var titleRect = string.IsNullOrEmpty(BrandSubtitle)
                ? new Rectangle(44, 14, Width - 52, 36)
                : new Rectangle(44, 11, Width - 52, 22);

            TextRenderer.DrawText(g, BrandTitle, DarkGeekTheme.CreateFontPublic(11f, FontStyle.Bold),
                titleRect, DarkGeekTheme.TextPrimary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

            if (!string.IsNullOrEmpty(BrandSubtitle))
            {
                TextRenderer.DrawText(g, BrandSubtitle, DarkGeekTheme.CreateFontPublic(8.5f, FontStyle.Regular),
                    new Rectangle(44, 31, Width - 52, 18), DarkGeekTheme.TextMuted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }
        }

        using var line = new Pen(DarkGeekTheme.SidebarBorder);
        g.DrawLine(line, 0, BrandHeight - 1, Width, BrandHeight - 1);
    }

    /// <summary>顶部模式组：调试模式 / 查看模式，互斥，激活项带蓝底与 2px 强调线。</summary>
    private void DrawModeSection(Graphics g)
    {
        using (var path = DarkGeekTheme.RoundedRect(_modeCardRect, 8))
        using (var brush = new SolidBrush(DarkGeekTheme.CardBackHover))
            g.FillPath(brush, path);

        using (var pen = new Pen(DarkGeekTheme.SidebarBorder))
        using (var path = DarkGeekTheme.RoundedRect(_modeCardRect, 8))
            g.DrawPath(pen, path);

        DrawModeRow(g, _modeDebugRect, "debug", "调试模式", DarkGeekTheme.Warning);
        DrawModeRow(g, _modeInspectRect, "inspect", "查看模式", DarkGeekTheme.Success);
    }

    private void DrawModeRow(Graphics g, Rectangle bounds, string key, string title, Color dotColor)
    {
        bool active = string.Equals(ModeKey, key, StringComparison.OrdinalIgnoreCase);

        if (active)
        {
            using var path = DarkGeekTheme.RoundedRect(bounds, 6);
            using var brush = new SolidBrush(DarkGeekTheme.NavActiveBack);
            g.FillPath(brush, path);

            using var accent = new SolidBrush(DarkGeekTheme.Primary);
            g.FillRectangle(accent, bounds.X, bounds.Y + 7, 2, bounds.Height - 14);
        }

        int dotSize = 8;
        int dotX = Collapsed ? (Width - dotSize) / 2 : bounds.X + 14;
        using (var dot = new SolidBrush(dotColor))
            g.FillEllipse(dot, dotX, bounds.Y + (bounds.Height - dotSize) / 2, dotSize, dotSize);

        if (Collapsed) return;

        TextRenderer.DrawText(g, title,
            active ? DarkGeekTheme.CreateFontPublic(9.5f, FontStyle.Bold) : DarkGeekTheme.UiFont,
            new Rectangle(bounds.X + 30, bounds.Y, bounds.Width - 34, bounds.Height),
            active ? DarkGeekTheme.NavActiveText : DarkGeekTheme.NavText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
            TextFormatFlags.EndEllipsis);
    }

    private void DrawGroupHeader(Graphics g, string title, int y)
    {
        TextRenderer.DrawText(g, title.ToUpperInvariant(),
            DarkGeekTheme.CreateFontPublic(8f, FontStyle.Bold),
            new Rectangle(14, y, Width - 24, 18),
            DarkGeekTheme.NavGroupText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private void DrawItem(Graphics g, NavItem item, Rectangle bounds, bool hover, bool selected)
    {
        if (selected || hover)
        {
            using var path = DarkGeekTheme.RoundedRect(bounds, 6);
            using var brush = new SolidBrush(selected ? DarkGeekTheme.NavActiveBack : DarkGeekTheme.CardBackHover);
            g.FillPath(brush, path);
        }

        // 2px 实心左侧强调线：折叠态同样保留，作为“我在哪”的永久锚点
        if (selected)
        {
            using var accent = new SolidBrush(DarkGeekTheme.Primary);
            g.FillRectangle(accent, bounds.X, bounds.Y + 9, 2, bounds.Height - 18);
        }

        int shift = hover && !selected ? IconShift : 0;
        var iconRect = Collapsed
            ? new Rectangle((Width - IconSize) / 2 + shift, bounds.Y + (bounds.Height - IconSize) / 2, IconSize, IconSize)
            : new Rectangle(15 + shift, bounds.Y + (bounds.Height - IconSize) / 2, IconSize, IconSize);

        TextRenderer.DrawText(g, item.Icon, NavIconFont, iconRect,
            selected ? DarkGeekTheme.NavActiveText : DarkGeekTheme.NavTextMuted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        if (Collapsed)
        {
            if (!string.IsNullOrEmpty(item.Badge))
            {
                using var dot = new SolidBrush(item.BadgeColor);
                g.FillEllipse(dot, Width - 16, bounds.Y + 8, 7, 7);
            }
            return;
        }

        TextRenderer.DrawText(g, item.Title, DarkGeekTheme.UiFont,
            new Rectangle(bounds.X + 44, bounds.Y, bounds.Width - 44, bounds.Height),
            selected ? DarkGeekTheme.NavActiveText : DarkGeekTheme.NavText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
            TextFormatFlags.EndEllipsis);

        if (!string.IsNullOrEmpty(item.Badge))
        {
            var size = TextRenderer.MeasureText(g, item.Badge, DarkGeekTheme.UiFontSmall, Size.Empty,
                TextFormatFlags.NoPadding);
            int width = Math.Max(20, size.Width + 10);
            var badgeRect = new Rectangle(bounds.Right - width - 8, bounds.Y + (bounds.Height - 18) / 2, width, 18);
            using var path = DarkGeekTheme.RoundedRect(badgeRect, 9);
            using var brush = new SolidBrush(item.BadgeColor);
            g.FillPath(brush, path);
            TextRenderer.DrawText(g, item.Badge, DarkGeekTheme.UiFontSmall, badgeRect, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    private void DrawUser(Graphics g)
    {
        using var line = new Pen(DarkGeekTheme.SidebarBorder);
        g.DrawLine(line, 0, _userRect.Y, Width, _userRect.Y);

        int avatarSize = 30;
        int avatarX = Collapsed ? (Width - avatarSize) / 2 : 14;
        var avatarRect = new Rectangle(avatarX, _userRect.Y + 18, avatarSize, avatarSize);

        using (var path = new GraphicsPath())
        {
            path.AddEllipse(avatarRect);
            using var brush = new LinearGradientBrush(avatarRect, DarkGeekTheme.Primary, DarkGeekTheme.PrimaryDim, 45f);
            g.FillPath(brush, path);
        }

        TextRenderer.DrawText(g, "JL", DarkGeekTheme.CreateFontPublic(8.5f, FontStyle.Bold), avatarRect, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        if (Collapsed) return;

        TextRenderer.DrawText(g, UserName, DarkGeekTheme.CreateFontPublic(9.5f, FontStyle.Bold),
            new Rectangle(52, _userRect.Y + 14, Width - 60, 20), DarkGeekTheme.TextPrimary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        TextRenderer.DrawText(g, UserRole, DarkGeekTheme.UiFontSmall,
            new Rectangle(52, _userRect.Y + 34, Width - 60, 18), DarkGeekTheme.NavTextMuted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        // 版本号单独一行显示在「角色」下面，加粗，任何子界面（含主页）都常驻可见
        if (!string.IsNullOrEmpty(VersionText))
        {
            TextRenderer.DrawText(g, VersionText, VersionFont,
                new Rectangle(52, _userRect.Y + 48, Width - 60, 16), DarkGeekTheme.Primary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
