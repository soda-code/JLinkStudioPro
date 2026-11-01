using System.Drawing.Drawing2D;
using JLinkStudio.WinFormsUI.Theme;

namespace JLinkStudio.WinFormsUI.Controls;

/// <summary>圆角复选框：自绘圆角方框 + 对勾，替换系统直角勾选框。</summary>
public sealed class FlatCheckBox : Control
{
    private const int BoxSize = 16;
    private bool _checked;
    private bool _hover;

    public FlatCheckBox(string text = "", bool @checked = false)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint |
                 ControlStyles.SupportsTransparentBackColor, true);
        Text = text;
        _checked = @checked;
        BackColor = Color.Transparent;
        ForeColor = DarkGeekTheme.TextPrimary;
        Font = DarkGeekTheme.UiFont;
        Height = 24;
        Width = MeasureWidth();
        Cursor = Cursors.Hand;
        Margin = new Padding(0, 8, 12, 6);
    }

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? CheckedChanged;

    private int MeasureWidth() =>
        TextRenderer.MeasureText(Text ?? string.Empty, Font).Width + BoxSize + 12;

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Width = MeasureWidth();
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Width = MeasureWidth();
        Invalidate();
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
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();
        Checked = !Checked;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Space) Checked = !Checked;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(DarkGeekTheme.ResolveBackground(this));

        int top = Math.Max(0, (Height - BoxSize) / 2);
        var boxRect = new Rectangle(1, top, BoxSize, BoxSize);
        using var path = DarkGeekTheme.RoundedRect(boxRect, 4);

        if (Checked)
        {
            using var brush = new SolidBrush(DarkGeekTheme.Primary);
            g.FillPath(brush, path);
            using var pen = new Pen(Color.White, 1.8f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            g.DrawLines(pen, new[]
            {
                new Point(boxRect.Left + 4, boxRect.Top + 8),
                new Point(boxRect.Left + 7, boxRect.Top + 11),
                new Point(boxRect.Left + 12, boxRect.Top + 5),
            });
        }
        else
        {
            using var brush = new SolidBrush(DarkGeekTheme.InputBack);
            g.FillPath(brush, path);
            using var pen = new Pen(_hover ? DarkGeekTheme.Primary : DarkGeekTheme.Border, 1.2f);
            g.DrawPath(pen, path);
        }

        var textRect = new Rectangle(BoxSize + 8, 0, Width - BoxSize - 8, Height);
        TextRenderer.DrawText(g, Text, Font, textRect,
            Enabled ? ForeColor : DarkGeekTheme.TextMuted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}
