using System.Collections.Concurrent;
using JLinkStudio.Core.Models;

namespace JLinkStudio.Core.Services;

/// <summary>「运行日志」中的一条会话记录。</summary>
public sealed record SessionLogEntry(long TimestampMs, string Text);

/// <summary>
/// 应用服务上下文（组合根）：把设备、RTT、变量、寄存器、Skill 安全策略聚合为单一入口，
/// UI 与 Skill 网关共享同一组单例，实现“零内存拷贝”的跨层访问。
/// </summary>
public sealed class StudioContext : IDisposable
{
    public StudioContext()
    {
        Device = new DeviceManager();
        Rtt = new RttEngine(Device);
        Variables = new VariablePollEngine(Device);
        Registers = new RegisterAccessService(Device);
        Audit = new SkillAuditLog();
        Security = new SkillSecurityPolicy();
        Keil = new KeilToolchain();
    }

    public DeviceManager Device { get; }
    public RttEngine Rtt { get; }
    public VariablePollEngine Variables { get; }
    public RegisterAccessService Registers { get; }
    public SkillAuditLog Audit { get; }
    public SkillSecurityPolicy Security { get; }

    /// <summary>Keil 工具链（编译 / 下载），安装目录默认 D:\Keil_V5。</summary>
    public KeilToolchain Keil { get; }

    /// <summary>
    /// 查看模式（只读观察）：禁止一切对目标板的写操作。
    /// UI 与 Skill 网关共享同一开关，保证「查看模式禁止写目标」对两条通道同时生效。
    /// </summary>
    public volatile bool IsInspectMode;

    public event EventHandler<string>? SessionLog;

    /// <summary>会话日志保留条数上限（外部接口按需取最近的 N 条）。</summary>
    private const int MaxSessionLogEntries = 2000;

    private readonly ConcurrentQueue<SessionLogEntry> _sessionLogs = new();

    /// <summary>
    /// 向「运行日志」推一条会话记录（界面各页面与 Skill 网关共用）。
    /// 事件只能由声明方触发，因此这里提供统一出口；
    /// 同时写入环形缓冲，供 <c>GET /api/v1/session/logs</c> 外部读取。
    /// </summary>
    public void LogSession(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        _sessionLogs.Enqueue(new SessionLogEntry(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), message));
        while (_sessionLogs.Count > MaxSessionLogEntries) _sessionLogs.TryDequeue(out _);

        SessionLog?.Invoke(this, message);
    }

    /// <summary>取最近的会话日志（按时间正序返回，最多 <paramref name="take"/> 条）。</summary>
    public IReadOnlyList<SessionLogEntry> RecentSessionLogs(int take, string? keyword = null)
    {
        take = Math.Clamp(take, 1, MaxSessionLogEntries);

        IEnumerable<SessionLogEntry> query = _sessionLogs;
        if (!string.IsNullOrWhiteSpace(keyword))
            query = query.Where(e => e.Text.Contains(keyword!, StringComparison.OrdinalIgnoreCase));

        return query.Reverse().Take(take).Reverse().ToList();
    }

    /// <summary>
    /// 界面提示回调：由主窗口注册，用于在必须人工介入时弹框。
    /// 未注册（无界面 / 界面未就绪）时 <see cref="PromptAsync"/> 返回 Delivered=false。
    /// </summary>
    public Func<UiPromptRequest, Task<UiPromptResult>>? PromptHandler { get; set; }

    /// <summary>请求界面弹框；返回是否显示、用户是否确认、以及选中的选项。</summary>
    public async Task<UiPromptResult> PromptAsync(UiPromptRequest request)
    {
        var handler = PromptHandler;
        if (handler == null) return UiPromptResult.NotDelivered;

        try
        {
            var result = await handler(request).ConfigureAwait(false);
            return new UiPromptResult
            {
                Delivered = true,
                Confirmed = result.Confirmed,
                Selected = result.Selected,
            };
        }
        catch (Exception ex)
        {
            LogSession($"界面提示失败：{ex.Message}");
            return UiPromptResult.NotDelivered;
        }
    }

    /// <summary>连接 J-Link 硬件并启动 RTT 采集与变量轮询。</summary>
    public async Task<bool> StartSessionAsync(DeviceSessionConfig config)
    {
        StopSession();

        bool ok = await Device.ConnectAsync(config);
        if (!ok)
        {
            LogSession(Device.LastError);
            return false;
        }

        if (!Rtt.Start(out var rttError))
            LogSession($"RTT 未启动：{rttError}");
        else
            LogSession("RTT 采集已启动。");

        Variables.Start();
        return true;
    }

    public void StopSession()
    {
        try
        {
            Variables.Stop();
            Rtt.Stop();
            Device.Disconnect();
        }
        catch (Exception ex)
        {
            LogSession($"断开异常：{ex.Message}");
        }
    }

    public object BuildStatus() => new
    {
        connected = Device.IsConnected,
        state = Device.State.ToString(),
        device = Device.Config.Device,
        interfaceType = Device.Config.Interface.ToString(),
        speedKhz = Device.Config.SpeedKhz,
        serialNumber = Device.Config.SerialNumber,
        elfPath = Device.Config.ElfPath,
        svdPath = Device.Config.SvdPath,
        elfLoaded = Device.Symbols.Count > 0,
        symbolCount = Device.Symbols.Count,
        svdLoaded = Device.Svd != null,
        peripheralCount = Device.Svd?.Peripherals.Count ?? 0,
        jlinkDriver = DeviceManager.IsDriverAvailable ? DeviceManager.DriverDescription : null,
        rttRunning = Rtt.IsRunning,
        rttControlBlock = Device.Config.RttControlBlockAddress is uint addr ? $"0x{addr:X8}" : null,
        rttLines = Rtt.TotalLines,
        watchedVariables = Variables.ActiveLeafCount,
        sampleRateHz = Variables.SampleRateHz,
        writeProtected = !Security.WriteEnabled,
        lastError = string.IsNullOrEmpty(Device.LastError) ? null : Device.LastError,
        diagnosis = Device.LastDiagnosis?.Summary,
        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    };

    public void Dispose()
    {
        StopSession();
        Variables.Dispose();
        Rtt.Dispose();
        Device.Dispose();
    }
}
