using JLinkStudio.Core.Interop;

namespace JLinkStudio.Core.Services;

/// <summary>芯片识别结果：命中的代表型号、内核名称与识别依据。</summary>
public sealed record DeviceDetection(string? DeviceName, string CoreName, string Detail);

/// <summary>
/// 目标芯片自动识别：读取内核 CPUID 与厂商 ID 寄存器（DBGMCU_IDCODE / FICR 等），
/// 映射到 J-Link 可用的设备名。识别粒度到“系列”，同一系列的 Flash/引脚差异需人工确认。
/// </summary>
public static class TargetDeviceDetector
{
    /// <summary>STM32 / GD32 的 DBGMCU_IDCODE DEV_ID → 系列与代表型号。</summary>
    private static readonly Dictionary<uint, (string Series, string Device)> DevIdMap = new()
    {
        [0x410] = ("STM32F10x 中容量", "STM32F103C8"),
        [0x412] = ("STM32F10x 小容量", "STM32F103C6"),
        [0x414] = ("STM32F10x 大容量", "STM32F103VC"),
        [0x418] = ("STM32F105/107", "STM32F107VC"),
        [0x420] = ("STM32F100 超值型", "STM32F100RB"),
        [0x413] = ("STM32F405/407/415/417", "STM32F407VG"),
        [0x419] = ("STM32F42x/43x", "STM32F429ZI"),
        [0x421] = ("STM32F446", "STM32F446ZE"),
        [0x434] = ("STM32F469/479", "STM32F469NI"),
        [0x440] = ("STM32F030/F070", "STM32F030C8"),
        [0x445] = ("STM32F04x/070x", "STM32F042C6"),
        [0x448] = ("STM32F07x", "STM32F072CB"),
        [0x442] = ("STM32F09x", "STM32F091RC"),
        [0x450] = ("STM32H742/743/750/753", "STM32H743VI"),
        [0x480] = ("STM32H7A3/7B3", "STM32H7B3LI"),
        [0x483] = ("STM32H72x/73x", "STM32H723ZG"),
        [0x460] = ("STM32G0x1", "STM32G071RB"),
        [0x466] = ("STM32G03x/04x", "STM32G030C8"),
        [0x468] = ("STM32G431/441", "STM32G431CB"),
        [0x469] = ("STM32G47x/48x", "STM32G474RE"),
        [0x479] = ("STM32G491/4A1", "STM32G491RE"),
        [0x435] = ("STM32L43x/44x", "STM32L431RC"),
        [0x462] = ("STM32L45x/46x", "STM32L452RE"),
        [0x464] = ("STM32L41x/42x", "STM32L412KB"),
        [0x415] = ("STM32L47x/48x", "STM32L476RG"),
        [0x470] = ("STM32L4Rx/4Sx", "STM32L496AG"),
        [0x472] = ("STM32L4P5/4Q5", "STM32L4P5AG"),
    };

    /// <summary>Nordic FICR.INFO.PART → J-Link 设备名。</summary>
    private static readonly Dictionary<uint, string> NrfPartMap = new()
    {
        [0x52840] = "NRF52840_XXAA",
        [0x52833] = "NRF52833_XXAA",
        [0x52832] = "NRF52832_XXAA",
        [0x52811] = "NRF52811_XXAA",
        [0x52810] = "NRF52810_XXAA",
        [0x52805] = "NRF52805_XXAA",
    };

    public static DeviceDetection Detect(IJLinkLink link)
    {
        var notes = new List<string>();
        string core = ReadCoreName(link);

        // 1. STM32 / GD32 系列：DBGMCU_IDCODE（F4/F7/H7/G4/L4 在 0xE0042000；F0/L0/G0 在 0x40015800）
        foreach (var probeAddress in new uint[] { 0xE0042000, 0x40015800 })
        {
            if (!TryRead(link, probeAddress, out uint value)) continue;
            if (value == 0 || value == 0xFFFFFFFF) continue;

            uint devId = value & 0xFFF;
            if (devId == 0) continue;

            if (DevIdMap.TryGetValue(devId, out var info))
            {
                return new DeviceDetection(info.Device, core,
                    $"DBGMCU_IDCODE@0x{probeAddress:X8} = 0x{value:X8}（DEV_ID 0x{devId:X3}）→ {info.Series}；" +
                    $"已填入代表型号 {info.Device}，若是同系列其它引脚/Flash 版本请手动改具体型号。");
            }

            notes.Add($"DBGMCU_IDCODE@0x{probeAddress:X8} = 0x{value:X8}（DEV_ID 0x{devId:X3} 未收录）");
        }

        // 2. Nordic nRF52 系列：FICR.INFO.PART
        if (TryRead(link, 0x10000100, out uint part))
        {
            uint partId = part & 0xFFFF;
            if (NrfPartMap.TryGetValue(partId, out var device))
                return new DeviceDetection(device, core, $"nRF FICR.INFO.PART = 0x{partId:X4} → {device}");
            if (partId != 0 && partId != 0xFFFF)
                notes.Add($"FICR.INFO.PART = 0x{partId:X4}（未收录）");
        }

        notes.Insert(0, core == "未知内核" ? "未能读取内核 CPUID" : $"内核：{core}");
        notes.Add("可改用「常用型号」手动指定，或在 J-Link 设备列表中查询具体型号");
        return new DeviceDetection(null, core, string.Join("；", notes));
    }

    private static bool TryRead(IJLinkLink link, uint address, out uint value)
    {
        value = 0;
        var buffer = new byte[4];
        int read = link.ReadMemory(address, buffer);
        if (read < 4) return false;
        value = BitConverter.ToUInt32(buffer, 0);
        return true;
    }

    private static string ReadCoreName(IJLinkLink link)
    {
        if (!TryRead(link, 0xE000ED00, out uint cpuid)) return "未知内核";
        if ((cpuid >> 24) != 0x41) return $"非 ARM 内核（CPUID 0x{cpuid:X8}）";

        return ((cpuid >> 4) & 0xFFF) switch
        {
            0xC20 => "Cortex-M0",
            0xC21 => "Cortex-M1",
            0xC23 => "Cortex-M3",
            0xC24 => "Cortex-M4",
            0xC27 => "Cortex-M7",
            0xD20 => "Cortex-M23",
            0xD21 => "Cortex-M33",
            0xD22 => "Cortex-M55",
            var partNo => $"Cortex-M?（PARTNO 0x{partNo:X3}）",
        };
    }
}
