using JLinkStudio.Core.Models;
using JLinkStudio.Core.Services;
using JLinkStudio.WinFormsUI.Theme;

namespace JLinkStudio.WinFormsUI.Controls;

/// <summary>ELF 符号快速检索弹窗：模糊搜索 + 表格多选加入监控。</summary>
public sealed class SymbolSearchDialog : Form
{
    private const int ResultLimit = 500;

    private readonly SymbolIndex _index;
    private readonly RoundedTextBox _search;
    private readonly DataGridView _grid;
    private readonly Label _hint;

    public List<WatchSymbol> SelectedSymbols { get; } = new();

    public SymbolSearchDialog(SymbolIndex index)
    {
        _index = index;

        Text = "搜索 ELF 符号";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(720, 552);
        BackColor = DarkGeekTheme.PageBack;
        ForeColor = DarkGeekTheme.TextPrimary;
        Font = DarkGeekTheme.UiFont;

        // ── 标题 / 副标题（显示当前 ELF 与符号总数）──
        var title = Ui.Text("🔍 搜索 ELF 符号", true, DarkGeekTheme.TextPrimary, 11f);
        title.Location = new Point(18, 14);

        string elfName = string.IsNullOrWhiteSpace(index.SourceFile)
            ? "未加载 ELF 文件"
            : Path.GetFileName(index.SourceFile);
        var subtitle = Ui.Text($"{elfName} · 共 {index.Count} 个符号", false, DarkGeekTheme.TextMuted, 8.75f);
        subtitle.Location = new Point(18, 40);

        // ── 搜索框 ──
        _search = Ui.Input("", 660);
        _search.Location = new Point(18, 68);
        _search.Width = 684;
        _search.PlaceholderText = "输入变量名或结构体成员，如 motor / pid / g_motorPid.kp";

        // ── 结果表格：变量名 / 地址 / 类型，支持 Ctrl / Shift 多选 ──
        _grid = new DataGridView { Dock = DockStyle.Fill, MultiSelect = true };
        DarkGeekTheme.StyleGridExcel(_grid, readOnly: true,
            selectionMode: DataGridViewSelectionMode.FullRowSelect);
        _grid.MultiSelect = true;
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "变量名称",
            Name = "name",
            FillWeight = 64,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "地址",
            Name = "address",
            FillWeight = 20,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "类型",
            Name = "type",
            FillWeight = 16,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });

        var gridFrame = new RoundedFrame
        {
            Location = new Point(18, 112),
            Size = new Size(684, 380),
            BackColor = DarkGeekTheme.InputBack,
            BorderColor = DarkGeekTheme.GridLineSoft,
            Radius = 8,
            Padding = new Padding(1),
        };
        gridFrame.Controls.Add(_grid);

        // ── 底部：结果统计 + 操作按钮 ──
        _hint = Ui.Text("", false, DarkGeekTheme.TextMuted, 8.5f);
        _hint.Location = new Point(18, 500);

        var ok = Ui.Button("加入监控", ButtonKind.Primary, (_, _) => Accept(), 110);
        ok.Location = new Point(494, 496);
        var cancel = Ui.Button("取消", ButtonKind.Ghost, (_, _) => DialogResult = DialogResult.Cancel, 90);
        cancel.Location = new Point(612, 496);

        Controls.AddRange(new Control[] { title, subtitle, _search, gridFrame, _hint, ok, cancel });

        // ── 交互 ──
        _search.TextChanged += (_, _) => RefreshList();
        _search.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                Accept();
            }
            else if (e.KeyCode == Keys.Down)
            {
                e.SuppressKeyPress = true;
                _grid.Focus();
                if (_grid.Rows.Count > 0) _grid.CurrentCell = _grid.Rows[0].Cells[0];
            }
        };

        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0) Accept();
        };
        _grid.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                Accept();
            }
        };

        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
        };

        Shown += (_, _) => _search.FocusInput();

        RefreshList();
    }

    private void RefreshList()
    {
        _grid.SuspendLayout();
        _grid.Rows.Clear();

        var results = _index.Search(_search.Text, ResultLimit)
            .OrderBy(s => s.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var symbol in results)
        {
            int index = _grid.Rows.Add(
                symbol.FullPath,
                $"0x{symbol.Address:X8}",
                symbol.Type.ToCName());
            _grid.Rows[index].Tag = symbol;
        }

        _grid.ResumeLayout();

        if (_grid.Rows.Count > 0) _grid.CurrentCell = _grid.Rows[0].Cells[0];

        _hint.Text = results.Count == 0
            ? "没有匹配的符号，换个关键字试试（支持模糊匹配，如 motor / pid / adc）"
            : $"匹配 {results.Count} 个符号 · 可 Ctrl / Shift 多选，双击或回车加入监控";
    }

    private void Accept()
    {
        SelectedSymbols.Clear();

        if (_grid.SelectedRows.Count > 0)
        {
            foreach (DataGridViewRow row in _grid.SelectedRows)
            {
                if (row.Tag is WatchSymbol symbol) SelectedSymbols.Add(symbol);
            }
        }
        else if (_grid.CurrentRow?.Tag is WatchSymbol current)
        {
            SelectedSymbols.Add(current);
        }

        if (SelectedSymbols.Count == 0) return;

        DialogResult = DialogResult.OK;
    }
}
