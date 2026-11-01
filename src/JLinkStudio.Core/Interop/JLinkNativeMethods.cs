using System.Runtime.InteropServices;
using System.Text;

namespace JLinkStudio.Core.Interop;

/// <summary>SEGGER J-Link SDK 原生接口声明（JLink_x64.dll）。</summary>
internal static class JLinkNativeMethods
{
    /// <summary>64 位进程使用 JLink_x64.dll；32 位进程编译时定义 X86 常量以使用 JLinkARM.dll。</summary>
#if X86
    internal const string DllName = "JLinkARM.dll";
#else
    internal const string DllName = "JLink_x64.dll";
#endif

    [StructLayout(LayoutKind.Sequential)]
    internal struct RttConfigTransfer
    {
        public uint BufferIndex;
        public uint Flags;
        public uint Status;
        public uint BytesTransferred;
    }

    internal const int RTT_CMD_START = 0;
    internal const int RTT_CMD_STOP = 1;
    internal const int RTT_CMD_GETDESC = 2;
    internal const int RTT_CMD_GETSTAT = 3;

    internal const int TIF_SWD = 1;
    internal const int TIF_JTAG = 2;

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi, EntryPoint = "JLINKARM_OpenEx")]
    internal static extern IntPtr JLINKARM_OpenEx(IntPtr pfLog, IntPtr pfErrorOut);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "JLINKARM_Close")]
    internal static extern void JLINKARM_Close();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "JLINKARM_IsConnected")]
    internal static extern int JLINKARM_IsConnected();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "JLINKARM_Connect")]
    internal static extern int JLINKARM_Connect();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "JLINKARM_TIF_Select")]
    internal static extern int JLINKARM_TIF_Select(int interfaceType);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "JLINKARM_SetSpeed")]
    internal static extern void JLINKARM_SetSpeed(int speedKhz);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi, EntryPoint = "JLINKARM_ExecCommand")]
    internal static extern int JLINKARM_ExecCommand(string command, StringBuilder? errorOut, int bufferSize);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "JLINKARM_ReadMemEx")]
    internal static extern int JLINKARM_ReadMemEx(uint address, uint numBytes, byte[] data, uint accessWidth);

    /// <summary>
    /// 把固件文件烧写到目标（走 J-Link 内置 Flash 算法，自动擦除 + 编程 + 校验）。
    /// 返回 0 表示成功；.hex / .elf / .mot 使用文件内地址，address 仅对 .bin 生效。
    /// 注意：本函数在 V9.48 DLL 中只有 JLINK_ 前缀的导出名，没有 JLINKARM_ 别名。
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi, EntryPoint = "JLINK_DownloadFile")]
    internal static extern int JLINK_DownloadFile(string filePath, uint address);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "JLINKARM_WriteMemEx")]
    internal static extern int JLINKARM_WriteMemEx(uint address, uint numBytes, byte[] data, uint accessWidth);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "JLINKARM_ReadReg")]
    internal static extern uint JLINKARM_ReadReg(int registerIndex);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "JLINKARM_Reset")]
    internal static extern void JLINKARM_Reset();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "JLINKARM_Halt")]
    internal static extern void JLINKARM_Halt();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "JLINKARM_IsHalted")]
    internal static extern int JLINKARM_IsHalted();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "JLINKARM_Go")]
    internal static extern void JLINKARM_Go();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "JLINKARM_GetSN")]
    internal static extern int JLINKARM_GetSN();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "JLINK_RTTERMINAL_Control")]
    internal static extern int JLINK_RTTERMINAL_Control(int command, ref RttConfigTransfer config);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "JLINK_RTTERMINAL_Read")]
    internal static extern int JLINK_RTTERMINAL_Read(int bufferIndex, byte[] buffer, int bufferSize);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, EntryPoint = "JLINK_RTTERMINAL_Write")]
    internal static extern int JLINK_RTTERMINAL_Write(int bufferIndex, byte[] buffer, int bufferSize);
}
