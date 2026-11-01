using System.Drawing.Drawing2D;

namespace JLinkStudio.WinFormsUI.Theme;

/// <summary>全局视觉规范（浅色 Slate + Blue 配色、字体、圆角与控件样式）。</summary>
public static class DarkGeekTheme
{
    // ── 中性色：Tailwind Slate 浅色体系（sidebar 白 / 内容 slate-100 / 卡片白）──
    public static readonly Color WindowBack = Hex("#FFFFFF");   // 顶栏 / 状态栏
    public static readonly Color PageBack = Hex("#E6ECF3");     // 内容区底色（比 slate-100 再深一档）
    public static readonly Color CardBack = Hex("#FFFFFF");     // 卡片
    public static readonly Color CardBackHover = Hex("#EDF1F6");
    public static readonly Color Border = Hex("#DCE3EC");       // 分隔线（随底色加深）
    public static readonly Color GridHeaderBack = Hex("#F1F5F9");
    /// <summary>表头底色（比行底色更深一档，形成明确分区）。</summary>
    public static readonly Color GridHeaderBackStrong = Hex("#E4EAF2");
    /// <summary>表格分隔线：极浅横向线，避免出现深色 / 黑色网格。</summary>
    public static readonly Color GridLineSoft = Hex("#F2F5F9");
    public static readonly Color RowAltBack = Hex("#F4F7FB");
    public static readonly Color TerminalBack = Hex("#F0F4F9");
    public static readonly Color PlotBack = Hex("#FFFFFF");
    public static readonly Color PlotGrid = Hex("#E2E8F0");
    public static readonly Color CardShadow = Color.FromArgb(20, 15, 23, 42); // rgba(15,23,42,0.08)

    // ── 强调色：Tailwind Blue（blue-600 为实心强调线 / 主按钮）──
    public static readonly Color Primary = Hex("#2563EB");      // blue-600
    public static readonly Color PrimaryDim = Hex("#1D4ED8");   // blue-700
    public static readonly Color PrimarySoft = Hex("#3B82F6");  // blue-500
    public static readonly Color PrimaryWash = Hex("#EFF6FF");  // blue-50
    public static readonly Color PrimaryWashStrong = Hex("#DBEAFE"); // blue-100

    // 固定侧边栏（Fixed Sidebar）：白底 + slate-200 分隔线
    public static readonly Color SidebarBack = Hex("#FFFFFF");
    public static readonly Color SidebarBorder = Hex("#E2E8F0");
    public static readonly Color NavText = Hex("#334155");       // slate-700
    public static readonly Color NavTextMuted = Hex("#64748B");  // slate-500
    public static readonly Color NavActiveBack = Hex("#EFF6FF"); // blue-50
    public static readonly Color NavActiveText = Hex("#1D4ED8"); // blue-700
    public static readonly Color NavGroupText = Hex("#94A3B8");  // slate-400

    public static readonly Color Success = Hex("#059669");
    public static readonly Color Warning = Hex("#D97706");
    public static readonly Color Danger = Hex("#DC2626");

    public static readonly Color TextPrimary = Hex("#0F172A");    // slate-900
    public static readonly Color TextSecondary = Hex("#475569");  // slate-600
    public static readonly Color TextMuted = Hex("#94A3B8");      // slate-400
    /// <summary>输入框 / 下拉框 / 输出区统一底色：比卡片深一档，白卡片上也能看出输入区域。</summary>
    public static readonly Color InputBack = Hex("#F0F4F9");
    /// <summary>输入框描边。</summary>
    public static readonly Color InputBorder = Hex("#DCE3EC");
    /// <summary>输入框聚焦底色（变白 + 蓝色描边，突出当前焦点）。</summary>
    public static readonly Color InputFocusBack = Hex("#FFFFFF");

    /// <summary>Ghost 按钮静止态底色（与输入框同族，但略深一档形成层级）。</summary>
    public static readonly Color ButtonGhostBack = Hex("#E8EDF4");

    public static readonly Color Selection = Hex("#DBEAFE");      // blue-100
    public static readonly Color SelectionText = Hex("#1E3A8A");  // blue-900

    public static readonly Font UiFont = CreateFont(new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI" }, 9.75f);
    public static readonly Font UiFontSmall = CreateFont(new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI" }, 8.75f);
    public static readonly Font UiFontBold = CreateFont(new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI" }, 10.5f, FontStyle.Bold);
    public static readonly Font TitleFont = CreateFont(new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI" }, 12f, FontStyle.Bold);
    public static readonly Font MonoFont = CreateFont(new[] { "JetBrains Mono", "Cascadia Code", "Consolas", "Courier New" }, 9.5f);
    public static readonly Font MonoFontSmall = CreateFont(new[] { "JetBrains Mono", "Cascadia Code", "Consolas", "Courier New" }, 8.5f);
    public static readonly Font IconFont = CreateFont(new[] { "Segoe UI Emoji", "Segoe UI Symbol", "Microsoft YaHei UI" }, 12f);

    public static Color Hex(string hex) => ColorTranslator.FromHtml(hex);

    /// <summary>向上回溯到第一个不透明背景色，供透明容器内的自绘控件取底色。</summary>
    public static Color ResolveBackground(Control? control)
    {
        for (var current = control; current != null; current = current.Parent)
        {
            if (current.BackColor.A == 255) return current.BackColor;
        }
        return PageBack;
    }

    public static Color Blend(Color from, Color to, double ratio)
    {
        ratio = Math.Clamp(ratio, 0, 1);
        return Color.FromArgb(
            (int)(from.R + (to.R - from.R) * ratio),
            (int)(from.G + (to.G - from.G) * ratio),
            (int)(from.B + (to.B - from.B) * ratio));
    }

    public static Font CreateFontPublic(float size, FontStyle style = FontStyle.Regular) =>
        CreateFont(new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI" }, size, style);

    private static Font CreateFont(string[] candidates, float size, FontStyle style = FontStyle.Regular)
    {
        var installed = FontFamily.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in candidates)
        {
            if (installed.Contains(name))
                return new Font(name, size, style, GraphicsUnit.Point);
        }
        return new Font(FontFamily.GenericSansSerif, size, style, GraphicsUnit.Point);
    }

    public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        int d = radius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>统一暗色 DataGridView 视觉。</summary>
    public static void StyleGrid(DataGridView grid, bool readOnly = true)
    {
        grid.EnableHeadersVisualStyles = false;
        grid.BackgroundColor = CardBack;
        grid.BorderStyle = BorderStyle.None;
        grid.GridColor = Border;
        grid.RowHeadersVisible = false;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.ReadOnly = readOnly;
        grid.ColumnHeadersHeight = 30;
        grid.RowTemplate.Height = 26;
        grid.Font = MonoFontSmall;
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = GridHeaderBack,
            ForeColor = TextSecondary,
            Font = UiFontSmall,
            SelectionBackColor = GridHeaderBack,
            Alignment = DataGridViewContentAlignment.MiddleLeft,
            Padding = new Padding(4, 0, 0, 0),
        };
        grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = CardBack,
            ForeColor = TextPrimary,
            SelectionBackColor = Selection,
            SelectionForeColor = SelectionText,
            Padding = new Padding(4, 0, 0, 0),
        };
        grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = RowAltBack,
            ForeColor = TextPrimary,
            SelectionBackColor = Selection,
            SelectionForeColor = SelectionText,
            Padding = new Padding(4, 0, 0, 0),
        };
    }

    /// <summary>
    /// Excel 风格表格：左侧行号、单元格网格线、无隔行底色、单元格选择、统一行高与内边距。
    /// 所有业务表格（变量、寄存器、日志、芯片选择、符号搜索）统一走这里，保证视觉一致。
    /// </summary>
    public static void StyleGridExcel(DataGridView grid, bool readOnly = true,
        DataGridViewSelectionMode selectionMode = DataGridViewSelectionMode.CellSelect)
    {
        StyleGrid(grid, readOnly);

        // 只保留极浅的横向分隔线：去掉竖线与深色框线
        grid.GridColor = GridLineSoft;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.RowHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.ColumnHeadersHeight = 32;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.RowTemplate.Height = 30;
        grid.RowTemplate.MinimumHeight = 30;
        grid.SelectionMode = selectionMode;
        grid.ScrollBars = ScrollBars.Both;

        // 表头：加深底色 + 加重字色，与单元格形成清晰分区
        grid.ColumnHeadersDefaultCellStyle.BackColor = GridHeaderBackStrong;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = TextSecondary;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = GridHeaderBackStrong;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = TextSecondary;
        grid.ColumnHeadersDefaultCellStyle.Font = UiFontBold;
        grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(4, 0, 4, 0);

        grid.DefaultCellStyle.Font = UiFont;
        grid.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
        grid.DefaultCellStyle.Padding = new Padding(4, 0, 4, 0);

        // 取消隔行底色，形成 Excel 一样的纯白单元格
        grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = CardBack,
            ForeColor = TextPrimary,
            SelectionBackColor = Selection,
            SelectionForeColor = SelectionText,
            Padding = new Padding(4, 0, 4, 0),
        };

        grid.RowHeadersVisible = true;
        grid.RowHeadersWidth = 34;
        grid.RowHeadersWidthSizeMode = DataGridViewRowHeadersWidthSizeMode.DisableResizing;
        grid.RowHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = GridHeaderBackStrong,
            ForeColor = TextMuted,
            SelectionBackColor = GridHeaderBackStrong,
            SelectionForeColor = TextMuted,
            Alignment = DataGridViewContentAlignment.MiddleRight,
            Font = UiFontSmall,
            Padding = new Padding(0, 0, 4, 0),
        };

        // 行号自绘：先解绑再绑定，避免重复订阅导致重绘两次
        grid.RowPostPaint -= PaintRowNumber;
        grid.RowPostPaint += PaintRowNumber;
    }

    /// <summary>行号列：Excel 风格的右侧对齐行序号。</summary>
    private static void PaintRowNumber(object? sender, DataGridViewRowPostPaintEventArgs e)
    {
        if (sender is not DataGridView grid) return;

        var bounds = new Rectangle(
            e.RowBounds.Left,
            e.RowBounds.Top,
            grid.RowHeadersWidth - 4,
            e.RowBounds.Height);

        TextRenderer.DrawText(e.Graphics, (e.RowIndex + 1).ToString(), UiFontSmall, bounds,
            TextMuted,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    public static void StyleInput(Control control)
    {
        control.BackColor = InputBack;
        control.ForeColor = TextPrimary;
        control.Font = UiFont;
    }

    public static void StyleCombo(ComboBox combo)
    {
        combo.BackColor = InputBack;
        combo.ForeColor = TextPrimary;
        combo.FlatStyle = FlatStyle.Flat;
        combo.Font = UiFont;
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
    }

    public static void StyleCheck(Control check)
    {
        check.ForeColor = TextPrimary;
        check.Font = UiFont;
        check.BackColor = Color.Transparent;
    }
}
