using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using JLinkStudio.Core.Models;
using JLinkStudio.WinFormsUI.Theme;

namespace JLinkStudio.WinFormsUI.Controls;

/// <summary>
/// 寄存器说明弹窗：双击硬件寄存器时展示位域明细。
/// 无边框圆角窗口 + 自绘标题栏（可拖动，Esc / 右上角 ✕ 关闭），
/// 只保留位域明细，避免上方堆叠大量文字把表格挤小。
/// </summary>
public sealed class RegisterInfoDialog : Form
{
    private const int PadLeft = 18;
    private const int ContentWidth = 664;
    private const int CornerRadius = 10;
    private const int WmNcLButtonDown = 0xA1;
    private const int HtCaption = 2;
    private const int CsDropShadow = 0x20000;

    private Region? _roundedRegion;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    public RegisterInfoDialog(SvdPeripheral peripheral, SvdRegister register)
    {
        Text = $"寄存器说明 - {register.Name}";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(700, 480);
        BackColor = DarkGeekTheme.PageBack;
        ForeColor = DarkGeekTheme.TextPrimary;
        Font = DarkGeekTheme.UiFont;
        DoubleBuffered = true;

        string titleText = string.IsNullOrWhiteSpace(register.DisplayName)
            ? register.Name
            : $"{register.Name} · {register.DisplayName}";
        var title = Ui.Text(titleText, true, DarkGeekTheme.TextPrimary, 11f);
        title.Location = new Point(PadLeft, 14);

        // 外设 / 地址 / 位宽 / 权限 / 复位值压成一行副标题，不再单独占一块信息区
        var subtitle = Ui.Text(
            $"{peripheral.Name} · {register.AddressText} · {register.SizeBits} 位 · {register.Access} · 复位 0x{register.ResetValue:X8}",
            false, DarkGeekTheme.TextMuted, 8.75f);
        subtitle.Location = new Point(PadLeft, 40);

        var hairline = new Panel
        {
            Location = new Point(PadLeft, 70),
            Size = new Size(ContentWidth, 1),
            BackColor = DarkGeekTheme.Border,
        };

        // 右上角关闭
        var closeIcon = new Label
        {
            Text = "✕",
            Location = new Point(ClientSize.Width - PadLeft - 24, 12),
            Size = new Size(24, 24),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = DarkGeekTheme.TextMuted,
            BackColor = Color.Transparent,
            Cursor = Cursors.Hand,
            Font = DarkGeekTheme.UiFont,
        };
        closeIcon.Click += (_, _) => Close();
        closeIcon.MouseEnter += (_, _) => closeIcon.ForeColor = DarkGeekTheme.Danger;
        closeIcon.MouseLeave += (_, _) => closeIcon.ForeColor = DarkGeekTheme.TextMuted;

        // ── 位域明细（唯一主体） ──
        var fieldLabel = Ui.Text($"位域明细（{register.Fields.Count} 个）", true, DarkGeekTheme.TextSecondary, 9f);
        fieldLabel.Location = new Point(PadLeft, 86);

        var grid = new DataGridView { Dock = DockStyle.Fill };
        DarkGeekTheme.StyleGridExcel(grid, readOnly: true);
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "bits", HeaderText = "位域", FillWeight = 18 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "名称", FillWeight = 24 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "access", HeaderText = "权限", FillWeight = 18 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "desc", HeaderText = "说明", FillWeight = 72 });

        foreach (var field in register.Fields.OrderByDescending(f => f.BitOffset))
        {
            string desc = field.Description ?? string.Empty;
            if (field.EnumeratedValues.Count > 0)
            {
                string enums = string.Join("; ", field.EnumeratedValues
                    .OrderBy(kv => kv.Key)
                    .Select(kv => $"{kv.Key}={kv.Value}"));
                desc = string.IsNullOrWhiteSpace(desc) ? enums : $"{desc}（{enums}）";
            }

            grid.Rows.Add(field.BitsText, field.Name, field.Access, desc);
        }

        if (grid.Rows.Count == 0)
        {
            grid.Rows.Add("—", "（无位域信息）", "—", "SVD 中未定义该寄存器的位域");
        }

        var gridFrame = new RoundedFrame
        {
            Location = new Point(PadLeft, 110),
            Size = new Size(ContentWidth, 300),
            BackColor = DarkGeekTheme.InputBack,
            BorderColor = DarkGeekTheme.GridLineSoft,
            Radius = 6,
            Padding = new Padding(1),
        };
        gridFrame.Controls.Add(grid);

        var close = Ui.Button("关闭", ButtonKind.Primary, (_, _) => Close(), 96);
        close.Location = new Point(ClientSize.Width - PadLeft - 96, 426);

        Controls.AddRange(new Control[]
        {
            title, subtitle, hairline, closeIcon,
            fieldLabel, gridFrame, close,
        });

        // 无边框窗口：标题区拖动
        MouseDown += DragWindow;
        title.MouseDown += DragWindow;
        subtitle.MouseDown += DragWindow;

        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) Close();
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= CsDropShadow;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyRoundedRegion();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyRoundedRegion();
    }

    /// <summary>把窗口外形裁成圆角，去掉直角边框。</summary>
    private void ApplyRoundedRegion()
    {
        if (!IsHandleCreated || Width <= 0 || Height <= 0) return;

        using var path = DarkGeekTheme.RoundedRect(new Rectangle(0, 0, Width, Height), CornerRadius);
        var region = new Region(path);
        var previous = _roundedRegion;
        _roundedRegion = region;
        Region = region;
        previous?.Dispose();
    }

    /// <summary>沿窗口外形描一圈细边，避免圆角处与背景糊在一起。</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = DarkGeekTheme.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), CornerRadius);
        using var pen = new Pen(DarkGeekTheme.Border);
        e.Graphics.DrawPath(pen, path);
    }

    private void DragWindow(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, WmNcLButtonDown, HtCaption, 0);
    }
}
