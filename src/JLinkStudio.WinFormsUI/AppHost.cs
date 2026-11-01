using JLinkStudio.Core.Services;
using JLinkStudio.SkillGateway;

namespace JLinkStudio.WinFormsUI;

/// <summary>界面运行模式：调试模式可读写目标，查看模式只读观察。</summary>
public enum StudioMode
{
    Debug,
    Inspect,
}

/// <summary>应用组合根：创建核心服务上下文。</summary>
public sealed class AppHost : IDisposable
{
    private StudioMode _mode = StudioMode.Debug;

    public StudioContext Context { get; } = new();

    /// <summary>进程内自宿主的 Skill 网关（REST + WebSocket + Swagger，仅监听 127.0.0.1）。</summary>
    public EmbeddedHostService Gateway { get; } = new();

    public AppHost()
    {
        // 把查看模式同步到核心上下文，Skill 网关的写接口同样受其保护
        Context.IsInspectMode = IsInspectMode;
        ModeChanged += (_, _) => Context.IsInspectMode = IsInspectMode;

        // 修复：SkillGateway 此前从未被任何入口启动，网关功能形同虚设。
        // 启动失败（端口占用等）时 StartAsync 内部已捕获并记录 LastError，不影响主界面。
        _ = Gateway.StartAsync(5080, Context);
    }

    /// <summary>当前运行模式；变更时触发 <see cref="ModeChanged"/> 供外壳同步指示。</summary>
    public StudioMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            ModeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>查看模式：禁止向目标写入变量 / 寄存器 / RTT 指令。</summary>
    public bool IsInspectMode => _mode == StudioMode.Inspect;

    public event EventHandler? ModeChanged;

    private bool _disposed;

    public void Dispose()
    {
        // OnFormClosed 与 Program.Main 的 using 会各调用一次，必须幂等
        if (_disposed) return;
        _disposed = true;
        _ = Gateway.StopAsync();
        Context.Dispose();
    }
}
