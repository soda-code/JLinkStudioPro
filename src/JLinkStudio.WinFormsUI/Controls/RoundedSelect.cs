using System.Drawing.Drawing2D;
using JLinkStudio.WinFormsUI.Theme;

namespace JLinkStudio.WinFormsUI.Controls;

/// <summary>圆角下拉选择器：自绘外观 + 暗色弹层，替代系统 ComboBox 的直角边框。</summary>
public sealed class RoundedSelect : Control
{
    private readonly List<string> _items = new();
    private readonly TextBox _editor = new();
    private int _selectedIndex = -1;
    private bool _hover;
    private bool _open;
    private bool _editable;

    public RoundedSelect(params string[] items)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = DarkGeekTheme.InputBack;
        ForeColor = DarkGeekTheme.TextPrimary;
        Font = DarkGeekTheme.UiFont;
        Height = 32;
        Width = 200;
        Radius = 6;
        Cursor = Cursors.Hand;
        Margin = new Padding(0, 4, 12, 4);

        foreach (var item in items) _items.Add(item);
        if (_items.Count > 0) _selectedIndex = 0;

        // 内嵌输入框：仅 Editable 模式显示，用于允许手动输入不在列表中的值
        _editor.BorderStyle = BorderStyle.None;
        _editor.BackColor = DarkGeekTheme.InputBack;
        _editor.ForeColor = DarkGeekTheme.TextPrimary;
        _editor.Font = DarkGeekTheme.UiFont;
        _editor.Visible = false;
        Controls.Add(_editor);

        KeyDown += (_, e) =>
        {
            if (_editable)
            {
                if (e.Alt && e.KeyCode == Keys.Down) ShowDropdown();
                return;
            }

            if (e.KeyCode == Keys.Down && _items.Count > 0)
                SelectedIndex = Math.Min(_items.Count - 1, _selectedIndex + 1);
            else if (e.KeyCode == Keys.Up && _items.Count > 0)
                SelectedIndex = Math.Max(0, _selectedIndex - 1);
            else if (e.KeyCode is Keys.Enter or Keys.Space)
                ShowDropdown();
        };
    }

    public int Radius { get; set; }

    /// <summary>可编辑：显示内嵌输入框，允许输入列表外的自定义值（右侧箭头仍可展开列表）。</summary>
    public bool Editable
    {
        get => _editable;
        set
        {
            if (_editable == value) return;
            _editable = value;
            _editor.Visible = value;
            LayoutEditor();
            Invalidate();
        }
    }

    public void FocusInput() => _editor.Focus();

    private void LayoutEditor()
    {
        if (!_editable) return;
        _editor.Left = 10;
        _editor.Width = Math.Max(20, Width - 42);
        _editor.Top = Math.Max(2, (Height - _editor.PreferredHeight) / 2);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutEditor();
    }

    public IList<string> Items => _items;

    public void AddRange(params string[] items)
    {
        _items.AddRange(items);
        if (_selectedIndex < 0 && _items.Count > 0) _selectedIndex = 0;
        Invalidate();
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            int clamped = _items.Count == 0 ? -1 : Math.Clamp(value, -1, _items.Count - 1);
            if (clamped == _selectedIndex) return;
            _selectedIndex = clamped;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string? SelectedItem => _selectedIndex >= 0 && _selectedIndex < _items.Count ? _items[_selectedIndex] : null;

    public override string Text
    {
        get => _editable ? _editor.Text : SelectedItem ?? string.Empty;
        set
        {
            string text = value ?? string.Empty;

            if (_editable) _editor.Text = text;

            int index = _items.FindIndex(i => string.Equals(i, text, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) SelectedIndex = index;
        }
    }

    public event EventHandler? SelectedIndexChanged;

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
        Focus();

        // 可编辑模式：只有点在右侧箭头区域才展开，左侧区域交给内嵌输入框
        if (_editable && e.X < Width - 34)
        {
            _editor.Focus();
            return;
        }

        ShowDropdown();
    }

    private void ShowDropdown()
    {
        if (_items.Count == 0) return;

        var menu = new ContextMenuStrip
        {
            BackColor = DarkGeekTheme.CardBack,
            ForeColor = DarkGeekTheme.TextPrimary,
            Font = DarkGeekTheme.UiFont,
            ShowImageMargin = false,
            Padding = new Padding(2),
            Renderer = new DarkMenuRenderer(),
        };

        for (int i = 0; i < _items.Count; i++)
        {
            int index = i;
            var item = new ToolStripMenuItem(_items[i])
            {
                Checked = index == _selectedIndex,
                AutoSize = false,
                Height = 26,
                Width = Math.Max(Width - 6, 120),
            };
            item.Click += (_, _) =>
            {
                SelectedIndex = index;
                if (_editable) _editor.Text = _items[index];
            };
            menu.Items.Add(item);
        }

        menu.Closed += (_, _) =>
        {
            _open = false;
            Invalidate();
        };

        _open = true;
        Invalidate();
        menu.Show(this, new Point(0, Height + 2));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = DarkGeekTheme.RoundedRect(rect, Radius);
        using (var brush = new SolidBrush(DarkGeekTheme.InputBack))
            g.FillPath(brush, path);

        var border = _open || _hover
            ? DarkGeekTheme.Blend(DarkGeekTheme.Border, DarkGeekTheme.Primary, 0.6)
            : DarkGeekTheme.Border;
        using (var pen = new Pen(border))
            g.DrawPath(pen, path);

        // 可编辑模式下文本由内嵌输入框绘制
        if (!_editable)
        {
            string text = SelectedItem ?? "请选择…";
            var textColor = SelectedItem == null ? DarkGeekTheme.TextMuted : DarkGeekTheme.TextPrimary;
            TextRenderer.DrawText(g, text, Font, new Rectangle(10, 0, Width - 32, Height), textColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        // 右侧下拉箭头
        int cx = Width - 16, cy = Height / 2;
        using var arrowPen = new Pen(_hover || _open ? DarkGeekTheme.Primary : DarkGeekTheme.TextMuted, 1.6f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        g.DrawLines(arrowPen, new[]
        {
            new Point(cx - 4, cy - 2),
            new Point(cx, cy + 2),
            new Point(cx + 4, cy - 2),
        });
    }

    /// <summary>下拉弹层的暗色渲染器。</summary>
    private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColorTable())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var rect = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
            using var path = DarkGeekTheme.RoundedRect(rect, 4);
            using var brush = new SolidBrush(e.Item.Selected ? DarkGeekTheme.CardBackHover : DarkGeekTheme.CardBack);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillPath(brush, path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Selected ? DarkGeekTheme.Primary : DarkGeekTheme.TextPrimary;
            base.OnRenderItemText(e);
        }

        private sealed class DarkColorTable : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => DarkGeekTheme.CardBack;
            public override Color MenuBorder => DarkGeekTheme.Border;
            public override Color MenuItemBorder => DarkGeekTheme.Primary;
            public override Color MenuItemSelected => DarkGeekTheme.CardBackHover;
            public override Color ImageMarginGradientBegin => DarkGeekTheme.CardBack;
            public override Color ImageMarginGradientMiddle => DarkGeekTheme.CardBack;
            public override Color ImageMarginGradientEnd => DarkGeekTheme.CardBack;
            public override Color CheckBackground => DarkGeekTheme.PrimaryDim;
            public override Color CheckSelectedBackground => DarkGeekTheme.Primary;
        }
    }
}
