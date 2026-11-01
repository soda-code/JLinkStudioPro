using JLinkStudio.Core.Models;
using JLinkStudio.WinFormsUI.Theme;

namespace JLinkStudio.WinFormsUI.Controls;

/// <summary>变量在线改写弹窗（支持 0x / 0b / 十进制 / 浮点输入）。</summary>
public sealed class ValueEditDialog : Form
{
    private readonly RoundedTextBox _input;

    public string ValueText => _input.Text.Trim();

    public ValueEditDialog(string symbolPath, DataType type, string currentText)
    {
        Text = "在线修改变量";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(460, 196);
        BackColor = DarkGeekTheme.PageBack;
        ForeColor = DarkGeekTheme.TextPrimary;
        Font = DarkGeekTheme.UiFont;

        var title = Ui.Text(symbolPath, true, DarkGeekTheme.TextPrimary, 11f);
        title.Location = new Point(18, 16);

        var hint = Ui.Text($"类型: {type.ToCName()} · 支持 0x1F / 0b1010 / 1.65 等写法", false, DarkGeekTheme.TextMuted, 8.75f);
        hint.Location = new Point(18, 44);

        _input = Ui.Input(currentText, 424);
        _input.Location = new Point(18, 72);
        _input.Font = DarkGeekTheme.MonoFont;

        var ok = Ui.Button("写入", ButtonKind.Primary, (_, _) =>
        {
            if (ValueText.Length == 0) return;
            DialogResult = DialogResult.OK;
        }, 100);
        ok.Location = new Point(18, 122);

        var cancel = Ui.Button("取消", ButtonKind.Ghost, (_, _) => DialogResult = DialogResult.Cancel, 90);
        cancel.Location = new Point(128, 122);

        Controls.AddRange(new Control[] { title, hint, _input, ok, cancel });
        Shown += (_, _) =>
        {
            _input.FocusInput();
            _input.SelectAll();
        };
        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && ValueText.Length > 0) DialogResult = DialogResult.OK;
            if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
        };
    }
}
