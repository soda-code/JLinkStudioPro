using System.Drawing.Drawing2D;
using JLinkStudio.Core.Models;
using JLinkStudio.WinFormsUI.Theme;

namespace JLinkStudio.WinFormsUI.Controls;

/// <summary>
/// 需要人工介入时的圆角提示框（无系统边框 + 阴影）。
/// 可选项以 Excel 表格样式展示（序号 / 待选操作），点选后返回所选文本。
/// </summary>
public sealed class NotifyDialog : Form
{
    private const int PadLeft = 22;
    private const int ContentWidth = 596;
    private const int Radius = 14;
    private const int ItemHeight = 28;

    private bool _dragging;
    private Point _dragOrigin;

    /// <summary>用户选中的选项文本；无选项且点了确定时返回「确定」。</summary>
    public string? SelectedOption { get; private set; }

    public NotifyDialog(UiPromptRequest request)
    {
        var options = request.Options ?? new List<string>();
        bool hasOptions = options.Count > 0;
        bool cancellable = hasOptions || request.WaitForConfirm;

        Text = string.IsNullOrWhiteSpace(request.Title) ? "需要人工操作" : request.Title;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Padding = new Padding(0);
        BackColor = DarkGeekTheme.PageBack;
        ForeColor = DarkGeekTheme.TextPrimary;
        Font = DarkGeekTheme.UiFont;

        Color levelColor = (request.Level ?? "info").ToLowerInvariant() switch
        {
            "error" => DarkGeekTheme.Danger,
            "warn" or "warning" => DarkGeekTheme.Warning,
            "success" or "ok" => DarkGeekTheme.Success,
            _ => DarkGeekTheme.Primary,
        };

        var accent = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(4, 120),
            BackColor = levelColor,
        };

        var badge = new Panel
        {
            Location = new Point(PadLeft, 26),
            Size = new Size(10, 10),
            BackColor = levelColor,
        };

        var title = Ui.Text(Text, true, DarkGeekTheme.TextPrimary, 10.5f);
        title.Location = new Point(PadLeft + 18, 21);

        // 右上角关闭（无边框窗口自绘）
        var close = Ui.Text("✕", true, DarkGeekTheme.TextMuted, 10f);
        close.Cursor = Cursors.Hand;
        close.Location = new Point(ContentWidth + PadLeft - 12, 20);
        close.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };

        int y = 52;

        // 有选项时只显示「标题 + 列表 + 按钮」：不显示正文与来源提示，列表占满可用区域
        TextBox? message = null;
        if (!hasOptions)
        {
            message = new TextBox
            {
                Location = new Point(PadLeft, y),
                Width = ContentWidth,
                Multiline = true,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = DarkGeekTheme.PageBack,
                ForeColor = DarkGeekTheme.TextSecondary,
                Font = DarkGeekTheme.CreateFontPublic(9.5f, FontStyle.Regular),
                ScrollBars = ScrollBars.None,
                TabStop = false,
                Text = request.Message,
            };

            int lineCount = message.GetLineFromCharIndex(message.TextLength) + 1;
            message.Height = lineCount * message.Font.Height + 8;
            y = message.Bottom + 14;
            Controls.Add(message);
        }

        // ── 选项表格（Excel 风格：序号 | 待选操作）──
        DataGridView? grid = null;
        if (hasOptions)
        {
            grid = new DataGridView
            {
                Location = new Point(PadLeft, y),
                Size = new Size(ContentWidth, Math.Min(options.Count, 8) * ItemHeight + 6),
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AllowUserToResizeColumns = false,
                MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                EditMode = DataGridViewEditMode.EditProgrammatically,
                ColumnHeadersHeight = 34,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                BackgroundColor = DarkGeekTheme.CardBack,
                BorderStyle = BorderStyle.FixedSingle,
                GridColor = DarkGeekTheme.Border,
                EnableHeadersVisualStyles = false,
                RowTemplate = { Height = ItemHeight },
                Font = DarkGeekTheme.CreateFontPublic(9.5f, FontStyle.Regular),
            };

            // 先套用全局表格样式，再覆盖本窗口需要的设置（样式里会打开滚动条与行头）
            DarkGeekTheme.StyleGridExcel(grid, readOnly: true);

            // 只要「待选操作」这一列：不要行号列、不要表头
            grid.ScrollBars = ScrollBars.None;
            grid.RowHeadersVisible = false;
            grid.ColumnHeadersVisible = false;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            grid.RowTemplate.Height = ItemHeight;
            // 保留 Excel 的横向行分隔线
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;

            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "option",
                HeaderText = "待选操作",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                Width = ContentWidth - 6,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleLeft },
            });

            foreach (string option in options)
                grid.Rows.Add(option);

            grid.CurrentCell = grid.Rows[0].Cells["option"];
            grid.Rows[0].Selected = true;

            // 双击直接确定
            grid.CellDoubleClick += (_, _) => Accept();

            Controls.Add(grid);
            y = grid.Bottom + 12;
        }

        var ok = Ui.Button(hasOptions ? "确定" : cancellable ? "已处理，继续" : "知道了",
            ButtonKind.Primary, (_, _) => Accept(), 132);
        ok.Location = new Point(PadLeft + ContentWidth - 132, y);

        Control? cancel = null;
        if (cancellable)
        {
            cancel = Ui.Button("取消", ButtonKind.Ghost,
                (_, _) => { DialogResult = DialogResult.Cancel; Close(); }, 80);
            cancel.Location = new Point(ok.Left - 80 - 10, y);
        }

        Controls.Add(accent);
        Controls.Add(badge);
        Controls.Add(title);
        Controls.Add(close);
        Controls.Add(ok);
        if (cancel != null) Controls.Add(cancel);

        ClientSize = new Size(ContentWidth + PadLeft * 2, ok.Bottom + 18);
        accent.Height = ClientSize.Height;

        void Accept()
        {
            SelectedOption = grid?.CurrentRow?.Cells["option"].Value?.ToString() ?? "确定";
            DialogResult = DialogResult.OK;
            Close();
        }

        // 无标题栏：按住标题区拖动窗口
        void BeginDrag(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            _dragging = true;
            _dragOrigin = new Point(e.X, e.Y);
        }

        void Drag(object? sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            Location = new Point(Left + e.X - _dragOrigin.X, Top + e.Y - _dragOrigin.Y);
        }

        void EndDrag(object? sender, MouseEventArgs e) => _dragging = false;

        foreach (var control in new Control[] { this, title, badge, accent })
        {
            control.MouseDown += BeginDrag;
            control.MouseMove += Drag;
            control.MouseUp += EndDrag;
        }

        KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Escape) return;

            if (cancellable)
            {
                DialogResult = DialogResult.Cancel;
                Close();
            }
            else
            {
                Accept();
            }
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ClassStyle |= 0x20000; // CS_DROPSHADOW：无边框窗口补一层阴影
            return parameters;
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyRoundedRegion();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyRoundedRegion();
    }

    /// <summary>把窗口裁剪成圆角矩形（无边框窗口需要自己做圆角）。</summary>
    private void ApplyRoundedRegion()
    {
        if (Width <= 0 || Height <= 0) return;

        using var path = new GraphicsPath();
        int d = Radius * 2;

        path.AddArc(0, 0, d, d, 180, 90);
        path.AddArc(Width - d, 0, d, d, 270, 90);
        path.AddArc(Width - d, Height - d, d, d, 0, 90);
        path.AddArc(0, Height - d, d, d, 90, 90);
        path.CloseFigure();

        var old = Region;
        Region = new Region(path);
        old?.Dispose();
    }

    /// <summary>在 UI 线程弹框并返回用户选择结果。</summary>
    public static UiPromptResult ShowPrompt(IWin32Window? owner, UiPromptRequest request)
    {
        using var dialog = new NotifyDialog(request);
        var result = owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner);

        return new UiPromptResult
        {
            Delivered = true,
            Confirmed = result == DialogResult.OK,
            Selected = result == DialogResult.OK ? dialog.SelectedOption : null,
        };
    }
}
