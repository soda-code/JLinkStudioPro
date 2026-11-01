using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using JLinkStudio.WinFormsUI.Theme;

namespace JLinkStudio.WinFormsUI.Controls;

/// <summary>圆角输入框：内部为无边框 TextBox，外层自绘圆角背景与聚焦描边。</summary>
public sealed class RoundedTextBox : Control
{
    private const int EM_SETCUEBANNER = 0x1501;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    private readonly TextBox _box = new();
    private string _placeholder = string.Empty;
    private bool _hover;
    private Control? _inlineAction;

    public RoundedTextBox(string text = "", int width = 240, bool multiline = false)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = DarkGeekTheme.InputBack;
        Margin = new Padding(0, 4, 12, 4);
        Width = width;
        Radius = 6;

        _box.BorderStyle = BorderStyle.None;
        _box.BackColor = DarkGeekTheme.InputBack;
        _box.ForeColor = DarkGeekTheme.TextPrimary;
        _box.Font = DarkGeekTheme.UiFont;
        _box.Text = text;
        _box.Multiline = multiline;
        _box.ShortcutsEnabled = true;

        if (multiline)
        {
            Height = 90;
            _box.ScrollBars = ScrollBars.Vertical;
            _box.Dock = DockStyle.Fill;
            Padding = new Padding(9, 7, 9, 7);
        }
        else
        {
            Height = 32;
            // 左右留白略大，避免文字贴住圆角边框
            Padding = new Padding(13, 0, 13, 0);
        }

        Controls.Add(_box);

        _box.TextChanged += (_, _) =>
        {
            OnTextChanged(EventArgs.Empty);
            Invalidate();
        };
        _box.GotFocus += (_, _) => Invalidate();
        _box.LostFocus += (_, _) => Invalidate();
        _box.KeyDown += (_, e) => OnKeyDown(e);
        _box.KeyUp += (_, e) => OnKeyUp(e);
        _box.KeyPress += (_, e) => OnKeyPress(e);
        _box.MouseEnter += (_, _) => { _hover = true; Invalidate(); };
        _box.MouseLeave += (_, _) => { _hover = false; Invalidate(); };
        _box.HandleCreated += (_, _) => ApplyCueBanner();

        // 聚焦时输入区变白 + 蓝色描边，未聚焦时与同族控件统一为浅灰底
        _box.GotFocus += (_, _) =>
        {
            _box.BackColor = DarkGeekTheme.InputFocusBack;
            Invalidate();
        };
        _box.LostFocus += (_, _) =>
        {
            _box.BackColor = DarkGeekTheme.InputBack;
            Invalidate();
        };
    }

    public int Radius { get; set; }

    public TextBox Inner => _box;

    public bool Multiline => _box.Multiline;

    public override string Text
    {
        get => _box.Text;
        set => _box.Text = value;
    }

    public string PlaceholderText
    {
        get => _placeholder;
        set
        {
            _placeholder = value ?? string.Empty;
            ApplyCueBanner();
        }
    }

    /// <summary>使用原生 EM_SETCUEBANNER 绘制占位符（暗色主题下对比度稳定、随控件绘制）。</summary>
    private void ApplyCueBanner()
    {
        if (!_box.IsHandleCreated || _box.Multiline) return;
        SendMessage(_box.Handle, EM_SETCUEBANNER, (IntPtr)1, _placeholder);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        LayoutInner();
        ApplyCueBanner();
    }

    public bool ReadOnly
    {
        get => _box.ReadOnly;
        set => _box.ReadOnly = value;
    }

    public bool PasswordChar
    {
        get => _box.UseSystemPasswordChar;
        set => _box.UseSystemPasswordChar = value;
    }

    public int MaxLength
    {
        get => _box.MaxLength;
        set => _box.MaxLength = value;
    }

    public void SelectAll() => _box.SelectAll();

    public void FocusInput() => _box.Focus();

    /// <summary>
    /// 在输入框内部右侧嵌入一个小按钮（如「自动识别」），
    /// 视觉上仍是一个完整的输入框，而不是输入框 + 右侧独立按钮。
    /// </summary>
    public void SetInlineAction(Control action)
    {
        if (_inlineAction != null)
        {
            Controls.Remove(_inlineAction);
            _inlineAction.Dispose();
        }

        _inlineAction = action;
        action.Dock = DockStyle.None;
        action.Margin = new Padding(0);
        Controls.Add(action);

        LayoutInlineAction();
        OnResize(EventArgs.Empty);
        Invalidate();
    }

    private void LayoutInlineAction()
    {
        if (_inlineAction == null || _inlineAction.IsDisposed) return;
        int height = Math.Max(20, Height - 8);
        _inlineAction.Height = height;
        _inlineAction.Left = Math.Max(0, Width - _inlineAction.Width - 4);
        _inlineAction.Top = Math.Max(0, (Height - height) / 2);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutInner();
    }

    protected override void OnPaddingChanged(EventArgs e)
    {
        base.OnPaddingChanged(e);
        // Padding 在构造中晚于 Width 设置，必须在此重新排版，否则内部文本框会贴住边框
        LayoutInner();
    }

    /// <summary>按当前尺寸与内边距摆放内部文本框。</summary>
    private void LayoutInner()
    {
        if (_box.Multiline) return;

        int reserve = _inlineAction is { IsDisposed: false } ? _inlineAction.Width + 12 : 0;
        _box.Width = Math.Max(10, Width - Padding.Horizontal - reserve);
        _box.Left = Padding.Left;
        // 上下各留 2px，避免字体放大后文字压到上下边框
        _box.Top = Math.Max(2, (Height - _box.PreferredHeight) / 2);
        LayoutInlineAction();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        if (_box.Font != Font) _box.Font = Font;
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        _box.Enabled = Enabled;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _box.Focus();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = DarkGeekTheme.RoundedRect(rect, Radius);

        var back = !Enabled
            ? DarkGeekTheme.RowAltBack
            : _box.Focused
                ? DarkGeekTheme.InputFocusBack
                : DarkGeekTheme.InputBack;

        using (var brush = new SolidBrush(back))
            g.FillPath(brush, path);

        var border = !Enabled
            ? DarkGeekTheme.Border
            : _box.Focused
                ? DarkGeekTheme.Primary
                : _hover
                    ? DarkGeekTheme.Blend(DarkGeekTheme.Border, DarkGeekTheme.Primary, 0.55)
                    : DarkGeekTheme.Border;

        using var pen = new Pen(border);
        g.DrawPath(pen, path);
    }
}
