namespace JLinkStudio.Core.Models;

/// <summary>固件下载目标：片内 Flash 与片外（QSPI / 并行 NOR / SDRAM 等）存储器。</summary>
public enum FlashTarget
{
    /// <summary>芯片内部 Flash，走 J-Link 内置 Flash 算法，地址通常为 0x08000000。</summary>
    Internal = 0,

    /// <summary>
    /// 片外存储器。J-Link 靠「设备型号」决定片外 Flash 算法，
    /// 通常需要带接口后缀的型号（如 STM32H743XI_QSPI），或型号本身就含外部存储支持。
    /// </summary>
    External = 1,
}

/// <summary>
/// 下载参数。片内下载用默认值即可；片外下载补充 J-Link 设备名与片外基址。
/// </summary>
public sealed class FlashDownloadOptions
{
    /// <summary>下载目标，默认片内 Flash。</summary>
    public FlashTarget Target { get; init; } = FlashTarget.Internal;

    /// <summary>
    /// 片外下载使用的 J-Link 设备名（如 STM32H743XI_QSPI）。
    /// 留空表示沿用当前型号；与当前型号不同时会临时切换型号重连，烧完再切回。
    /// </summary>
    public string? ExternalDevice { get; init; }

    /// <summary>片外 Flash 基址，.bin 未填地址时作为烧写起始地址，默认 0x90000000（QSPI 常见映射）。</summary>
    public uint ExternalBase { get; init; } = 0x9000_0000;

    /// <summary>片内下载的默认参数。</summary>
    public static readonly FlashDownloadOptions Internal = new();
}
