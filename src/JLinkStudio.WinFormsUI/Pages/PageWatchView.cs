using JLinkStudio.Core.Models;
using JLinkStudio.Core.Services;
using JLinkStudio.WinFormsUI.Controls;
using JLinkStudio.WinFormsUI.Navigation;
using JLinkStudio.WinFormsUI.Theme;
using ScottPlot.WinForms;

namespace JLinkStudio.WinFormsUI.Pages;

/// <summary>子界面 3：变量实时监控树表 + ScottPlot 多通道示波器。</summary>
public sealed class PageWatchView : UserControl, ISubPageView
{
    private static readonly string[] Palette =
    {
        "#3B82F6", "#10B981", "#F59E0B", "#EF4444", "#8B5CF6",
        "#06B6D4", "#F472B6", "#84CC16", "#F97316", "#A3E635",
    };

    private static readonly string[] FormatNames = { "Dec", "Hex", "Bin", "Float" };
    private static readonly int[] SampleRates = { 10, 50, 100, 200, 500, 1000 };

    private readonly AppHost _host;
    private readonly DataGridView _grid = new();
    private readonly FormsPlot _plot = new();
    private readonly RoundedSelect _cmbRate = new();
    private readonly RoundedSelect _cmbView = new();
    private readonly FlatCheckBox _chkPauseWave = new();
    private readonly FlatCheckBox _chkAutoFollow = new();
    private readonly Label _lblInfo = new();
    private Control _gridHost = new Panel();
    private Control _plotPanel = new Panel();
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 100 };

    private readonly Dictionary<string, int> _rowIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _plotPaths = new();
    private readonly Dictionary<string, int> _plotColors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lastValueText = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _flashUntil = new(StringComparer.OrdinalIgnoreCase);

    private bool _rebuilding;
    private bool _activated;
    private bool _autoSeeded;
    private long _plotStartMs;
    private int _plotTick;

    public PageWatchView(AppHost host)
    {
        _host = host;
        Dock = DockStyle.Fill;
        BackColor = DarkGeekTheme.PageBack;
        Padding = new Padding(16, 4, 16, 12);

        // 变量页不显示卡片标题行，顶部留白相应收窄
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 10, 16, 14),
        };

        BuildToolbar(out var toolbar);

        // 单视图容器：变量列表与实时曲线通过工具栏下拉框切换，不再左右分栏
        var viewHost = new Panel { Dock = DockStyle.Fill, BackColor = DarkGeekTheme.CardBack };
        _gridHost = Ui.WrapDisplay(_grid, DarkGeekTheme.CardBack, 6);
        _plotPanel = BuildPlotPanel();
        _plotPanel.Visible = false;
        viewHost.Controls.Add(_gridHost);
        viewHost.Controls.Add(_plotPanel);

        card.Controls.Add(viewHost);
        card.Controls.Add(toolbar);
        Controls.Add(card);

        ConfigureGrid();
        ConfigurePlot();

        Ctx.Variables.WatchListChanged += (_, _) => SafeInvoke(RebuildGrid);
        Ctx.Device.SymbolsChanged += (_, _) => SafeInvoke(() =>
        {
            TrySeedWatches();
            RebuildGrid();
        });
        Ctx.Variables.ValueWritten += OnValueWritten;
        _uiTimer.Tick += (_, _) => RefreshValues();
        _uiTimer.Start();
    }

    private StudioContext Ctx => _host.Context;

    public string PageKey => "watch";
    public string PageTitle => "👀 变量监控";

    public void OnPageActivated()
    {
        _activated = true;
        TrySeedWatches();
        RebuildGrid();
        if (_plotStartMs == 0) _plotStartMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    /// <summary>首次进入且监控列表为空时，自动装载一组演示变量，避免空白页。</summary>
    private void TrySeedWatches()
    {
        if (_autoSeeded || Ctx.Variables.WatchedRoots.Count > 0 || Ctx.Device.Symbols.Count == 0) return;
        _autoSeeded = true;

        var seeds = new[] { "g_motorPid", "g_actualSpeed", "g_targetSpeed" }
            .Select(Ctx.Device.Symbols.Find)
            .Where(s => s != null)
            .Cast<WatchSymbol>()
            .ToList();

        if (seeds.Count == 0) return;
        Ctx.Variables.AddWatches(seeds);

        // 默认把实际转速加入波形，首屏即可看到曲线
        var speed = seeds.FirstOrDefault(s => s.Name == "g_actualSpeed");
        if (speed != null) speed.Enabled = true;
    }

    public void OnPageHidden() => _activated = false;

    private void BuildToolbar(out Panel toolbar)
    {
        // 两行布局：第一行=符号搜索与监控管理，第二行=视图切换与采样/波形控制
        toolbar = new Panel { Dock = DockStyle.Top, Height = 88, BackColor = DarkGeekTheme.CardBack, Padding = new Padding(0, 0, 0, 4) };

        var rows = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

        var rowTop = NewToolbarRow();
        var rowBottom = NewToolbarRow();
        rows.Controls.Add(rowTop, 0, 0);
        rows.Controls.Add(rowBottom, 0, 1);

        // ---- 第一行：符号加载 + 监控列表管理 ----
        rowTop.Controls.Add(Ui.Button("📂 选择 ELF", ButtonKind.Ghost, (_, _) => SelectElf(), 104));
        rowTop.Controls.Add(Ui.Button("+ 添加监控", ButtonKind.Primary, (_, _) => OpenSymbolSearch(), 104));
        rowTop.Controls.Add(Ui.Button("清空", ButtonKind.Ghost, (_, _) =>
        {
            Ctx.Variables.ClearWatches();
            RebuildGrid();
        }, 64));

        _lblInfo.AutoSize = true;
        _lblInfo.ForeColor = DarkGeekTheme.TextMuted;
        _lblInfo.Font = DarkGeekTheme.UiFontSmall;
        _lblInfo.Margin = new Padding(6, 12, 0, 0);
        rowTop.Controls.Add(_lblInfo);

        // ---- 第二行：视图切换 + 采样率 + 波形控制 ----
        rowBottom.Controls.Add(ToolbarLabel("视图"));
        _cmbView.AddRange("变量列表", "实时曲线");
        _cmbView.Width = 110;
        _cmbView.SelectedIndex = 0;
        _cmbView.SelectedIndexChanged += (_, _) => ShowView(_cmbView.SelectedIndex);
        rowBottom.Controls.Add(_cmbView);

        rowBottom.Controls.Add(ToolbarLabel("采样率"));
        _cmbRate.AddRange(SampleRates.Select(r => $"{r} Hz").ToArray());
        _cmbRate.Width = 96;
        _cmbRate.SelectedIndex = 3;
        _cmbRate.SelectedIndexChanged += (_, _) =>
        {
            Ctx.Variables.SampleRateHz = SampleRates[Math.Max(0, _cmbRate.SelectedIndex)];
        };
        rowBottom.Controls.Add(_cmbRate);

        _chkPauseWave.Text = "暂停波形";
        _chkPauseWave.AutoSize = true;
        DarkGeekTheme.StyleCheck(_chkPauseWave);
        _chkPauseWave.Margin = new Padding(0, 8, 10, 0);
        rowBottom.Controls.Add(_chkPauseWave);

        _chkAutoFollow.Text = "自动跟随";
        _chkAutoFollow.Checked = true;
        _chkAutoFollow.AutoSize = true;
        DarkGeekTheme.StyleCheck(_chkAutoFollow);
        _chkAutoFollow.Margin = new Padding(0, 8, 10, 0);
        rowBottom.Controls.Add(_chkAutoFollow);

        rowBottom.Controls.Add(Ui.Button("导出 CSV", ButtonKind.Ghost, (_, _) => ExportCsv(), 92));

        toolbar.Controls.Add(rows);
    }

    private static FlowLayoutPanel NewToolbarRow() => new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = false,
        BackColor = Color.Transparent,
        Margin = new Padding(0),
        Padding = new Padding(0),
    };

    /// <summary>工具栏标签：与下拉框同高（32px）、顶对齐、文字垂直居中，保证整行视觉一致。</summary>
    private static Label ToolbarLabel(string text)
    {
        var label = Ui.Text(text, true, size: 9f);
        int width = label.PreferredWidth;
        label.AutoSize = false;
        label.Width = width;
        label.Height = 32;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.Margin = new Padding(0, 4, 8, 4);
        return label;
    }

    /// <summary>切换显示视图：0 = 变量列表，1 = 实时曲线。</summary>
    private void ShowView(int index)
    {
        bool showPlot = index == 1;
        _gridHost.Visible = !showPlot;
        _plotPanel.Visible = showPlot;
        if (showPlot) UpdatePlot();
    }

    private Control BuildPlotPanel()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = DarkGeekTheme.CardBack, Padding = new Padding(6, 0, 0, 0) };
        var header = new Panel { Dock = DockStyle.Top, Height = 28, BackColor = DarkGeekTheme.CardBack };
        var label = Ui.Text("📈 实时多通道波形", true, DarkGeekTheme.TextSecondary, 9f);
        label.Location = new Point(4, 6);
        header.Controls.Add(label);
        host.Controls.Add(_plot);
        host.Controls.Add(header);
        return host;
    }

    private void ConfigureGrid()
    {
        DarkGeekTheme.StyleGridExcel(_grid, readOnly: false);
        _grid.Columns.Clear();

        // 按 FillWeight 分配宽度：变量名与当前值占主要空间，地址/类型/格式/颜色保持紧凑
        _grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "enabled",
            HeaderText = "使能",
            FillWeight = 38,
            MinimumWidth = 46,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                NullValue = CheckState.Checked,
            },
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "变量名称", ReadOnly = true, FillWeight = 200, MinimumWidth = 150 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "address", HeaderText = "地址", ReadOnly = true, FillWeight = 100, MinimumWidth = 88 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "type", HeaderText = "类型", ReadOnly = true, FillWeight = 58, MinimumWidth = 52 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "value", HeaderText = "当前值", ReadOnly = true, FillWeight = 120, MinimumWidth = 96 });
        var formatColumn = new DataGridViewComboBoxColumn
        {
            Name = "format",
            HeaderText = "格式",
            ReadOnly = true,
            FillWeight = 62,
            MinimumWidth = 58,
            FlatStyle = FlatStyle.Flat,
        };
        formatColumn.Items.AddRange(FormatNames);
        _grid.Columns.Add(formatColumn);
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "color", HeaderText = "颜色", ReadOnly = true, FillWeight = 48, MinimumWidth = 54 });

        // 列宽按比例自适应铺满可用宽度，不再右侧留白（Excel 风格由主题统一设置）
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;

        _grid.CellValueChanged += OnCellValueChanged;
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellDoubleClick += OnCellDoubleClick;
        _grid.CellMouseClick += OnCellMouseClick;
        _grid.CellPainting += OnCellPainting;
        _grid.CellClick += OnCellClick;
    }

    private void ConfigurePlot()
    {
        _plot.Dock = DockStyle.Fill;
        var plot = _plot.Plot;
        plot.FigureBackground.Color = ToScott(DarkGeekTheme.CardBack);
        plot.DataBackground.Color = ToScott(DarkGeekTheme.PlotBack);
        plot.Axes.Color(ToScott(DarkGeekTheme.TextSecondary));
        plot.Grid.MajorLineColor = ToScott(DarkGeekTheme.PlotGrid);
        plot.Legend.IsVisible = true;
        plot.Legend.BackgroundColor = ToScott(DarkGeekTheme.PlotBack);
        plot.Legend.FontColor = ToScott(DarkGeekTheme.TextPrimary);
        plot.Legend.OutlineColor = ToScott(DarkGeekTheme.PlotGrid);
        // ScottPlot 默认字库不含中文字形，坐标轴文案使用英文避免出现方块
        plot.Title("Realtime Waveform");
        plot.XLabel("Time (s)");
        plot.YLabel("Value");

        // 用户手动缩放/拖拽后自动关闭“自动跟随”，保留其视图
        _plot.MouseWheel += (_, _) => _chkAutoFollow.Checked = false;
        _plot.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) _chkAutoFollow.Checked = false;
        };
    }

    private static ScottPlot.Color ToScott(Color color) => new(color.R, color.G, color.B);

    private void RebuildGrid()
    {
        if (_rebuilding || _grid.IsDisposed) return;
        _rebuilding = true;
        try
        {
            var selectedPath = _grid.CurrentRow?.Tag is WatchSymbol current ? current.FullPath : null;
            _grid.Rows.Clear();
            _rowIndex.Clear();
            _lastValueText.Clear();
            _flashUntil.Clear();

            foreach (var root in Ctx.Variables.WatchedRoots)
                AddSymbolRow(root, 0);

            if (selectedPath != null && _rowIndex.TryGetValue(selectedPath, out var rowIndex))
                _grid.CurrentCell = _grid.Rows[rowIndex].Cells["name"];

            string elfPath = Ctx.Device.Config.ElfPath;
            string elfInfo = string.IsNullOrWhiteSpace(elfPath) ? "未加载 ELF" : $"ELF: {elfPath}";
            _lblInfo.Text = $"{elfInfo} · 监控 {Ctx.Variables.ActiveLeafCount} 项 · 符号库 {Ctx.Device.Symbols.Count} 项";
            RebuildScatters();
        }
        finally
        {
            _rebuilding = false;
        }
    }

    private void AddSymbolRow(WatchSymbol symbol, int depth)
    {
        string indent = new(' ', depth * 3);

        string displayName;
        if (symbol.IsContainer)
        {
            // ▾ / ▸ 表示展开状态；数组用 ▦ / ▨ 区分
            string icon = symbol.Type == DataType.Array
                ? symbol.Expanded ? "▦" : "▨"
                : symbol.Expanded ? "▾" : "▸";
            displayName = $"{indent}{icon} {symbol.Name}";
        }
        else
        {
            displayName = $"{indent}{symbol.Name}";
        }

        // 结构体 / 数组行不读内存，当前值列显示成员数量摘要
        string valueText = symbol.IsContainer
            ? symbol.Type == DataType.Array ? $"[{symbol.Children.Count}]" : $"{{ {symbol.Children.Count} 项 }}"
            : symbol.CurrentValueText;

        int index = _grid.Rows.Add(
            symbol.Enabled,
            displayName,
            $"0x{symbol.Address:X8}",
            symbol.Type.ToCName(),
            valueText,
            symbol.Format.ToString(),
            string.Empty);

        var row = _grid.Rows[index];
        row.Tag = symbol;
        if (symbol.IsContainer) row.ReadOnly = true;

        _rowIndex[symbol.FullPath] = index;
        _lastValueText[symbol.FullPath] = symbol.CurrentValueText;

        if (!symbol.Expanded) return;

        foreach (var child in symbol.Children)
            AddSymbolRow(child, depth + 1);
    }

    private void RebuildScatters()
    {
        _plotPaths.Clear();
        _plotColors.Clear();
        foreach (var symbol in Ctx.Variables.WatchedRoots.SelectMany(r => r.Flatten()))
        {
            if (!symbol.Enabled || symbol.IsContainer) continue;
            _plotPaths.Add(symbol.FullPath);
            _plotColors[symbol.FullPath] = ResolveColor(symbol).ToArgb();
        }

        _plot.Plot.Clear();
        _plot.Refresh();
    }

    /// <summary>解析变量的曲线颜色：未指定时按调色板分配并固化，保证颜色稳定且可编辑。</summary>
    private static Color ResolveColor(WatchSymbol symbol)
    {
        if (symbol.ColorArgb != 0) return Color.FromArgb(symbol.ColorArgb);

        var color = ColorTranslator.FromHtml(Palette[Math.Abs(symbol.FullPath.GetHashCode()) % Palette.Length]);
        symbol.ColorArgb = color.ToArgb();
        return color;
    }

    /// <summary>颜色列自绘：在单元格中绘制圆角色块。</summary>
    private void OnCellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
        if (_grid.Columns[e.ColumnIndex].Name != "color") return;
        if (_grid.Rows[e.RowIndex].Tag is not WatchSymbol symbol) return;

        e.PaintBackground(e.ClipBounds, true);
        if (!symbol.IsContainer)
        {
            var color = ResolveColor(symbol);
            var rect = new Rectangle(
                e.CellBounds.X + 10,
                e.CellBounds.Y + e.CellBounds.Height / 2 - 6,
                Math.Max(12, e.CellBounds.Width - 20),
                12);

            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = DarkGeekTheme.RoundedRect(rect, 3);
            using var brush = new SolidBrush(color);
            e.Graphics.FillPath(brush, path);
        }
        e.Handled = true;
    }

    /// <summary>点击颜色列弹出取色器，修改该变量的曲线颜色。</summary>
    private void OnCellClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
        if (_grid.Columns[e.ColumnIndex].Name != "color") return;
        if (_grid.Rows[e.RowIndex].Tag is not WatchSymbol symbol || symbol.IsContainer) return;

        using var dialog = new ColorDialog { FullOpen = true, Color = ResolveColor(symbol) };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        symbol.ColorArgb = dialog.Color.ToArgb();
        RebuildGrid();
        RebuildScatters();
    }

    private void OnCellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_rebuilding || e.RowIndex < 0) return;
        var row = _grid.Rows[e.RowIndex];
        if (row.Tag is not WatchSymbol symbol) return;

        if (e.ColumnIndex == _grid.Columns["enabled"]!.Index)
        {
            symbol.Enabled = row.Cells["enabled"].Value is true;
            RebuildScatters();
        }
    }

    private void OnCellMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0) return;

        // 结构体 / 数组行：左键点名称列即折叠或展开（▾ / ▸ 图标随状态变化）
        if (e.Button == MouseButtons.Left &&
            _grid.Columns[e.ColumnIndex].Name == "name" &&
            _grid.Rows[e.RowIndex].Tag is WatchSymbol container &&
            container.IsContainer)
        {
            container.Expanded = !container.Expanded;
            RebuildGrid();
            return;
        }

        if (_grid.Columns[e.ColumnIndex].Name != "value" || e.Button != MouseButtons.Right) return;
        if (_grid.Rows[e.RowIndex].Tag is not WatchSymbol symbol) return;

        var menu = new ContextMenuStrip
        {
            BackColor = DarkGeekTheme.CardBack,
            ForeColor = DarkGeekTheme.TextPrimary,
            Font = DarkGeekTheme.UiFont,
            ShowImageMargin = false,
        };
        var edit = new ToolStripMenuItem("✏ 在线改写数值");
        edit.Click += (_, _) => BeginEditValue(symbol);

        if (symbol.IsContainer)
        {
            var toggle = new ToolStripMenuItem(symbol.Expanded ? "▸ 折叠子项" : "▾ 展开子项");
            toggle.Click += (_, _) =>
            {
                symbol.Expanded = !symbol.Expanded;
                RebuildGrid();
            };
            menu.Items.Add(toggle);
        }
        var watch = new ToolStripMenuItem(symbol.Enabled ? "⏸ 取消使能" : "▶ 使能");
        watch.Click += (_, _) =>
        {
            symbol.Enabled = !symbol.Enabled;
            RebuildGrid();
        };
        var remove = new ToolStripMenuItem("🗑 移除监控");
        remove.Click += (_, _) =>
        {
            Ctx.Variables.RemoveWatch(symbol);
            RebuildGrid();
        };
        menu.Items.Add(edit);
        menu.Items.Add(watch);
        menu.Items.Add(remove);
        menu.Show(_grid, e.Location);
    }

    private void OnCellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
        if (_grid.Columns[e.ColumnIndex].Name != "value") return;
        if (_grid.Rows[e.RowIndex].Tag is not WatchSymbol symbol || symbol.IsContainer) return;
        BeginEditValue(symbol);
    }

    private void BeginEditValue(WatchSymbol symbol)
    {
        if (_host.IsInspectMode)
        {
            MessageBox.Show(this, "当前为查看模式，已禁止写入变量。\r\n可在「设置 → 调试和查看模式」切回调试模式。",
                "查看模式", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new ValueEditDialog(symbol.FullPath, symbol.Type, symbol.CurrentValueText);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        if (!ValueCodec.TryParseInput(dialog.ValueText, symbol.Type, out var value))
        {
            MessageBox.Show(this, "无法解析输入值，请检查格式。", "写入失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        bool ok = Ctx.Variables.WriteByName(symbol.FullPath, value, "ui", out var error, out var sample);
        if (!ok) MessageBox.Show(this, error, "写入失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        else RefreshValues(force: true);
    }

    private void OnValueWritten(object? sender, VariablePollEngine.VariableWriteEventArgs e)
    {
        SafeInvoke(() =>
        {
            if (_rowIndex.TryGetValue(e.Path, out var index))
            {
                _flashUntil[e.Path] = DateTime.UtcNow.AddMilliseconds(600);
                _grid.Rows[index].Cells["value"].Style.BackColor = DarkGeekTheme.Blend(DarkGeekTheme.Warning, DarkGeekTheme.CardBack, 0.45);
            }
        });
    }

    private void RefreshValues(bool force = false)
    {
        if (!_activated || IsDisposed || _rebuilding) return;

        var samples = Ctx.Variables.LastSamples;
        foreach (var sample in samples)
        {
            if (!_rowIndex.TryGetValue(sample.Name, out var index)) continue;
            var cell = _grid.Rows[index].Cells["value"];
            if (force || _lastValueText.GetValueOrDefault(sample.Name) != sample.ValueText)
            {
                cell.Value = sample.ValueText;
                _lastValueText[sample.Name] = sample.ValueText;
            }

            if (_flashUntil.TryGetValue(sample.Name, out var until))
            {
                if (DateTime.UtcNow > until)
                {
                    _flashUntil.Remove(sample.Name);
                    cell.Style.BackColor = Color.Empty;
                }
            }
        }

        _plotTick++;
        if (!_chkPauseWave.Checked && _plotTick % 3 == 0 && _plotPanel.Visible) UpdatePlot();
    }

    private void UpdatePlot()
    {
        if (_plotStartMs == 0) _plotStartMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var series = new List<(string Path, double[] Xs, double[] Ys)>();
        foreach (var path in _plotPaths)
        {
            var history = Ctx.Variables.GetHistory(path);
            if (history.Count < 2) continue;

            var xs = new double[history.Count];
            var ys = new double[history.Count];
            for (int i = 0; i < history.Count; i++)
            {
                xs[i] = (history[i].Timestamp - _plotStartMs) / 1000.0;
                ys[i] = history[i].Value;
            }
            series.Add((path, xs, ys));
        }

        if (series.Count == 0) return;

        ScottPlot.AxisLimits? preserved = null;
        if (!_chkAutoFollow.Checked)
        {
            try
            {
                preserved = _plot.Plot.Axes.GetLimits();
            }
            catch
            {
                preserved = null;
            }
        }

        _plot.Plot.Clear();
        foreach (var (path, xs, ys) in series)
        {
            var scatter = _plot.Plot.Add.Scatter(xs, ys);
            scatter.LegendText = path;
            var color = _plotColors.TryGetValue(path, out int argb)
                ? Color.FromArgb(argb)
                : ColorTranslator.FromHtml(Palette[Math.Abs(path.GetHashCode()) % Palette.Length]);
            scatter.Color = new ScottPlot.Color(color.R, color.G, color.B);
            scatter.LineWidth = 1.6f;
            scatter.MarkerSize = 0;
        }

        try
        {
            if (preserved is { } limits) _plot.Plot.Axes.SetLimits(limits);
            else _plot.Plot.Axes.AutoScale();
        }
        catch
        {
            // 坐标轴同步失败时忽略，不影响数据刷新
        }

        _plot.Refresh();
    }

    private void OpenSymbolSearch()
    {
        if (Ctx.Device.Symbols.Count == 0)
        {
            MessageBox.Show(this, "尚未加载 ELF 符号表。\r\n请先在【设置 → 设备与工程配置】中加载 .elf 文件。",
                "无符号表", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new SymbolSearchDialog(Ctx.Device.Symbols);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        Ctx.Variables.AddWatches(dialog.SelectedSymbols);
        Ctx.Audit.Add("ui", "WATCH", "/variables/watch",
            string.Join(",", dialog.SelectedSymbols.Select(s => s.FullPath)));
        RebuildGrid();
    }

    private void SelectElf()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "ELF 固件|*.elf;*.axf|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        if (!Ctx.Device.LoadElf(dialog.FileName))
        {
            MessageBox.Show(this, Ctx.Device.LastError, "加载失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // 符号表已更换，旧的监控项不再有效
        Ctx.Variables.ClearWatches();
        RebuildGrid();
    }

    private void ExportCsv()
    {
        var paths = _plotPaths.ToList();
        if (paths.Count == 0)
        {
            MessageBox.Show(this, "请先勾选变量加入波形后再导出。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Filter = "CSV 文件|*.csv",
            FileName = $"watch-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var histories = paths.ToDictionary(p => p, p => Ctx.Variables.GetHistory(p));
        int maxCount = histories.Values.Max(h => h.Count);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("index,path,timestamp_ms,value");
        for (int i = 0; i < maxCount; i++)
        {
            foreach (var (path, history) in histories)
            {
                if (i >= history.Count) continue;
                sb.AppendLine($"{i},{path},{history[i].Timestamp},{history[i].Value}");
            }
        }

        File.WriteAllText(dialog.FileName, sb.ToString());
        Ctx.Audit.Add("ui", "EXPORT", "/variables/export", $"{paths.Count} 变量 → {dialog.FileName}");
    }

    private void SafeInvoke(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _uiTimer.Stop();
            _uiTimer.Dispose();
            Ctx.Variables.ValueWritten -= OnValueWritten;
        }
        base.Dispose(disposing);
    }
}
