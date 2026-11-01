using JLinkStudio.WinFormsUI.Theme;

namespace JLinkStudio.WinFormsUI.Controls;

/// <summary>
/// 「目标型号」选择器：点击后弹出常见 MCU 列表，列出厂商 / 型号 / RAM / Flash，
/// 支持关键字过滤，双击或回车即可回填到输入框。
/// </summary>
internal static class DevicePicker
{
    internal sealed record Entry(string Vendor, string Device, int? RamKb, int? FlashKb)
    {
        public string RamText => RamKb is int ram ? $"{ram} KB" : "—";

        public string FlashText => FlashKb is int flash ? $"{flash} KB" : "外部";
    }

    private static readonly Entry[] Catalog =
    {
        new("ST", "STM32F030F4", 4, 16),
        new("ST", "STM32F030C8", 8, 64),
        new("ST", "STM32F103C8", 20, 64),
        new("ST", "STM32F103RC", 48, 256),
        new("ST", "STM32F103ZE", 64, 512),
        new("ST", "STM32F407VG", 192, 1024),
        new("ST", "STM32F429ZI", 256, 2048),
        new("ST", "STM32L431RC", 64, 256),
        new("ST", "STM32G071RB", 36, 128),
        new("ST", "STM32G431CB", 32, 128),
        new("ST", "STM32H743VI", 1024, 2048),
        new("ST", "STM32H750VB", 1024, 128),

        new("GigaDevice", "GD32F103C8", 20, 64),
        new("GigaDevice", "GD32F103CB", 20, 128),
        new("GigaDevice", "GD32F103RC", 48, 256),
        new("GigaDevice", "GD32F103VC", 48, 256),
        new("GigaDevice", "GD32F103ZE", 64, 512),
        new("GigaDevice", "GD32F303CC", 48, 256),
        new("GigaDevice", "GD32F303RC", 48, 256),
        new("GigaDevice", "GD32F303VC", 48, 256),
        new("GigaDevice", "GD32F303ZK", 64, 512),
        new("GigaDevice", "GD32F407VE", 192, 512),
        new("GigaDevice", "GD32F407ZK", 192, 1024),
        new("GigaDevice", "GD32F450ZK", 512, 3072),
        new("GigaDevice", "GD32F450IK", 512, 3072),
        new("GigaDevice", "GD32E230C8", 8, 64),
        new("GigaDevice", "GD32E230K8", 8, 64),
        new("GigaDevice", "GD32L233CC", 32, 256),

        new("NXP", "LPC1768", 64, 512),
        new("NXP", "LPC1766", 64, 256),
        new("NXP", "LPC1758", 64, 512),
        new("NXP", "LPC1114", 8, 32),
        new("NXP", "LPC54114", 192, null),
        new("NXP", "MIMXRT1062XXX6A", 1024, null),
        new("NXP", "MIMXRT1052DVL6B", 512, null),
        new("NXP", "MIMXRT1021DAG5A", 256, null),
        new("NXP", "MK60DN512", 128, 512),
        new("NXP", "MK66FX1M0", 256, 1024),

        new("Nordic", "NRF51822_XXAA", 16, 256),
        new("Nordic", "NRF52832_XXAA", 64, 512),
        new("Nordic", "NRF52833_XXAA", 128, 512),
        new("Nordic", "NRF52840_XXAA", 256, 1024),

        new("Espressif", "ESP32-S3FN8", 512, null),
        new("Espressif", "ESP32-D0WD", 520, null),

        new("Raspberry Pi", "RP2040_M0_0", 264, null),

        new("Microchip", "ATSAMD21G18", 32, 256),
        new("Microchip", "ATSAMD51J19", 192, 512),

        new("TI", "CC2640R2F", 20, 128),
        new("TI", "MSP432P401R", 64, 256),

        new("Infineon", "XMC4700-F144x2048", 352, 2048),

        new("Renesas", "R7FA4M1AB", 32, 256),
    };

    /// <summary>型号名列表（供连接配置的下拉框直接选择）。</summary>
    public static IReadOnlyList<string> DeviceNames { get; } = Catalog.Select(c => c.Device).ToArray();

    private const int PickerWidth = 618;
    private const int PickerHeight = 620;
    private const int FooterHeight = 60;
    private const int HeaderHeight = 56;

    /// <summary>弹窗底板：比页面底色再深一档（slate-100 与 slate-200 之间），与白色表格形成层次。</summary>
    private static Color Surface => DarkGeekTheme.Blend(DarkGeekTheme.PageBack, DarkGeekTheme.Border, 0.55);

    /// <summary>弹出型号选择窗口：圆角浮层居中显示，确定后回填到输入框。</summary>
    public static void Show(Control anchor, Action<string> onPicked)
    {
        var hostForm = anchor.FindForm();
        var picker = new PickerForm { ClientSize = new Size(PickerWidth, PickerHeight) };

        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = FooterHeight,
            BackColor = Surface,
            Padding = new Padding(0),
        };

        var body = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Surface,
            Padding = new Padding(12, 12, 12, 0),
        };

        var filter = new RoundedTextBox(string.Empty, 500) { Dock = DockStyle.Top, Height = 42 };
        filter.PlaceholderText = "搜索厂商 / 型号";
        filter.Font = DarkGeekTheme.CreateFontPublic(11f, FontStyle.Regular);

        var grid = BuildGrid();
        var gridHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 12, 0, 0) };
        gridHost.Controls.Add(grid);

        // 先加 Fill 再加 Top，停靠才会给筛选框让出空间
        body.Controls.Add(gridHost);
        body.Controls.Add(filter);

        var hint = new Label
        {
            Text = "双击行或按 Enter 快速选择",
            AutoSize = false,
            Location = new Point(12, 10),
            Size = new Size(380, 38),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = DarkGeekTheme.TextMuted,
            BackColor = Color.Transparent,
            Font = DarkGeekTheme.UiFontSmall,
        };

        var btnOk = Ui.Button("确定", ButtonKind.Primary, null, 120);
        btnOk.Location = new Point(PickerWidth - 12 - 120, 10);
        btnOk.Size = new Size(120, 38);

        footer.Controls.Add(hint);
        footer.Controls.Add(btnOk);

        var header = BuildHeader();
        var close = new FlatButton
        {
            Text = "✕",
            Kind = ButtonKind.Ghost,
            Bordered = false,
            ForeColor = DarkGeekTheme.TextSecondary,
            Location = new Point(PickerWidth - 12 - 32, 12),
            Size = new Size(32, 32),
        };
        close.Click += (_, _) => picker.Close();
        header.Controls.Add(close);

        // 先加 Fill / Bottom，最后加 Top，停靠顺序才正确
        picker.Controls.Add(body);
        picker.Controls.Add(footer);
        picker.Controls.Add(header);

        void Refresh()
        {
            string keyword = filter.Text.Trim();
            grid.Rows.Clear();
            foreach (var entry in Catalog)
            {
                if (keyword.Length > 0 &&
                    !entry.Vendor.Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
                    !entry.Device.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    continue;

                int index = grid.Rows.Add(entry.Vendor, entry.Device, entry.RamText, entry.FlashText);
                grid.Rows[index].Tag = entry;
            }

            if (grid.Rows.Count > 0) grid.CurrentCell = grid.Rows[0].Cells[1];
        }

        void Pick()
        {
            if (grid.CurrentRow?.Tag is not Entry entry) return;
            onPicked(entry.Device);
            picker.Close();
        }

        filter.TextChanged += (_, _) => Refresh();
        filter.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Down) { grid.Focus(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter) Pick();
        };
        grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) Pick(); };
        grid.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) Pick(); };
        btnOk.Click += (_, _) => Pick();
        picker.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) picker.Close();
        };
        // 点击弹窗外任意位置自动收起
        picker.Deactivate += (_, _) => picker.Close();

        Refresh();

        // 居中显示在主窗口（找不到主窗口时退回屏幕工作区中心）
        var target = hostForm?.Owner ?? hostForm;
        Rectangle area = target is { IsDisposed: false }
            ? target.RectangleToScreen(target.ClientRectangle)
            : Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);

        picker.Location = new Point(
            area.Left + Math.Max(0, (area.Width - picker.Width) / 2),
            area.Top + Math.Max(0, (area.Height - picker.Height) / 2));

        if (hostForm is { IsDisposed: false }) picker.Show(hostForm);
        else picker.Show();

        filter.FocusInput();
    }

    /// <summary>无边框圆角浮层：自绘 1px 描边并裁圆角。</summary>
    private sealed class PickerForm : Form
    {
        private const int Radius = 10;
        private const int CS_DROPSHADOW = 0x00020000;

        public PickerForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Surface;
            KeyPreview = true;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= CS_DROPSHADOW;
                return cp;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width <= 0 || Height <= 0) return;
            using var path = DarkGeekTheme.RoundedRect(new Rectangle(0, 0, Width, Height), Radius);
            var previous = Region;
            Region = new Region(path);
            previous?.Dispose();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            // 加深后的底板 + 稍深的描边，和主界面的白色卡片区分开
            using (var brush = new SolidBrush(Surface))
                g.FillRectangle(brush, ClientRectangle);

            using var pen = new Pen(DarkGeekTheme.Blend(DarkGeekTheme.Border, DarkGeekTheme.TextMuted, 0.35));
            using var path = DarkGeekTheme.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), Radius);
            g.DrawPath(pen, path);
        }
    }

    /// <summary>弹窗标题栏：图标 + 标题 + 型号库概况。</summary>
    private static Panel BuildHeader()
    {
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = HeaderHeight,
            BackColor = DarkGeekTheme.CardBack,
        };

        var icon = new Label
        {
            Text = "🔌",
            AutoSize = false,
            Location = new Point(10, 8),
            Size = new Size(38, 40),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = DarkGeekTheme.CreateFontPublic(15f, FontStyle.Regular),
            BackColor = Color.Transparent,
        };

        var title = new Label
        {
            Text = "选择目标芯片",
            AutoSize = true,
            Location = new Point(52, 9),
            ForeColor = DarkGeekTheme.TextPrimary,
            BackColor = Color.Transparent,
            Font = DarkGeekTheme.CreateFontPublic(11f, FontStyle.Bold),
        };

        var vendors = Catalog.Select(c => c.Vendor).Distinct().Count();
        var subtitle = new Label
        {
            Text = $"共 {Catalog.Length} 款 · {vendors} 个厂商 · 型号即 J-Link 设备名",
            AutoSize = true,
            Location = new Point(52, 31),
            ForeColor = DarkGeekTheme.TextMuted,
            BackColor = Color.Transparent,
            Font = DarkGeekTheme.UiFontSmall,
        };

        var line = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = DarkGeekTheme.Border };

        header.Controls.AddRange(new Control[] { icon, title, subtitle, line });
        return header;
    }

    private static DataGridView BuildGrid()
    {
        var grid = new DataGridView { Dock = DockStyle.Fill };
        DarkGeekTheme.StyleGridExcel(grid, readOnly: true);
        // 厂商列用英文并收窄，把空间让给型号列
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "vendor", HeaderText = "厂商", Width = 108, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "device", HeaderText = "型号", Width = 236, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ram", HeaderText = "RAM", Width = 96, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "flash", HeaderText = "Flash", Width = 104, ReadOnly = true });
        grid.RowTemplate.Height = 34;
        // 整体放大 1.5 倍：表格字体同步放大，避免行高变大而文字偏小
        grid.Font = DarkGeekTheme.CreateFontPublic(11f, FontStyle.Regular);
        grid.DefaultCellStyle.Font = DarkGeekTheme.CreateFontPublic(11f, FontStyle.Regular);
        // 内容与表头统一居中
        grid.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        grid.ColumnHeadersDefaultCellStyle.Font = DarkGeekTheme.CreateFontPublic(10.5f, FontStyle.Bold);
        grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        grid.ColumnHeadersHeight = 40;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToResizeRows = false;
        grid.MultiSelect = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.ScrollBars = ScrollBars.Vertical;
        return grid;
    }
}
