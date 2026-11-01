using System.Drawing.Drawing2D;
using JLinkStudio.WinFormsUI.Theme;

namespace JLinkStudio.WinFormsUI.Controls;

/// <summary>
/// 圆角容器：通过 Region 裁剪自身与内部控件，让表格 / 列表 / 日志等原生控件也能呈现圆角。
/// </summary>
public sealed class RoundedFrame : Panel
{
    private bool _hover;

    public RoundedFrame()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = DarkGeekTheme.InputBack;
        Padding = new Padding(1);
        Radius = 6;
        BorderColor = DarkGeekTheme.Border;
        ClipChildren = true;
    }

    public int Radius { get; set; }

    public Color BorderColor { get; set; }

    /// <summary>是否把内部子控件一并裁剪为圆角。</summary>
    public bool ClipChildren { get; set; }

    public bool HighlightOnHover { get; set; }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyRegion();
        ApplyChildRegions();
    }

    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        e.Control.Resize += (_, _) => ApplyChildRegions();
        ApplyChildRegions();
    }

    private void ApplyRegion()
    {
        if (Width <= 0 || Height <= 0) return;
        using var path = DarkGeekTheme.RoundedRect(new Rectangle(0, 0, Width, Height), Radius);
        var old = Region;
        Region = new Region(path);
        old?.Dispose();
    }

    private void ApplyChildRegions()
    {
        if (!ClipChildren) return;

        foreach (Control child in Controls)
        {
            if (child.Width <= 0 || child.Height <= 0) continue;
            int radius = Math.Max(1, Radius - 2);
            using var path = DarkGeekTheme.RoundedRect(new Rectangle(0, 0, child.Width, child.Height), radius);
            var old = child.Region;
            child.Region = new Region(path);
            old?.Dispose();
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        if (HighlightOnHover) Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        if (HighlightOnHover) Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = DarkGeekTheme.RoundedRect(rect, Radius);

        var border = HighlightOnHover && _hover
            ? DarkGeekTheme.Blend(BorderColor, DarkGeekTheme.Primary, 0.6)
            : BorderColor;

        using var pen = new Pen(border);
        g.DrawPath(pen, path);
    }
}
