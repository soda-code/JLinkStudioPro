using System.Drawing.Drawing2D;
using JLinkStudio.WinFormsUI.Theme;

namespace JLinkStudio.WinFormsUI.Controls;

public sealed class SubTabItem
{
    public string Key { get; init; } = string.Empty;
    public string Icon { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
}

/// <summary>设置页顶部的横向页签条：激活态为淡蓝底 + 底部 2px 强调线。</summary>
public sealed class SubTabStrip : Control
{
    private const int ItemHeight = 40;
    private const int ItemGap = 6;
    private const int IconWidth = 24;
    private const int TextLeft = 40;
    private const int HorizontalPadding = 14;

    private readonly List<(SubTabItem Item, Rectangle Bounds)> _hitAreas = new();
    private string _selectedKey = string.Empty;
    private int _hoverIndex = -1;

    public SubTabStrip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = DarkGeekTheme.PageBack;
        Height = 46;
    }

    public event EventHandler<string>? ItemSelected;

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

    public void AddItem(SubTabItem item)
    {
        _hitAreas.Add((item, Rectangle.Empty));
        RebuildLayout();
        Invalidate();
    }

    private void RebuildLayout()
    {
        int x = 0;
        for (int i = 0; i < _hitAreas.Count; i++)
        {
            var item = _hitAreas[i].Item;
            int width = TextRenderer.MeasureText(item.Title, DarkGeekTheme.UiFont).Width + TextLeft + HorizontalPadding;
            _hitAreas[i] = (item, new Rectangle(x, 4, width, ItemHeight));
            x += width + ItemGap;
        }
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

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int index = HitIndex(e.Location);
        if (index != _hoverIndex)
        {
            _hoverIndex = index;
            Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
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

        for (int i = 0; i < _hitAreas.Count; i++)
        {
            var (item, bounds) = _hitAreas[i];
            bool selected = item.Key == _selectedKey;
            bool hover = i == _hoverIndex;

            if (selected || hover)
            {
                using var path = DarkGeekTheme.RoundedRect(bounds, 6);
                using var brush = new SolidBrush(selected ? DarkGeekTheme.NavActiveBack : DarkGeekTheme.CardBackHover);
                g.FillPath(brush, path);
            }

            var fore = selected ? DarkGeekTheme.NavActiveText : DarkGeekTheme.NavTextMuted;

            TextRenderer.DrawText(g, item.Icon, DarkGeekTheme.IconFont,
                new Rectangle(bounds.X + 10, bounds.Y + (bounds.Height - 22) / 2, IconWidth, 22), fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            TextRenderer.DrawText(g, item.Title, DarkGeekTheme.UiFont,
                new Rectangle(bounds.X + TextLeft, bounds.Y, bounds.Width - TextLeft - 6, bounds.Height),
                selected ? DarkGeekTheme.NavActiveText : DarkGeekTheme.NavText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            if (selected)
            {
                using var accent = new SolidBrush(DarkGeekTheme.Primary);
                g.FillRectangle(accent, bounds.X + 8, bounds.Bottom - 2, bounds.Width - 16, 2);
            }
        }

        using var line = new Pen(DarkGeekTheme.Border);
        g.DrawLine(line, 0, Height - 1, Width, Height - 1);
    }
}
