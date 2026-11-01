using JLinkStudio.Core.Data;
using JLinkStudio.Core.Interop;
using JLinkStudio.Core.Models;
using JLinkStudio.Core.Parsers;

namespace JLinkStudio.Core.Services;

/// <summary>设备连接管理器：统一管理 J-Link 会话、符号表、寄存器参考数据与工程文件热加载。</summary>
public sealed class DeviceManager : IDisposable
{
    private readonly object _stateLock = new();
    private readonly ElfParser _elfParser = new();
    private IJLinkLink? _link;
    private FileSystemWatcher? _elfWatcher;
    private System.Threading.Timer? _reloadTimer;
    private bool _disposed;

    public DeviceSessionConfig Config { get; private set; } = new();
    public SymbolIndex Symbols { get; } = new();
    private readonly object _svdLock = new();
    private SvdModel? _svd;
    private string _svdDevice = string.Empty;

    /// <summary>寄存器参考数据：按当前目标芯片型号返回对应的内置手册表（STM32/GD32/NXP 等）。</summary>
    public SvdModel Svd
    {
        get
        {
            string device = Config.Device ?? string.Empty;
            lock (_svdLock)
            {
                if (_svd == null || !string.Equals(_svdDevice, device, StringComparison.OrdinalIgnoreCase))
                {
                    _svd = BuiltInRegisterDatabase.ForDevice(device);
                    _svdDevice = device;
                }
                return _svd;
            }
        }
    }
    public ElfParser.ParseResult? ElfInfo { get; private set; }
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string LastError { get; private set; } = string.Empty;
    /// <summary>最近一次连接失败的结构化诊断（成功连接后清空）。</summary>
    public ConnectionDiagnosis? LastDiagnosis { get; private set; }
    public bool IsConnected => State == ConnectionState.Connected;
    public string LinkDescription => _link?.Description ?? "未连接";

    /// <summary>检测到固件更新时是否自动重载符号表（对应界面「自动重载 ELF」开关）。</summary>
    public bool AutoReloadElf { get; set; } = true;

    /// <summary>J-Link SDK 加载信息（版本 + 路径），供界面展示。</summary>
    public static string DriverDescription => JLinkWrapper.DriverDescription;
    public static bool IsDriverAvailable => JLinkWrapper.IsDriverAvailable;
    public static string? DriverVersion => JLinkLibraryLocator.VersionLabel;
    public static string? DriverPath => JLinkLibraryLocator.DllPath;
    public IJLinkLink? Link => _link;

    public event EventHandler? StateChanged;
    public event EventHandler? SymbolsChanged;
    public event EventHandler? SvdChanged;
    public event EventHandler<string>? Log;
    public event EventHandler<string>? ElfReloaded;

    /// <summary>
    /// 只落地会话配置、不连接硬件（供 Skill 网关在 autoConnect=false 时使用，
    /// 否则外部设置的型号 / 速率等参数会被静默丢弃）。
    /// </summary>
    public void ApplyConfig(DeviceSessionConfig config)
    {
        bool deviceChanged = !string.Equals(Config.Device, config.Device, StringComparison.Ordinal);
        Config = config.Clone();
        // 型号变化会影响内置寄存器手册数据，通知界面重建寄存器表
        if (deviceChanged) SvdChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> ConnectAsync(DeviceSessionConfig config)
    {
        Config = config.Clone();
        // 目标芯片可能变化：通知界面重建寄存器表（按芯片切换内置手册数据）
        SvdChanged?.Invoke(this, EventArgs.Empty);
        SetState(ConnectionState.Connecting, "正在连接目标…");

        return await Task.Run(() =>
        {
            try
            {
                lock (_stateLock)
                {
                    _link?.Dispose();
                    _link = null;
                }

                // 1. 先加载工程文件（ELF 提供 RTT 控制块地址与符号表）
                if (!string.IsNullOrWhiteSpace(Config.ElfPath) && File.Exists(Config.ElfPath))
                    LoadElfInternal(Config.ElfPath);
                // 寄存器数据来自内置手册表，无需加载外部 SVD 文件

                // 2. 建立硬件链路（仅支持真实 J-Link）
                if (!JLinkWrapper.IsDriverAvailable)
                {
                    var missing = "未找到 J-Link 驱动（JLink_x64.dll），请先在「设置 → 驱动与关于」中指定安装目录。";
                    LastError = missing;
                    LastDiagnosis = ConnectionDiagnostics.Analyze(Config, missing, 0, driverAvailable: false);
                    LogDiagnosis(LastDiagnosis);
                    SetState(ConnectionState.Faulted, missing);
                    return false;
                }

                var link = new JLinkWrapper();
                Log?.Invoke(this, $"J-Link 驱动：{JLinkWrapper.DriverDescription}");

                if (!link.Open(Config.SerialNumber, Config.Device, Config.Interface, Config.SpeedKhz, out var error))
                {
                    int emulatorCount = 0;
                    try { emulatorCount = link.EnumerateEmulators().Length; } catch { /* 枚举失败不影响诊断 */ }
                    link.Dispose();

                    LastError = error;
                    LastDiagnosis = ConnectionDiagnostics.Analyze(Config, error, emulatorCount, driverAvailable: true);
                    LogDiagnosis(LastDiagnosis);
                    SetState(ConnectionState.Faulted, error);
                    return false;
                }

                lock (_stateLock) _link = link;

                if (Config.RttControlBlockAddress == null && ElfInfo?.RttControlBlockAddress is uint rttAddr)
                    Config.RttControlBlockAddress = rttAddr;

                SetupElfWatcher();

                LastError = string.Empty;
                LastDiagnosis = null;
                SetState(ConnectionState.Connected, $"已连接 · {link.Description}");
                Log?.Invoke(this, $"设备连接成功：{link.Description}");
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                SetState(ConnectionState.Faulted, $"连接失败: {ex.Message}");
                return false;
            }
        });
    }

    public void Disconnect()
    {
        lock (_stateLock)
        {
            _link?.Dispose();
            _link = null;
        }
        StopElfWatcher();
        SetState(ConnectionState.Disconnected, "已断开");
    }

    public bool LoadElf(string path)
    {
        try
        {
            LoadElfInternal(path);
            Config.ElfPath = path;
            SetupElfWatcher();
            Log?.Invoke(this, $"ELF 已加载：{Path.GetFileName(path)}，符号 {Symbols.Count} 个" +
                              (ElfInfo?.DwarfLoaded == true ? "（DWARF 结构体已解析）" : "（仅符号表）"));
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log?.Invoke(this, $"ELF 解析失败：{ex.Message}");
            return false;
        }
    }

    private void LoadElfInternal(string path)
    {
        var result = _elfParser.Parse(path);
        ElfInfo = result;
        Symbols.Load(result.Symbols, path);

        // 仅在「从 ELF 自动」模式下跟随 ELF 更新控制块地址；
        // 用户手动指定的模式与地址不能被覆盖，否则重连后设置会被静默改回自动。
        if (result.RttControlBlockAddress is uint addr && Config.RttSearchMode == RttSearchMode.AutoFromElf)
            Config.RttControlBlockAddress = addr;

        if (!string.IsNullOrEmpty(result.Warning))
            Log?.Invoke(this, result.Warning!);

        SymbolsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 兼容旧接口：寄存器数据已改为内置手册表（RM0090 常用外设），
    /// 调用此方法不再加载外部 .svd 文件。
    /// </summary>
    public bool LoadSvd(string path)
    {
        Log?.Invoke(this, $"寄存器数据已使用内置手册表（{Svd.Peripherals.Count} 个外设），无需加载 {Path.GetFileName(path)}。");
        return true;
    }

    /// <summary>
    /// 自动识别目标芯片型号：已连接时直接读 ID 寄存器；
    /// 未连接时用通用内核（Cortex-M7/M4/M33/M3/M0+）建立临时会话探测，探测完立即断开。
    /// </summary>
    public async Task<DeviceDetection?> DetectTargetAsync()
    {
        return await Task.Run(() =>
        {
            IJLinkLink? temporary = null;
            try
            {
                if (!JLinkWrapper.IsDriverAvailable)
                {
                    Log?.Invoke(this, "自动识别失败：未找到 J-Link 驱动（JLink_x64.dll）。");
                    return null;
                }

                IJLinkLink link;
                if (_link is { IsConnected: true })
                {
                    link = _link;
                }
                else
                {
                    var candidate = new JLinkWrapper();
                    int speed = Math.Clamp(Config.SpeedKhz <= 0 ? 1000 : Config.SpeedKhz, 5, 1000);
                    bool opened = false;
                    foreach (var generic in new[] { "Cortex-M7", "Cortex-M4", "Cortex-M33", "Cortex-M3", "Cortex-M0+" })
                    {
                        if (candidate.Open(Config.SerialNumber, generic, Config.Interface, speed, out _))
                        {
                            opened = true;
                            break;
                        }
                    }

                    if (!opened)
                    {
                        candidate.Dispose();
                        Log?.Invoke(this, "自动识别失败：无法建立通用内核会话，请检查目标供电、SWD 接线与连接速率。");
                        return null;
                    }

                    temporary = candidate;
                    link = candidate;
                }

                var detection = TargetDeviceDetector.Detect(link);
                Log?.Invoke(this, $"自动识别目标：{detection.Detail}");
                return detection;
            }
            catch (Exception ex)
            {
                Log?.Invoke(this, $"自动识别异常：{ex.Message}");
                return null;
            }
            finally
            {
                temporary?.Dispose();
            }
        });
    }

    private void LogDiagnosis(ConnectionDiagnosis diagnosis)
    {
        foreach (var line in diagnosis.ToLogLines()) Log?.Invoke(this, line);
    }

    public bool Reset(out string error)
    {
        var link = _link;
        if (link == null || !link.IsConnected) { error = "目标未连接。"; return false; }
        return link.Reset(out error);
    }

    public bool Halt(out string error)
    {
        var link = _link;
        if (link == null || !link.IsConnected) { error = "目标未连接。"; return false; }
        return link.Halt(out error);
    }

    public bool Go(out string error)
    {
        var link = _link;
        if (link == null || !link.IsConnected) { error = "目标未连接。"; return false; }
        return link.Go(out error);
    }

    private void SetState(ConnectionState state, string message)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
        Log?.Invoke(this, message);
    }

    private void SetupElfWatcher()
    {
        StopElfWatcher();
        if (string.IsNullOrWhiteSpace(Config.ElfPath)) return;

        var directory = Path.GetDirectoryName(Config.ElfPath);
        var fileName = Path.GetFileName(Config.ElfPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;

        try
        {
            _elfWatcher = new FileSystemWatcher(directory, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _elfWatcher.Changed += (_, _) => ScheduleReload();
            _elfWatcher.Created += (_, _) => ScheduleReload();
        }
        catch
        {
            _elfWatcher = null;
        }
    }

    private void ScheduleReload()
    {
        if (!AutoReloadElf) return; // 用户关闭了自动重载
        _reloadTimer?.Dispose();
        _reloadTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                if (Config.ElfPath != null && File.Exists(Config.ElfPath))
                {
                    LoadElfInternal(Config.ElfPath);
                    ElfReloaded?.Invoke(this, Config.ElfPath);
                    Log?.Invoke(this, "检测到固件更新，符号表已自动重载。");
                }
            }
            catch
            {
                // 文件可能仍被编译器占用，下次变更再重试
            }
        }, null, 800, Timeout.Infinite);
    }

    private void StopElfWatcher()
    {
        _elfWatcher?.Dispose();
        _elfWatcher = null;
        _reloadTimer?.Dispose();
        _reloadTimer = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopElfWatcher();
        lock (_stateLock)
        {
            _link?.Dispose();
            _link = null;
        }
    }
}
