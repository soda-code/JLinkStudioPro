using System.Drawing.Drawing2D;
using JLinkStudio.WinFormsUI.Theme;

namespace JLinkStudio.WinFormsUI.Controls;

public sealed class BitToggleEventArgs : EventArgs
{
    public int Bit { get; init; }
    public bool Value { get; init; }
}

/// <summary>32 位寄存器位域可视化面板：Bit31~Bit0 双行网格，点击直接翻转对应位。</summary>
public sealed class BitFieldVisualizer : Control
{
    private const int Columns = 16;
    private const int Gap = 3;

    private uint _value;
    private bool _readOnly;

    public BitFieldVisualizer()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint |
                 ControlStyles.SupportsTransparentBackColor, true);
        Height = 86;
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
    }

    public uint Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            Invalidate();
        }
    }

    public bool ReadOnly
    {
        get => _readOnly;
        set
        {
            _readOnly = value;
            Cursor = value ? Cursors.Default : Cursors.Hand;
        }
    }

    public event EventHandler<BitToggleEventArgs>? BitToggled;

    private float CellWidth => (Width - Gap * (Columns - 1)) / (float)Columns;
    private const int CellHeight = 32;

    private (int Row, int Col) HitTest(Point point)
    {
        int col = (int)(point.X / (CellWidth + Gap));
        int row = point.Y / (CellHeight + 6);
        return (row, col);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (_readOnly) return;

        var (row, col) = HitTest(e.Location);
        if (row is < 0 or > 1 || col < 0 || col >= Columns) return;

        int bit = row == 0 ? 31 - col : 15 - col;
        bool newValue = (_value & (1u << bit)) == 0;
        _value = newValue ? _value | (1u << bit) : _value & ~(1u << bit);
        Invalidate();
        BitToggled?.Invoke(this, new BitToggleEventArgs { Bit = bit, Value = newValue });
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_readOnly) return;
        var (row, col) = HitTest(e.Location);
        Cursor = row is 0 or 1 && col is >= 0 and < Columns ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(DarkGeekTheme.ResolveBackground(this));

        float cellWidth = CellWidth;
        bool showIndex = cellWidth >= 26;
        var valueFont = cellWidth >= 22 ? DarkGeekTheme.UiFontBold : DarkGeekTheme.UiFontSmall;
        using var indexFont = DarkGeekTheme.CreateFontPublic(6.5f);

        for (int row = 0; row < 2; row++)
        {
            for (int col = 0; col < Columns; col++)
            {
                int bit = row == 0 ? 31 - col : 15 - col;
                bool set = (_value & (1u << bit)) != 0;

                var rect = new RectangleF(col * (cellWidth + Gap), row * (CellHeight + 6), cellWidth, CellHeight);
                using var path = DarkGeekTheme.RoundedRect(Rectangle.Round(rect), 4);
                using (var brush = new SolidBrush(set ? DarkGeekTheme.PrimaryWashStrong : DarkGeekTheme.InputBack))
                    g.FillPath(brush, path);
                using (var pen = new Pen(set ? DarkGeekTheme.Primary : DarkGeekTheme.Border))
                    g.DrawPath(pen, path);

                if (showIndex)
                {
                    TextRenderer.DrawText(g, bit.ToString(), indexFont,
                        new Rectangle((int)rect.X, (int)rect.Y + 2, (int)rect.Width, 10),
                        set ? DarkGeekTheme.PrimarySoft : DarkGeekTheme.TextMuted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
                }

                var valueRect = showIndex
                    ? new Rectangle((int)rect.X, (int)rect.Y + 12, (int)rect.Width, 18)
                    : new Rectangle((int)rect.X, (int)rect.Y + 6, (int)rect.Width, 20);

                TextRenderer.DrawText(g, set ? "1" : "0", valueFont, valueRect,
                    set ? DarkGeekTheme.PrimaryDim : DarkGeekTheme.TextMuted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            }
        }
    }
}
