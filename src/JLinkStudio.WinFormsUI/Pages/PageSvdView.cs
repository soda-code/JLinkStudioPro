using JLinkStudio.Core.Models;
using JLinkStudio.Core.Services;
using JLinkStudio.WinFormsUI.Controls;
using JLinkStudio.WinFormsUI.Navigation;
using JLinkStudio.WinFormsUI.Theme;

namespace JLinkStudio.WinFormsUI.Pages;

/// <summary>子界面 4：寄存器视图，可通过下拉切换「通用寄存器（内核 R0-R15/xPSR/MSP/PSP）」与「硬件寄存器（SVD 外设）」。</summary>
public sealed class PageSvdView : UserControl, ISubPageView
{
    private static readonly (string Name, string Description)[] CoreRegisterNames =
    {
        ("R0", "通用数据寄存器"), ("R1", "通用数据寄存器"), ("R2", "通用数据寄存器"), ("R3", "通用数据寄存器"),
        ("R4", "通用数据寄存器"), ("R5", "通用数据寄存器"), ("R6", "通用数据寄存器"), ("R7", "通用数据寄存器"),
        ("R8", "通用数据寄存器"), ("R9", "通用数据寄存器"), ("R10", "通用数据寄存器"), ("R11", "通用数据寄存器"),
        ("R12", "通用数据寄存器"),
        ("SP", "栈指针（R13）"), ("LR", "链接寄存器（R14）"), ("PC", "程序计数器（R15）"),
        ("xPSR", "程序状态寄存器"), ("MSP", "主栈指针"), ("PSP", "进程栈指针"),
    };

    private readonly AppHost _host;
    private readonly RoundedTextBox _filter = new();
    private readonly RoundedSelect _cmbBank = new();
    // 硬件寄存器模式下的种类下拉
    private readonly RoundedSelect _cmbGroup = new();
    // 自动读取间隔
    private readonly RoundedSelect _cmbReadInterval = new();
    private SvdModel? _comboSvd;
    private bool _syncingCombo;
    private readonly DataGridView _gridRegisters = new();
    private readonly Label _lblStatus = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 1000 };

    private bool _activated;
    private bool _updating;
    private bool _coreMode = true;
    private int _readCount;
    /// <summary>连接发生在其他页面时置位，进入本页后补一次自动读取。</summary>
    private bool _pendingAutoRead;

    public PageSvdView(AppHost host)
    {
        _host = host;
        Dock = DockStyle.Fill;
        BackColor = DarkGeekTheme.PageBack;
        Padding = new Padding(16, 4, 16, 12);

        // 不显示卡片标题行，顶部留白收窄
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 10, 16, 14),
        };

        card.Controls.Add(BuildRegisterPane());
        Controls.Add(card);

        Ctx.Device.SvdChanged += (_, _) => SafeInvoke(() =>
        {
            BuildRows();
            if (!_coreMode) RefreshGroupCombo();
        });
        _refreshTimer.Tick += (_, _) => AutoRefresh();
        _gridRegisters.CellDoubleClick += OnRegisterDoubleClick;
        // 连接 / 断开目标时自动刷新寄存器数值
        Ctx.Device.StateChanged += (_, _) => SafeInvoke(OnDeviceStateChanged);

        HandleCreated += (_, _) => ApplyMode();
    }

    /// <summary>双击硬件寄存器行：弹出寄存器说明（描述 / 复位值 / 权限 / 位域）。</summary>
    private void OnRegisterDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (_coreMode || e.RowIndex < 0) return;
        if (_gridRegisters.Rows[e.RowIndex].Tag is not RegisterRef reference) return;

        try
        {
            using var dialog = new RegisterInfoDialog(reference.Peripheral, reference.Register);
            dialog.ShowDialog(this);
        }
        catch (Exception ex)
        {
            // 说明弹窗异常不应升级为未处理异常
            _lblStatus.Text = $"寄存器说明打开失败：{ex.Message}";
        }
    }

    private StudioContext Ctx => _host.Context;
    public string PageKey => "svd";
    public string PageTitle => "⚙ 寄存器";

    /// <summary>硬件寄存器行数据：所属外设 + 寄存器定义。</summary>
    private sealed record RegisterRef(SvdPeripheral Peripheral, SvdRegister Register);

    public void OnPageActivated()
    {
        _activated = true;
        if (_gridRegisters.Rows.Count == 0) ApplyMode();

        // 连接发生在其他页面时，进入本页后补一次自动读取
        if (_pendingAutoRead)
        {
            _pendingAutoRead = false;
            RefreshFromTarget();
        }

        _refreshTimer.Start();
    }

    /// <summary>连接 / 断开状态变化：连上后自动读取一次，断开后清空数值列。</summary>
    private void OnDeviceStateChanged()
    {
        if (!Ctx.Device.IsConnected)
        {
            ClearValues();
            return;
        }

        if (!_activated)
        {
            _pendingAutoRead = true;
            return;
        }

        RefreshFromTarget();
    }

    /// <summary>读取一次当前视图的数据（内核模式读全部，硬件模式只刷新当前行）。</summary>
    private void RefreshFromTarget()
    {
        try
        {
            if (_coreMode) ReadSelected();
            else AutoRefresh();
        }
        catch (Exception ex)
        {
            _lblStatus.Text = $"读取失败：{ex.Message}";
        }
    }

    /// <summary>断开后把数值列恢复为占位符。</summary>
    private void ClearValues()
    {
        _updating = true;
        try
        {
            foreach (DataGridViewRow row in _gridRegisters.Rows)
            {
                if (row.Cells["value"] is { } cell) cell.Value = "—";
            }
        }
        catch
        {
            // 表格正在重建时忽略
        }
        finally
        {
            _updating = false;
        }

        _lblStatus.Text = "目标已断开";
    }

    public void OnPageHidden()
    {
        _activated = false;
        _refreshTimer.Stop();
    }

    private Control BuildRegisterPane()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = DarkGeekTheme.CardBack };

        // ── 顶部：标题 + 寄存器类型下拉 + 操作按钮 ──
        var header = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = DarkGeekTheme.CardBack };

        var title = Ui.Text("寄存器", true, DarkGeekTheme.TextSecondary, 9f);
        title.Location = new Point(0, 12);
        header.Controls.Add(title);

        _cmbBank.AddRange("通用寄存器", "硬件寄存器");
        _cmbBank.Size = new Size(150, 32);
        _cmbBank.Location = new Point(56, 4);
        _cmbBank.SelectedIndex = 0;
        _cmbBank.SelectedIndexChanged += (_, _) => ApplyMode();
        header.Controls.Add(_cmbBank);

        // 硬件寄存器模式：种类下拉
        _cmbGroup.Size = new Size(150, 32);
        _cmbGroup.Location = new Point(214, 4);
        _cmbGroup.Visible = false;
        _cmbGroup.SelectedIndexChanged += (_, _) => OnGroupChanged();
        header.Controls.Add(_cmbGroup);

        // 自动读取间隔
        var intervalLabel = Ui.Text("读取间隔", true, DarkGeekTheme.TextSecondary, 9f);
        intervalLabel.Location = new Point(376, 12);
        header.Controls.Add(intervalLabel);

        _cmbReadInterval.Size = new Size(110, 32);
        _cmbReadInterval.Location = new Point(446, 4);
        _cmbReadInterval.AddRange(ReadIntervals.Select(i => i.Text).ToArray());
        _cmbReadInterval.SelectedIndex = 2; // 默认 1 秒
        _cmbReadInterval.SelectedIndexChanged += (_, _) => OnReadIntervalChanged();
        header.Controls.Add(_cmbReadInterval);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
        };
        var read = Ui.Button("读取", ButtonKind.Primary, (_, _) => ReadSelected(), 64);
        read.Height = 28;
        actions.Controls.Add(read);
        header.Controls.Add(actions);

        // ── 过滤行 ──
        var filterHost = new Panel
        {
            Dock = DockStyle.Top,
            Height = 40,
            BackColor = DarkGeekTheme.CardBack,
            Padding = new Padding(0, 4, 0, 4),
        };
        _filter.Dock = DockStyle.Fill;
        _filter.Height = 32;
        _filter.PlaceholderText = "过滤寄存器名…";
        _filter.TextChanged += (_, _) => BuildRows();
        filterHost.Controls.Add(_filter);

        // ── 状态栏 ──
        _lblStatus.Dock = DockStyle.Bottom;
        _lblStatus.Height = 30;
        _lblStatus.ForeColor = DarkGeekTheme.TextMuted;
        _lblStatus.Font = DarkGeekTheme.UiFontSmall;
        _lblStatus.Padding = new Padding(4, 6, 0, 0);

        var gridFrame = Ui.WrapDisplay(_gridRegisters, DarkGeekTheme.CardBack, 6);

        panel.Controls.Add(gridFrame);
        panel.Controls.Add(_lblStatus);
        panel.Controls.Add(filterHost);
        panel.Controls.Add(header);
        return panel;
    }

    /// <summary>切换寄存器类型：重建列定义并刷新数据。</summary>
    private void ApplyMode()
    {
        if (_gridRegisters.IsDisposed) return;

        try
        {
            ApplyModeCore();
        }
        catch (Exception ex)
        {
            // 视图初始化失败时降级为状态提示，避免页面切换抛出未处理异常
            _lblStatus.Text = $"寄存器视图初始化失败：{ex.Message}";
        }
    }

    private void ApplyModeCore()
    {
        _coreMode = _cmbBank.SelectedIndex <= 0;
        _cmbGroup.Visible = !_coreMode;
        _filter.PlaceholderText = _coreMode ? "过滤寄存器名（如 PC / xPSR）…" : "过滤外设 / 寄存器名…";

        _gridRegisters.Columns.Clear();
        if (_coreMode)
        {
            _gridRegisters.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "寄存器", Width = 140 });
            _gridRegisters.Columns.Add(new DataGridViewTextBoxColumn { Name = "value", HeaderText = "当前值", Width = 160 });
            _gridRegisters.Columns.Add(new DataGridViewTextBoxColumn { Name = "desc", HeaderText = "说明", Width = 300 });
        }
        else
        {
            // 外设与寄存器合并为一列：外设.寄存器（如 GPIOA.MODER）
            _gridRegisters.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "外设.寄存器", Width = 200 });
            _gridRegisters.Columns.Add(new DataGridViewTextBoxColumn { Name = "value", HeaderText = "当前值", Width = 112 });
            _gridRegisters.Columns.Add(new DataGridViewTextBoxColumn { Name = "reset", HeaderText = "复位值", Width = 112 });
            _gridRegisters.Columns.Add(new DataGridViewTextBoxColumn { Name = "access", HeaderText = "权限", Width = 110 });
            _gridRegisters.Columns.Add(new DataGridViewTextBoxColumn { Name = "desc", HeaderText = "说明", Width = 240 });
        }

        DarkGeekTheme.StyleGridExcel(_gridRegisters);
        // 寄存器列较多：保持固定列宽并允许横向滚动，避免被 Fill 模式挤压变形
        _gridRegisters.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        _gridRegisters.ScrollBars = ScrollBars.Both;

        BuildRows();

        if (!_coreMode) RefreshGroupCombo();
    }

    /// <summary>自动读取间隔选项（毫秒）。</summary>
    private static readonly (string Text, int Ms)[] ReadIntervals =
    {
        ("200 ms", 200),
        ("500 ms", 500),
        ("1 秒", 1000),
        ("2 秒", 2000),
        ("5 秒", 5000),
    };

    /// <summary>按内置手册表重建「种类」下拉（数据未变化时跳过）。</summary>
    private void RefreshGroupCombo()
    {
        var svd = Ctx.Device.Svd;
        if (ReferenceEquals(svd, _comboSvd)) return;
        _comboSvd = svd;

        _syncingCombo = true;
        try
        {
            _cmbGroup.Items.Clear();
            _cmbGroup.Items.Add("全部种类");

            foreach (string group in svd.Peripherals
                         .Select(GroupOf)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(g => g, StringComparer.OrdinalIgnoreCase))
            {
                _cmbGroup.Items.Add(group);
            }

            _cmbGroup.SelectedIndex = 0;
        }
        finally
        {
            _syncingCombo = false;
        }
    }

    private static string GroupOf(SvdPeripheral peripheral) =>
        string.IsNullOrWhiteSpace(peripheral.GroupName) ? "其他" : peripheral.GroupName!;

    /// <summary>选择种类：表格只显示该种类下的寄存器。</summary>
    private void OnGroupChanged()
    {
        if (_syncingCombo) return;

        string? group = _cmbGroup.SelectedIndex <= 0 ? null : _cmbGroup.SelectedItem;
        _filter.Text = group ?? string.Empty;
    }

    /// <summary>切换自动读取间隔。</summary>
    private void OnReadIntervalChanged()
    {
        int index = Math.Clamp(_cmbReadInterval.SelectedIndex, 0, ReadIntervals.Length - 1);
        _refreshTimer.Interval = ReadIntervals[index].Ms;
        _lblStatus.Text = $"自动读取间隔：{ReadIntervals[index].Text}";
    }

    private void BuildRows()
    {
        if (_gridRegisters.IsDisposed) return;

        if (_coreMode) BuildCoreRows();
        else BuildHardwareRows();
    }

    private void BuildCoreRows()
    {
        _updating = true;
        try
        {
            string keyword = _filter.Text.Trim();
            _gridRegisters.Rows.Clear();

            foreach (var (name, description) in CoreRegisterNames)
            {
                if (keyword.Length > 0 && !name.Contains(keyword, StringComparison.OrdinalIgnoreCase)) continue;
                int index = _gridRegisters.Rows.Add(name, "—", description);
                _gridRegisters.Rows[index].Tag = name;
            }

            _lblStatus.Text = $"内核寄存器 {CoreRegisterNames.Length} 个 · 当前显示 {_gridRegisters.Rows.Count} 个";

            if (_gridRegisters.Rows.Count > 0)
                _gridRegisters.CurrentCell = _gridRegisters.Rows[0].Cells["name"];
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>按内置手册表生成硬件寄存器总表（可按种类 / 外设 / 寄存器名过滤）。</summary>
    private void BuildHardwareRows()
    {
        _updating = true;
        try
        {
            string keyword = _filter.Text.Trim();
            _gridRegisters.Rows.Clear();
            _readCount = 0;

            var svd = Ctx.Device.Svd;

            int total = 0;
            foreach (var peripheral in svd.Peripherals)
            {
                foreach (var register in peripheral.Registers)
                {
                    total++;
                    // 关键字同时匹配 外设名 / 种类(groupName) / 寄存器名，便于按种类搜索
                    if (keyword.Length > 0 &&
                        !peripheral.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
                        !GroupOf(peripheral).Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
                        !register.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string name = $"{peripheral.Name}.{register.Name}";
                    if (register.HasReadSideEffect) name = "⚠ " + name;

                    int index = _gridRegisters.Rows.Add(
                        name,
                        "—",
                        $"0x{register.ResetValue:X8}",
                        register.Access,
                        register.Description ?? string.Empty);

                    _gridRegisters.Rows[index].Tag = new RegisterRef(peripheral, register);
                }
            }

            _lblStatus.Text = $"{svd.Peripherals.Count} 个外设 · 共 {total} 条寄存器 · 当前显示 {_gridRegisters.Rows.Count} 条";

            if (_gridRegisters.Rows.Count > 0)
                _gridRegisters.CurrentCell = _gridRegisters.Rows[0].Cells["name"];
        }
        finally
        {
            _updating = false;
        }
    }

    private void ReadSelected()
    {
        // 目标断开 / 驱动异常时只更新状态栏，不抛未处理异常
        try
        {
            if (_coreMode) ReadCoreRegisters();
            else ReadHardwareVisible();
        }
        catch (Exception ex)
        {
            _lblStatus.Text = $"读取失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 读取内核寄存器（R0-R15 / xPSR / MSP / PSP）。
    /// CPU 运行中读取会被 J-Link 拒绝并弹出 DLL 错误框，因此：
    /// 自动刷新只在已暂停时读取；用户手动读取可临时暂停并在读完后恢复运行。
    /// </summary>
    private void ReadCoreRegisters(bool allowHalt = true)
    {
        var link = Ctx.Device.Link;
        if (link == null || !link.IsConnected)
        {
            _lblStatus.Text = "目标未连接：无法读取内核寄存器";
            return;
        }

        bool pausedByUs = false;
        if (!link.IsHalted)
        {
            if (!allowHalt || _host.IsInspectMode)
            {
                _lblStatus.Text = "CPU 正在运行：请先「暂停 CPU」再读取内核寄存器";
                return;
            }

            if (!link.Halt(out var haltError))
            {
                _lblStatus.Text = $"暂停 CPU 失败：{haltError}";
                return;
            }
            pausedByUs = true;
        }

        try
        {
            var values = Ctx.Registers.ReadCoreRegisters();
            if (values.Count == 0)
            {
                _lblStatus.Text = "目标未连接：无法读取内核寄存器";
                return;
            }

            int read = 0;
            _updating = true;
            try
            {
                foreach (DataGridViewRow row in _gridRegisters.Rows)
                {
                    if (row.Tag is not string name) continue;
                    if (!values.TryGetValue(name, out var hex)) continue;
                    row.Cells["value"].Value = hex;
                    read++;
                }
            }
            finally
            {
                _updating = false;
            }

            _lblStatus.Text = pausedByUs
                ? $"已读取 {read} 个内核寄存器（读取时临时暂停 CPU，现已恢复运行）"
                : $"已读取 {read} 个内核寄存器";
        }
        finally
        {
            if (pausedByUs) link.Go(out _);
        }
    }

    /// <summary>一次性读取整页所有可见行的硬件寄存器值（跳过读清零等有副作用的寄存器）。</summary>
    private void ReadHardwareVisible()
    {
        if (Ctx.Device.Svd == null) return;

        _readCount = 0;
        _updating = true;
        _gridRegisters.SuspendLayout();
        try
        {
            foreach (DataGridViewRow row in _gridRegisters.Rows)
            {
                if (!row.Visible || row.Tag is not RegisterRef reference) continue;
                if (reference.Register.HasReadSideEffect)
                {
                    row.Cells["value"].Value = "跳过(读清零)";
                    row.Cells["value"].Style.ForeColor = DarkGeekTheme.Warning;
                    continue;
                }

                var result = Ctx.Registers.ReadRegister(reference.Peripheral.Name, reference.Register.Name);
                if (result == null) continue;

                row.Cells["value"].Value = result.ValueHex;
                _readCount++;
            }
        }
        finally
        {
            _gridRegisters.ResumeLayout();
            _updating = false;
        }

        _lblStatus.Text = $"已读取 {_readCount} 条寄存器 · 显示 {_gridRegisters.Rows.Count} 条";
    }

    /// <summary>按当前读取间隔刷新：内核模式刷新全部，硬件模式读取整页数据。</summary>
    private void AutoRefresh()
    {
        if (!_activated || _updating) return;

        // 未连接时不做任何读取，避免状态栏被反复覆盖成「目标未连接」
        if (!Ctx.Device.IsConnected) return;

        try
        {
            if (_coreMode)
            {
                // 自动刷新不主动暂停 CPU：仅在已暂停时读取，避免每秒打断运行
                ReadCoreRegisters(allowHalt: false);
                return;
            }

            // 硬件寄存器：一次读取整页（所有可见行）
            ReadHardwareVisible();
        }
        catch
        {
            // 单次自动刷新失败（目标复位 / 断开等）不应中断定时器或抛到 UI 线程
        }
    }

    private void SafeInvoke(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _refreshTimer.Dispose();
        base.Dispose(disposing);
    }
}
