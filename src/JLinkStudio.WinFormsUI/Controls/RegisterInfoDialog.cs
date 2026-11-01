using JLinkStudio.Core.Models;
using JLinkStudio.WinFormsUI.Theme;

namespace JLinkStudio.WinFormsUI.Controls;

/// <summary>
/// 寄存器说明弹窗：双击硬件寄存器时展示外设 / 地址 / 宽度 / 复位值 / 权限 / 描述与位域明细。
/// 数据全部来自已加载的 SVD 描述文件。
/// </summary>
public sealed class RegisterInfoDialog : Form
{
    private const int PadLeft = 18;
    private const int ContentWidth = 664;

    public RegisterInfoDialog(SvdPeripheral peripheral, SvdRegister register)
    {
        Text = $"寄存器说明 - {register.Name}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(700, 580);
        BackColor = DarkGeekTheme.PageBack;
        ForeColor = DarkGeekTheme.TextPrimary;
        Font = DarkGeekTheme.UiFont;

        string titleText = string.IsNullOrWhiteSpace(register.DisplayName)
            ? register.Name
            : $"{register.Name} · {register.DisplayName}";
        var title = Ui.Text(titleText, true, DarkGeekTheme.TextPrimary, 11f);
        title.Location = new Point(PadLeft, 14);

        var subtitle = Ui.Text($"{peripheral.Name} · {peripheral.Description ?? peripheral.GroupName ?? "外设"}",
            false, DarkGeekTheme.TextMuted, 8.75f);
        subtitle.Location = new Point(PadLeft, 40);

        var hairline = new Panel
        {
            Location = new Point(PadLeft, 66),
            Size = new Size(ContentWidth, 1),
            BackColor = DarkGeekTheme.Border,
        };

        // ── 基本信息 ──
        var info = new Panel
        {
            Location = new Point(PadLeft, 76),
            Size = new Size(ContentWidth, 84),
            BackColor = Color.Transparent,
        };
        AddInfo(info, "外设", peripheral.Name, 0, 0);
        AddInfo(info, "地址", register.AddressText, 340, 0);
        AddInfo(info, "偏移", $"0x{register.Offset:X}", 0, 28);
        AddInfo(info, "宽度", $"{register.SizeBits} 位", 340, 28);
        AddInfo(info, "复位值", $"0x{register.ResetValue:X8}", 0, 56);
        AddInfo(info, "权限", register.Access, 340, 56);

        // ── 说明 ──
        var descLabel = Ui.Text("说明", true, DarkGeekTheme.TextSecondary, 9f);
        descLabel.Location = new Point(PadLeft, 168);

        string description = string.IsNullOrWhiteSpace(register.Description)
            ? "（SVD 中未提供该寄存器的文字说明）"
            : register.Description!;
        if (!string.IsNullOrEmpty(register.ReadAction))
            description += $"\r\n读取副作用：{register.ReadAction}（读取该寄存器可能清除或改变其值）";

        var descBox = new RichTextBox
        {
            Location = new Point(PadLeft, 190),
            Size = new Size(ContentWidth, 84),
            BackColor = DarkGeekTheme.InputBack,
            ForeColor = DarkGeekTheme.TextPrimary,
            BorderStyle = BorderStyle.None,
            Font = DarkGeekTheme.UiFont,
            ReadOnly = true,
            Text = description,
        };
        var descFrame = new RoundedFrame
        {
            Location = new Point(PadLeft, 190),
            Size = new Size(ContentWidth, 84),
            BackColor = DarkGeekTheme.InputBack,
            BorderColor = DarkGeekTheme.GridLineSoft,
            Radius = 6,
            Padding = new Padding(8, 6, 8, 6),
        };
        descFrame.Controls.Add(descBox);
        descBox.Dock = DockStyle.Fill;

        // ── 位域明细 ──
        var fieldLabel = Ui.Text($"位域明细（{register.Fields.Count} 个）", true, DarkGeekTheme.TextSecondary, 9f);
        fieldLabel.Location = new Point(PadLeft, 286);

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
            Location = new Point(PadLeft, 308),
            Size = new Size(ContentWidth, 196),
            BackColor = DarkGeekTheme.InputBack,
            BorderColor = DarkGeekTheme.GridLineSoft,
            Radius = 6,
            Padding = new Padding(1),
        };
        gridFrame.Controls.Add(grid);

        var close = Ui.Button("关闭", ButtonKind.Primary, (_, _) => Close(), 96);
        close.Location = new Point(700 - PadLeft - 96, 522);

        Controls.AddRange(new Control[]
        {
            title, subtitle, hairline, info,
            descLabel, descFrame,
            fieldLabel, gridFrame, close,
        });

        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) Close();
        };
    }

    private static void AddInfo(Panel host, string label, string value, int x, int y)
    {
        host.Controls.Add(new Label
        {
            Text = label,
            Location = new Point(x, y),
            Size = new Size(48, 24),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = DarkGeekTheme.TextMuted,
            BackColor = Color.Transparent,
            Font = DarkGeekTheme.UiFontSmall,
        });

        host.Controls.Add(new Label
        {
            Text = value,
            Location = new Point(x + 48, y),
            Size = new Size(284, 24),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = DarkGeekTheme.TextPrimary,
            BackColor = Color.Transparent,
            Font = DarkGeekTheme.MonoFont,
        });
    }
}
