using System.Drawing.Drawing2D;
using JLinkStudio.WinFormsUI.Theme;

namespace JLinkStudio.WinFormsUI.Controls;

/// <summary>状态胶囊：彩色圆点 + 文本，用于连接状态 / 服务状态展示。</summary>
public sealed class StatusPill : Control
{
    private Color _dotColor = DarkGeekTheme.TextMuted;
    private bool _pulse;

    public StatusPill()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint |
                 ControlStyles.SupportsTransparentBackColor, true);
        Height = 26;
        Width = 120;
        Font = DarkGeekTheme.UiFontSmall;
        BackColor = Color.Transparent;
        ForeColor = DarkGeekTheme.TextPrimary;
    }

    public Color DotColor
    {
        get => _dotColor;
        set { _dotColor = value; Invalidate(); }
    }

    public bool Pulse
    {
        get => _pulse;
        set { _pulse = value; Invalidate(); }
    }

    public void SetState(string text, Color color)
    {
        Text = text;
        DotColor = color;
        int width = TextRenderer.MeasureText(text, Font).Width + 34;
        Width = Math.Max(80, width);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(DarkGeekTheme.ResolveBackground(this));

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = DarkGeekTheme.RoundedRect(rect, Height / 2);
        using (var brush = new SolidBrush(DarkGeekTheme.CardBack))
            g.FillPath(brush, path);
        using (var pen = new Pen(DarkGeekTheme.Border))
            g.DrawPath(pen, path);

        int cy = Height / 2;
        using (var dot = new SolidBrush(DotColor))
            g.FillEllipse(dot, 10, cy - 4, 8, 8);
        if (Pulse)
        {
            using var halo = new SolidBrush(Color.FromArgb(60, DotColor));
            g.FillEllipse(halo, 6, cy - 8, 16, 16);
        }

        TextRenderer.DrawText(g, Text, Font, new Rectangle(24, 0, Width - 30, Height), ForeColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}
