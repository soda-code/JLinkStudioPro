using System.Drawing.Drawing2D;
using JLinkStudio.WinFormsUI.Theme;

namespace JLinkStudio.WinFormsUI.Controls;

public enum ButtonKind
{
    Primary,
    Ghost,
    Success,
    Danger,
}

/// <summary>暗色扁平按钮（圆角、悬停高亮、无系统边框）。</summary>
public sealed class FlatButton : Control
{
    private bool _hover;
    private bool _pressed;

    public ButtonKind Kind { get; set; } = ButtonKind.Ghost;
    public int Radius { get; set; } = 6;
    public bool ToggleState { get; set; }
    /// <summary>Ghost 按钮是否绘制描边（窗口控制按钮等需要无边框时置 false）。</summary>
    public bool Bordered { get; set; } = true;

    /// <summary>可选渐变底色（上 → 下），设置后覆盖 Kind 的纯色背景。</summary>
    public Color? GradientTop { get; set; }
    public Color? GradientBottom { get; set; }

    /// <summary>Ghost 按钮静止态底色（不设置时用主题默认）。</summary>
    public Color? BackOverride { get; set; }

    public FlatButton()
    {
        // 允许继承透明父容器的 BackColor：否则 BackColor 会回落到 SystemColors.Control（#F0F0F0），
        // 使自绘底色与顶栏白底不一致，圆角按钮四周露出方形灰底板。
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint |
                 ControlStyles.SupportsTransparentBackColor, true);
        Font = DarkGeekTheme.UiFont;
        Size = new Size(96, 32);
        Cursor = Cursors.Hand;
        ForeColor = DarkGeekTheme.TextPrimary;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _pressed = true;
        Invalidate();
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    private Color ResolveBack()
    {
        var baseColor = Kind switch
        {
            ButtonKind.Primary => DarkGeekTheme.Primary,
            ButtonKind.Success => DarkGeekTheme.Success,
            ButtonKind.Danger => DarkGeekTheme.Danger,
            _ => DarkGeekTheme.CardBackHover,
        };

        if (Kind == ButtonKind.Ghost && !_hover)
            return BackOverride ?? DarkGeekTheme.ButtonGhostBack;

        return _pressed ? DarkGeekTheme.Blend(baseColor, Color.Black, 0.25)
            : _hover ? DarkGeekTheme.Blend(baseColor, Color.White, 0.12)
            : baseColor;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(DarkGeekTheme.ResolveBackground(this));

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = DarkGeekTheme.RoundedRect(rect, Radius);

        if (GradientTop.HasValue && GradientBottom.HasValue)
        {
            // 渐变底：按下 / 悬停时整体提亮或压暗，保持圆角裁剪
            Color top = GradientTop.Value;
            Color bottom = GradientBottom.Value;

            if (_pressed)
            {
                top = DarkGeekTheme.Blend(top, Color.Black, 0.18);
                bottom = DarkGeekTheme.Blend(bottom, Color.Black, 0.18);
            }
            else if (_hover)
            {
                top = DarkGeekTheme.Blend(top, Color.White, 0.12);
                bottom = DarkGeekTheme.Blend(bottom, Color.White, 0.12);
            }

            using var gradient = new LinearGradientBrush(
                new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, Height)), top, bottom, 90f);
            g.FillPath(gradient, path);
        }
        else
        {
            using var brush = new SolidBrush(ResolveBack());
            g.FillPath(brush, path);
        }

        if (Kind == ButtonKind.Ghost && Bordered)
        {
            using var pen = new Pen(_hover ? DarkGeekTheme.Primary : DarkGeekTheme.Border);
            g.DrawPath(pen, path);
        }

        var fore = !Enabled
            ? DarkGeekTheme.TextMuted
            : Kind == ButtonKind.Ghost
                ? (ToggleState ? DarkGeekTheme.PrimaryDim : ForeColor)
                : Color.White;

        TextRenderer.DrawText(g, Text, Font, rect, fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}
