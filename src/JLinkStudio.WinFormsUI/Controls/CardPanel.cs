using System.Drawing.Drawing2D;
using JLinkStudio.WinFormsUI.Theme;

namespace JLinkStudio.WinFormsUI.Controls;

/// <summary>圆角卡片容器（带标题栏），符合暗黑风视觉规范。</summary>
public sealed class CardPanel : Panel
{
    private string _title = string.Empty;
    private string? _subtitle;

    public CardPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        // 使用不透明背景 + 圆角 Region：透明子控件取到的是 CardBack，颜色不会出现断层
        BackColor = DarkGeekTheme.CardBack;
        Padding = new Padding(16, 44, 16, 14);
        AutoScroll = false;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
    }

    public string Title
    {
        get => _title;
        set { _title = value; Invalidate(); }
    }

    public string? Subtitle
    {
        get => _subtitle;
        set { _subtitle = value; Invalidate(); }
    }

    public int Radius { get; set; } = 8;

    /// <summary>
    /// 是否绘制圆角边框。设为 false 时可作为"页面唯一卡片"内部的分区容器使用
    /// （仅保留标题与分隔线）。
    /// </summary>
    public bool ShowBorder { get; set; } = true;

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyRegion();
    }

    private void ApplyRegion()
    {
        if (!ShowBorder || Width <= 0 || Height <= 0)
        {
            var old = Region;
            Region = null;
            old?.Dispose();
            return;
        }
        using var path = DarkGeekTheme.RoundedRect(new Rectangle(0, 0, Width, Height), Radius);
        var previous = Region;
        Region = new Region(path);
        previous?.Dispose();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (ShowBorder)
        {
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using var pen = new Pen(DarkGeekTheme.Border);
            using var path = DarkGeekTheme.RoundedRect(rect, Radius);
            g.DrawPath(pen, path);

            // 顶部 1px 高光：浅色主题下用极淡的白色受光面，贴合参考规范的轻阴影层次
            using var highlight = new Pen(Color.FromArgb(180, Color.White));
            g.DrawLine(highlight, 14, 1, Width - 14, 1);
        }

        if (!string.IsNullOrEmpty(_title))
        {
            TextRenderer.DrawText(g, _title, DarkGeekTheme.UiFontBold, new Point(16, 13),
                DarkGeekTheme.TextPrimary, TextFormatFlags.NoPadding);

            if (!string.IsNullOrEmpty(_subtitle))
            {
                var size = TextRenderer.MeasureText(g, _subtitle, DarkGeekTheme.UiFontSmall);
                TextRenderer.DrawText(g, _subtitle, DarkGeekTheme.UiFontSmall,
                    new Point(Width - size.Width - 16, 16), DarkGeekTheme.TextMuted, TextFormatFlags.NoPadding);
            }

            using var line = new Pen(DarkGeekTheme.Border);
            g.DrawLine(line, 16, 38, Width - 16, 38);
        }
    }
}
