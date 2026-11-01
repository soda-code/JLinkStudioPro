using JLinkStudio.Core.Models;

namespace JLinkStudio.Core.Interop;

/// <summary>
/// 硬件访问抽象：上层引擎（RTT、变量轮询、寄存器读写）与 Skill 网关
/// 只依赖该接口，无需关心底层是哪个 J-Link 调试器。
/// </summary>
public interface IJLinkLink : IDisposable
{
    bool IsConnected { get; }
    string Description { get; }
    string TargetDevice { get; }
    int SpeedKhz { get; }
    long TotalBytesRead { get; }
    long TotalBytesWritten { get; }

    bool Open(string? serialNumber, string device, JLinkInterface iface, int speedKhz, out string error);
    void Close();

    int ReadMemory(uint address, byte[] buffer);
    int WriteMemory(uint address, byte[] buffer);

    int RttRead(int channel, byte[] buffer);
    int RttWrite(int channel, byte[] buffer);
    bool RttStart(uint? controlBlockAddress, out string error);
    void RttStop();

    bool Reset(out string error);
    bool Halt(out string error);
    bool Go(out string error);

    /// <summary>
    /// 下载（烧写）固件到目标 Flash：.hex / .elf / .axf 使用文件自带地址，
    /// .bin 为纯二进制镜像，必须由 <paramref name="address"/> 指定烧写起始地址。
    /// </summary>
    bool DownloadFile(string filePath, uint address, out string error);

    /// <summary>CPU 是否处于暂停（halted）状态；运行中读取内核寄存器会被 J-Link 拒绝。</summary>
    bool IsHalted { get; }

    uint ReadRegister(int index);
    string[] EnumerateEmulators();
}
