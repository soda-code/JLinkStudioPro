using JLinkStudio.WinFormsUI.Theme;

namespace JLinkStudio.WinFormsUI.Controls;

/// <summary>界面小工具：统一创建暗色圆角控件，减少各页面重复代码。</summary>
public static class Ui
{
    public static Label Text(string text, bool bold = false, Color? color = null, float? size = null)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = color ?? DarkGeekTheme.TextSecondary,
            BackColor = Color.Transparent,
            Font = bold
                ? DarkGeekTheme.CreateFontPublic(size ?? 10f, FontStyle.Bold)
                : DarkGeekTheme.CreateFontPublic(size ?? 9.75f, FontStyle.Regular),
            Margin = new Padding(0, 6, 8, 0),
        };
    }

    public static Label Mono(string text, Color? color = null)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = color ?? DarkGeekTheme.TextPrimary,
            BackColor = Color.Transparent,
            Font = DarkGeekTheme.MonoFont,
            Margin = new Padding(0, 6, 8, 0),
        };
    }

    public static RoundedTextBox Input(string text = "", int width = 240, bool multiline = false)
    {
        var input = new RoundedTextBox(text, width, multiline) { Margin = new Padding(0, 4, 12, 4) };
        return input;
    }

    public static RoundedSelect Combo(params string[] items)
    {
        return new RoundedSelect(items) { Width = 200, Margin = new Padding(0, 4, 12, 4) };
    }

    public static FlatCheckBox Check(string text, bool @checked = false)
    {
        return new FlatCheckBox(text, @checked);
    }

    public static FlatButton Button(string text, ButtonKind kind = ButtonKind.Ghost,
        EventHandler? onClick = null, int width = 96)
    {
        var button = new FlatButton
        {
            Text = text,
            Kind = kind,
            Width = width,
            Height = 32,
            Margin = new Padding(0, 4, 8, 4),
        };
        if (onClick != null) button.Click += onClick;
        return button;
    }

    /// <summary>页面级"唯一卡片"容器：整页只放这一张卡片。</summary>
    public static CardPanel PageCard(Padding? padding = null)
    {
        return new CardPanel
        {
            Dock = DockStyle.Fill,
            Padding = padding ?? new Padding(14, 10, 14, 12),
        };
    }

    /// <summary>卡片内的无边框分区：只保留标题与分隔线，用于"一个卡片多个分区"的版式。</summary>
    public static CardPanel Section(string title, string? subtitle = null, Padding? padding = null)
    {
        return new CardPanel
        {
            Dock = DockStyle.Fill,
            ShowBorder = false,
            Title = title,
            Subtitle = subtitle,
            Padding = padding ?? new Padding(4, 40, 4, 6),
        };
    }

    /// <summary>把表格 / 列表 / 日志等原生控件包进圆角容器。</summary>
    public static RoundedFrame WrapDisplay(Control inner, Color? background = null, int radius = 6)
    {
        var frame = new RoundedFrame
        {
            BackColor = background ?? DarkGeekTheme.InputBack,
            BorderColor = DarkGeekTheme.GridLineSoft,
            Radius = radius,
            Dock = DockStyle.Fill,
            Padding = new Padding(1),
        };
        inner.Dock = DockStyle.Fill;
        frame.Controls.Add(inner);
        return frame;
    }

    /// <summary>把数值框等无边框控件包进圆角容器。</summary>
    public static RoundedFrame WrapInput(Control inner, int height = 32, int width = 96, int radius = 6)
    {
        var frame = new RoundedFrame
        {
            BackColor = DarkGeekTheme.InputBack,
            Radius = radius,
            Height = height,
            Width = width,
            Padding = new Padding(6, 3, 6, 3),
            HighlightOnHover = true,
        };
        inner.Dock = DockStyle.Fill;
        frame.Controls.Add(inner);
        return frame;
    }

    public static FlowLayoutPanel Row(int topPadding = 0, int gap = 0)
    {
        return new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Color.Transparent,
            Padding = new Padding(0, topPadding, 0, 0),
            Margin = new Padding(0, 0, 0, gap),
        };
    }

    public static FlowLayoutPanel Stack(int top = 0)
    {
        return new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Color.Transparent,
            Padding = new Padding(0, top, 0, 0),
        };
    }

    public static Panel Separator()
    {
        return new Panel { Height = 1, Dock = DockStyle.Top, BackColor = DarkGeekTheme.Border, Margin = new Padding(0, 8, 0, 8) };
    }

    public static DataGridView Grid(params (string Header, string Name, int FillWeight, bool ReadOnly)[] columns)
    {
        var grid = new DataGridView { Dock = DockStyle.Fill };
        DarkGeekTheme.StyleGridExcel(grid);
        foreach (var (header, name, weight, readOnly) in columns)
        {
            var column = new DataGridViewTextBoxColumn
            {
                HeaderText = header,
                Name = name,
                FillWeight = weight,
                ReadOnly = readOnly,
                SortMode = DataGridViewColumnSortMode.NotSortable,
            };
            grid.Columns.Add(column);
        }
        return grid;
    }
}
