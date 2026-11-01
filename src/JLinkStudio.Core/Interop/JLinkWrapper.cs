using System.Text;
using JLinkStudio.Core.Models;

namespace JLinkStudio.Core.Interop;

/// <summary>
/// JLinkARM.dll 线程安全封装。
/// J-Link SDK 内部并非完全线程安全，因此所有硬件访问统一经由 <see cref="_busLock"/> 串行化，
/// 保证 RTT 采集、变量轮询、寄存器读写与 Skill 下发写操作互不冲突。
/// </summary>
public sealed class JLinkWrapper : IJLinkLink
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _busLock = new(1, 1);
    private readonly object _stateLock = new();

    private bool _disposed;
    private int _speedKhz;
    private long _bytesRead;
    private long _bytesWritten;

    /// <summary>进入硬件总线临界区；信号量已释放（Dispose 竞态）时返回 false 而非抛异常。</summary>
    private bool TryEnterBus(TimeSpan timeout)
    {
        try
        {
            return _busLock.Wait(timeout);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public bool IsConnected { get; private set; }
    public string Description { get; private set; } = "J-Link (未连接)";
    public string TargetDevice { get; private set; } = string.Empty;
    public int SpeedKhz => _speedKhz;
    public long TotalBytesRead => Interlocked.Read(ref _bytesRead);
    public long TotalBytesWritten => Interlocked.Read(ref _bytesWritten);

    static JLinkWrapper()
    {
        JLinkLibraryLocator.EnsureResolverInstalled();
    }

    /// <summary>J-Link 驱动是否可用（DLL 是否存在且可加载）。</summary>
    public static bool IsDriverAvailable => JLinkLibraryLocator.TryLoad();

    /// <summary>驱动描述：版本 + 实际加载路径，便于日志与界面显示。</summary>
    public static string DriverDescription
    {
        get
        {
            var path = JLinkLibraryLocator.DllPath;
            if (path == null) return "未找到 J-Link SDK";

            var version = JLinkLibraryLocator.VersionLabel;
            return version == null ? path : $"SEGGER J-Link {version} · {path}";
        }
    }

    public bool Open(string? serialNumber, string device, JLinkInterface iface, int speedKhz, out string error)
    {
        error = string.Empty;
        lock (_stateLock)
        {
            try
            {
                CloseInternal();

                // 1. 打开 J-Link 调试器硬件（此步只连接调试器，尚未连接目标芯片）
                // 注意：实测 V9.48 x64 DLL 的 JLINKARM_OpenEx 成功时也返回 0，
                // 返回值不是有效句柄，不能用它判断成败；改用 GetSN 验证调试器是否就绪。
                JLinkNativeMethods.JLINKARM_OpenEx(IntPtr.Zero, IntPtr.Zero);
                if (SafeGetSn() <= 0)
                {
                    IsConnected = false;
                    error = "J-Link 打开失败：未检测到调试器（请检查 USB 连接与驱动）。";
                    return false;
                }

                // 2. 选择目标芯片（加载对应的 Flash/RAM 算法）
                if (!string.IsNullOrWhiteSpace(device))
                {
                    var sb = new StringBuilder(512);
                    int rc = JLinkNativeMethods.JLINKARM_ExecCommand($"device = {device}", sb, sb.Capacity);
                    TargetDevice = device;

                    // 型号不被 J-Link 识别时，后续 Connect 会全部失败且错误信息含糊，
                    // 这里提前拦截并给出明确原因（ExecCommand 返回 0 表示成功）。
                    if (rc != 0)
                    {
                        string detail = sb.ToString().Trim();
                        IsConnected = false;
                        JLinkNativeMethods.JLINKARM_Close();
                        error = $"目标芯片型号 “{device}” 未被 J-Link 识别" +
                                (string.IsNullOrEmpty(detail) ? $"（错误码 {rc}）" : $"：{detail}") +
                                "。请填写完整型号，例如 STM32F407VE / STM32H743VI。";
                        return false;
                    }
                }

                // 3. 指定调试器序列号（多调试器共存时）
                if (!string.IsNullOrWhiteSpace(serialNumber))
                    ExecuteCommand($"SelectEmuBySN {serialNumber}");

                // 4. 选择接口与通信速率
                JLinkNativeMethods.JLINKARM_TIF_Select(iface == JLinkInterface.SWD
                    ? JLinkNativeMethods.TIF_SWD
                    : JLinkNativeMethods.TIF_JTAG);
                // 5. 连接目标芯片：握手失败时逐级降速重试
                //    （新板 / 长排线 / 目标时钟未起振时，高速握手失败很常见，降速往往能连上）
                int connectedSpeed = 0;
                foreach (int candidate in BuildSpeedFallbacks(speedKhz))
                {
                    JLinkNativeMethods.JLINKARM_SetSpeed(candidate);
                    if (JLinkNativeMethods.JLINKARM_Connect() >= 0 &&
                        JLinkNativeMethods.JLINKARM_IsConnected() > 0)
                    {
                        connectedSpeed = candidate;
                        break;
                    }
                }

                if (connectedSpeed == 0)
                {
                    IsConnected = false;
                    error = $"J-Link 已打开但目标 CPU 未连接（型号 {TargetDevice}，已尝试 {string.Join(" / ", BuildSpeedFallbacks(speedKhz))} kHz）：" +
                            "请检查 SWD 接线（SWDIO/SWCLK/GND）、目标板供电、芯片型号是否与目标板一致，" +
                            "以及芯片是否处于复位或读保护状态。";
                    JLinkNativeMethods.JLINKARM_Close();
                    return false;
                }

                IsConnected = true;
                _speedKhz = connectedSpeed;
                int sn = SafeGetSn();
                Description = $"J-Link (SN: {sn}) · {TargetDevice} · {iface} {connectedSpeed}kHz" +
                              (connectedSpeed != speedKhz ? $"（已自动降速，请求 {speedKhz}kHz）" : string.Empty);
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                IsConnected = false;
                error = $"未找到 J-Link 驱动 {JLinkNativeMethods.DllName}：{ex.Message}（请在「设置 → 驱动与关于」中指定安装目录）";
                return false;
            }
            catch (Exception ex)
            {
                IsConnected = false;
                error = $"J-Link 连接异常: {ex.Message}";
                return false;
            }
        }
    }

    public void Close()
    {
        lock (_stateLock) CloseInternal();
    }

    private void CloseInternal()
    {
        try
        {
            if (IsConnected) JLinkNativeMethods.JLINKARM_Close();
        }
        catch
        {
            // 驱动缺失时忽略
        }
        IsConnected = false;
        Description = "J-Link (未连接)";
    }

    public int ReadMemory(uint address, byte[] buffer)
    {
        if (!IsConnected) return -1;
        if (!TryEnterBus(LockTimeout)) return -1;
        try
        {
            int result = JLinkNativeMethods.JLINKARM_ReadMemEx(address, (uint)buffer.Length, buffer, 0);
            if (result > 0) Interlocked.Add(ref _bytesRead, buffer.Length);
            return result;
        }
        catch
        {
            return -1;
        }
        finally
        {
            _busLock.Release();
        }
    }

    public int WriteMemory(uint address, byte[] buffer)
    {
        if (!IsConnected) return -1;
        if (!TryEnterBus(LockTimeout)) return -1;
        try
        {
            int result = JLinkNativeMethods.JLINKARM_WriteMemEx(address, (uint)buffer.Length, buffer, 0);
            if (result >= 0) Interlocked.Add(ref _bytesWritten, buffer.Length);
            return result;
        }
        catch
        {
            return -1;
        }
        finally
        {
            _busLock.Release();
        }
    }

    public int RttRead(int channel, byte[] buffer)
    {
        if (!IsConnected) return -1;
        if (!TryEnterBus(LockTimeout)) return -1;
        try
        {
            int count = JLinkNativeMethods.JLINK_RTTERMINAL_Read(channel, buffer, buffer.Length);
            if (count > 0) Interlocked.Add(ref _bytesRead, count);
            return count;
        }
        catch
        {
            return -1;
        }
        finally
        {
            _busLock.Release();
        }
    }

    public int RttWrite(int channel, byte[] buffer)
    {
        if (!IsConnected) return -1;
        if (!TryEnterBus(LockTimeout)) return -1;
        try
        {
            int count = JLinkNativeMethods.JLINK_RTTERMINAL_Write(channel, buffer, buffer.Length);
            if (count > 0) Interlocked.Add(ref _bytesWritten, count);
            return count;
        }
        catch
        {
            return -1;
        }
        finally
        {
            _busLock.Release();
        }
    }

    public bool RttStart(uint? controlBlockAddress, out string error)
    {
        error = string.Empty;
        if (!IsConnected)
        {
            error = "未连接目标，无法启动 RTT。";
            return false;
        }

        if (!TryEnterBus(LockTimeout))
        {
            error = "硬件访问忙，启动 RTT 超时。";
            return false;
        }

        try
        {
            if (controlBlockAddress.HasValue)
                ExecuteCommandUnlocked($"SetRTTAddr 0x{controlBlockAddress.Value:X8}");

            var config = new JLinkNativeMethods.RttConfigTransfer { BufferIndex = 0 };
            int result = JLinkNativeMethods.JLINK_RTTERMINAL_Control(JLinkNativeMethods.RTT_CMD_START, ref config);
            if (result < 0)
            {
                error = $"RTT 启动失败（错误码 {result}）：请确认目标固件已编译 SEGGER RTT 且控制块地址正确。";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"RTT 启动异常: {ex.Message}";
            return false;
        }
        finally
        {
            _busLock.Release();
        }
    }

    public void RttStop()
    {
        if (!IsConnected) return;
        if (!TryEnterBus(TimeSpan.FromSeconds(1))) return;
        try
        {
            var config = new JLinkNativeMethods.RttConfigTransfer();
            JLinkNativeMethods.JLINK_RTTERMINAL_Control(JLinkNativeMethods.RTT_CMD_STOP, ref config);
        }
        catch
        {
            // ignore
        }
        finally
        {
            _busLock.Release();
        }
    }

    public bool Reset(out string error) => ExecuteSimple("复位", () => JLinkNativeMethods.JLINKARM_Reset(), out error);

    public bool Halt(out string error) => ExecuteSimple("暂停 CPU", () => JLinkNativeMethods.JLINKARM_Halt(), out error);

    public bool Go(out string error) => ExecuteSimple("运行 CPU", () => JLinkNativeMethods.JLINKARM_Go(), out error);

    /// <summary>Flash 编程可能持续数十秒（擦除 + 写入 + 校验），单独给一个更宽松的总线占用超时。</summary>
    private static readonly TimeSpan FlashTimeout = TimeSpan.FromSeconds(180);

    /// <summary>
    /// 通过 J-Link 内置 Flash 算法把固件烧写到目标芯片。
    /// .bin 为纯二进制镜像必须指定起始地址，.hex / .elf / .axf 自带地址信息。
    /// </summary>
    public bool DownloadFile(string filePath, uint address, out string error)
    {
        error = string.Empty;
        if (!IsConnected)
        {
            error = "未连接目标，请先连接 J-Link。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(TargetDevice))
        {
            error = "未配置目标芯片型号：Flash 算法依赖型号选择，请先在「设备与工程配置」中选择芯片型号。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            error = $"固件文件不存在：{filePath}";
            return false;
        }

        // .bin 不含地址信息，必须由调用方给出烧写起始地址
        bool needsAddress = string.Equals(Path.GetExtension(filePath), ".bin", StringComparison.OrdinalIgnoreCase);
        if (needsAddress && address == 0)
        {
            error = ".bin 固件需要指定起始地址（例如 0x08000000）。";
            return false;
        }

        if (!TryEnterBus(FlashTimeout))
        {
            error = "下载超时：硬件访问队列被占用，请稍后重试。";
            return false;
        }

        string? tempCopy = null;
        try
        {
            // 编程 Flash 前先停机，避免目标继续执行干扰擦除与写入
            JLinkNativeMethods.JLINKARM_Halt();

            // J-Link 原生接口只接受 ASCII 路径，工程放在中文目录下时先把固件复制到临时目录再烧写
            string source = filePath;
            if (!IsAsciiPath(filePath))
            {
                tempCopy = Path.Combine(Path.GetTempPath(), $"jlink_dl_{Guid.NewGuid():N}{Path.GetExtension(filePath)}");
                File.Copy(filePath, tempCopy, true);
                source = tempCopy;
            }

            // 必须用 JLINK_DownloadFile，不能走 JLINKARM_ExecCommand("loadbin/loadfile")：
            // V9.48 的 ExecCommand 只认 device / si / speed 这类配置命令，
            // load* 会直接返回 "ERROR: Unknown command"（这正是第一次下载失败的原因）。
            int rc = JLinkNativeMethods.JLINK_DownloadFile(source, needsAddress ? address : 0u);
            if (rc != 0)
            {
                error = DescribeDownloadFailure(rc, filePath, needsAddress, address);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"下载失败: {ex.Message}";
            return false;
        }
        finally
        {
            _busLock.Release();
            if (tempCopy != null)
            {
                try { File.Delete(tempCopy); } catch { /* 临时文件清理失败不影响结果 */ }
            }
        }
    }

    private static bool IsAsciiPath(string path)
    {
        foreach (char ch in path)
        {
            if (ch > 127) return false;
        }

        return true;
    }

    /// <summary>
    /// JLINK_DownloadFile 只返回整数错误码，DLL 未提供错误码转文本的接口，
    /// 因此这里保留原始错误码（便于对照 SEGGER 手册）并给出常见原因清单。
    /// </summary>
    private static string DescribeDownloadFailure(int code, string filePath, bool binFile, uint address)
    {
        string where = $"文件 {Path.GetFileName(filePath)}"
                       + (binFile ? $"，起始地址 0x{address:X8}" : "（地址取自文件内容）");

        return $"J-Link 下载失败（错误码 {code}）：{where}。" +
               "常见原因：芯片型号与实际目标不符导致 Flash 算法不匹配、" +
               "起始地址不在 Flash 区间、芯片被读保护/写保护锁定、" +
               "固件格式不被支持（支持 .bin / .hex / .elf / .mot / .srec），或供电与 SWD 时序不稳定。";
    }

    public bool IsHalted
    {
        get
        {
            if (!IsConnected) return false;
            if (!TryEnterBus(LockTimeout)) return false;
            try
            {
                return JLinkNativeMethods.JLINKARM_IsHalted() > 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                _busLock.Release();
            }
        }
    }

    private bool ExecuteSimple(string action, Action call, out string error)
    {
        error = string.Empty;
        if (!IsConnected)
        {
            error = $"未连接目标，无法{action}。";
            return false;
        }
        if (!TryEnterBus(LockTimeout))
        {
            error = $"{action}超时：硬件访问队列繁忙。";
            return false;
        }
        try
        {
            call();
            return true;
        }
        catch (Exception ex)
        {
            error = $"{action}失败: {ex.Message}";
            return false;
        }
        finally
        {
            _busLock.Release();
        }
    }

    public uint ReadRegister(int index)
    {
        if (!IsConnected) return 0;
        if (!TryEnterBus(LockTimeout)) return 0;
        try
        {
            return JLinkNativeMethods.JLINKARM_ReadReg(index);
        }
        catch
        {
            return 0;
        }
        finally
        {
            _busLock.Release();
        }
    }

    public string[] EnumerateEmulators()
    {
        try
        {
            int sn = SafeGetSn();
            return sn > 0 ? new[] { sn.ToString() } : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>握手失败时的降速阶梯：请求速率 + 依次降低的候选速率。</summary>
    private static IEnumerable<int> BuildSpeedFallbacks(int requestedKhz)
    {
        int start = Math.Clamp(requestedKhz, 5, 50000);
        yield return start;

        foreach (int candidate in new[] { 1000, 500, 100, 50 })
        {
            if (candidate < start) yield return candidate;
        }
    }

    private int SafeGetSn()
    {
        try
        {
            return JLinkNativeMethods.JLINKARM_GetSN();
        }
        catch
        {
            return 0;
        }
    }

    private void ExecuteCommand(string command)
    {
        if (!TryEnterBus(LockTimeout)) return;
        try
        {
            ExecuteCommandUnlocked(command);
        }
        finally
        {
            _busLock.Release();
        }
    }

    /// <summary>执行 J-Link Commander 风格命令，返回该命令的错误文本（成功时为空串）。</summary>
    private static string ExecuteCommandUnlocked(string command)
    {
        var sb = new StringBuilder(1024);
        JLinkNativeMethods.JLINKARM_ExecCommand(command, sb, sb.Capacity);
        return sb.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 先取得总线锁，确保在途的 ReadMem/RTT/变量写操作全部完成后才关闭驱动；
        // 之后再释放信号量，期间阻塞在 Wait 上的线程由 TryEnterBus 兜底，不会抛异常。
        bool acquired = false;
        try
        {
            acquired = _busLock.Wait(TimeSpan.FromSeconds(2));
        }
        catch (ObjectDisposedException)
        {
            // 已被并发 Dispose 释放
        }

        try
        {
            lock (_stateLock) CloseInternal();
        }
        finally
        {
            if (acquired)
            {
                try { _busLock.Release(); }
                catch (ObjectDisposedException) { }
            }
            _busLock.Dispose();
        }
    }
}
