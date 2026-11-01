using System.Text.Json;
using System.Text.Json.Serialization;

namespace JLinkStudio.SkillGateway.Models;

public sealed class SessionConfigRequest
{
    public string? Device { get; set; }
    public string? Interface { get; set; }
    public int? SpeedKhz { get; set; }
    public string? ElfPath { get; set; }
    public string? SvdPath { get; set; }
    /// <summary>AutoFromElf / AutoScan / ManualAddress</summary>
    public string? RttSearchMode { get; set; }
    public uint? RttControlBlockAddress { get; set; }
    public string? SerialNumber { get; set; }
    public bool? AutoConnect { get; set; }
}

public sealed class VariableReadRequest
{
    public List<string> Names { get; set; } = new();
}

public sealed class VariableWriteRequest
{
    public List<VariableWriteItem> Writes { get; set; } = new();
}

public sealed class VariableWriteItem
{
    public string Name { get; set; } = string.Empty;
    public JsonElement Value { get; set; }
}

/// <summary>
/// 批量写入：一次请求写多个变量，且每个变量可带一串值（按 intervalMs 依次下发）。
/// 适合「一次传输每个变量的多个数据」，例如下发一段波形、批量设置参数。
/// </summary>
public sealed class BatchWriteRequest
{
    public List<VariableSeriesItem> Writes { get; set; } = new();

    /// <summary>每一步之间的间隔（毫秒），0 表示连续写入不等待。</summary>
    public int IntervalMs { get; set; }

    /// <summary>全部写完后是否回读一次当前值（默认 true）。</summary>
    public bool ReadBack { get; set; } = true;
}

public sealed class VariableSeriesItem
{
    public string Name { get; set; } = string.Empty;

    /// <summary>要写入的一个或多个值；单值也写成数组，如 [1] 或 [1,2,3]。</summary>
    public List<JsonElement> Values { get; set; } = new();
}

/// <summary>运行参数设置（外部可改）。</summary>
public sealed class SettingsRequest
{
    /// <summary>查看模式：true 只读（禁止一切写目标操作）。</summary>
    public bool? InspectMode { get; set; }

    /// <summary>Skill 写保护总开关。</summary>
    public bool? WriteEnabled { get; set; }

    /// <summary>变量轮询采样启停。</summary>
    public bool? VariablesSampling { get; set; }

    /// <summary>轮询采样率（Hz），范围 1~1000，超出会被夹取。</summary>
    public double? SampleRateHz { get; set; }

    /// <summary>RTT 采集启停。</summary>
    public bool? RttEnabled { get; set; }

    /// <summary>ELF 变化时是否自动重新加载符号。</summary>
    public bool? AutoReloadElf { get; set; }
}

/// <summary>Keil 工具链设置。</summary>
public sealed class KeilConfigRequest
{
    /// <summary>Keil 安装目录，默认 D:\Keil_V5（也可直接填 UV4.exe 全路径）。</summary>
    public string? InstallPath { get; set; }

    /// <summary>默认 Keil 工程文件（.uvprojx / .uvproj）绝对路径。</summary>
    public string? ProjectPath { get; set; }
}

/// <summary>Keil 编译 / 下载请求。</summary>
public sealed class KeilActionRequest
{
    /// <summary>工程文件绝对路径；不填则用设置里保存的工程。</summary>
    public string? ProjectPath { get; set; }

    /// <summary>目标名（多目标工程时指定，对应 UV4 -t）。</summary>
    public string? Target { get; set; }

    /// <summary>超时（毫秒），默认 10 分钟。</summary>
    public int TimeoutMs { get; set; } = 600_000;
}

public sealed class TimeSeriesRequest
{
    public List<string> Names { get; set; } = new();
    public int SampleRateHz { get; set; } = 200;
    public int DurationMs { get; set; } = 1000;
}

public sealed class FieldWriteRequest
{
    public string Peripheral { get; set; } = string.Empty;
    public string Register { get; set; } = string.Empty;
    public string Field { get; set; } = string.Empty;
    public ulong Value { get; set; }
}

public sealed class RttSendRequest
{
    public string Text { get; set; } = string.Empty;
    public int Channel { get; set; }
    public bool Hex { get; set; }
    public bool AppendNewLine { get; set; } = true;
}

public sealed class SkillPolicyRequest
{
    public bool? WriteEnabled { get; set; }
    public List<GuardDto>? Guards { get; set; }
    public List<string>? ExposedSymbols { get; set; }
}

public sealed class GuardDto
{
    public string Pattern { get; set; } = "*";
    public double? Min { get; set; }
    public double? Max { get; set; }
    public bool AllowWrite { get; set; } = true;
}

public sealed class VariableSampleDto
{
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public object? Value { get; set; }
    public string ValueText { get; set; } = string.Empty;
}

public sealed class ReadResponse
{
    public long Timestamp { get; set; }
    public Dictionary<string, VariableSampleDto> Data { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Errors { get; set; } = new();
}

public sealed class RttLineDto
{
    public long Timestamp { get; set; }
    public int Channel { get; set; }
    public string Level { get; set; } = "Info";
    public string Text { get; set; } = string.Empty;
    public Dictionary<string, string> Fields { get; set; } = new();
}

public sealed class FaultSnapshotDto
{
    public long Timestamp { get; set; }
    public string Device { get; set; } = string.Empty;
    public Dictionary<string, string> CoreRegisters { get; set; } = new();
    public List<object?> FaultRegisters { get; set; } = new();
    public List<RttLineDto> RecentLogs { get; set; } = new();
    public string Diagnosis { get; set; } = string.Empty;
}

/// <summary>固件烧写请求（等价于界面「程序下载」页）。</summary>
public sealed class FlashDownloadRequest
{
    /// <summary>固件文件绝对路径，支持 .bin / .hex / .elf / .axf / .mot / .srec。</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>烧写起始地址（十六进制字符串 "0x08000000" 或十进制）；.hex/.elf 可留空。</summary>
    public string? Address { get; set; }

    /// <summary>烧写完成后是否复位并运行（默认 true）。</summary>
    public bool Reset { get; set; } = true;
}

/// <summary>监控项属性（不传的字段保持原值）。</summary>
public sealed class WatchItemRequest
{
    public string Name { get; set; } = string.Empty;

    /// <summary>显示进制：Dec / Hex / Bin / Float / Ascii。</summary>
    public string? Format { get; set; }

    /// <summary>波形颜色 ARGB（十进制或 0xAARRGGBB 字符串），0 表示按调色板自动分配。</summary>
    public string? Color { get; set; }

    /// <summary>是否绘制波形曲线。</summary>
    public bool? PlotEnabled { get; set; }

    /// <summary>使能：关掉后不参与轮询刷新与波形绘制。</summary>
    public bool? Enabled { get; set; }

    /// <summary>允许外部写入该项。</summary>
    public bool? IsWritable { get; set; }

    /// <summary>是否暴露给 Skill（白名单外仍可读）。</summary>
    public bool? ExposeToSkill { get; set; }

    /// <summary>该项的独立刷新率（Hz），0 表示跟随全局采样率。</summary>
    public double? RefreshRateHz { get; set; }
}

/// <summary>
/// 加入监控列表：<see cref="Names"/> 只加名字（兼容旧调用），
/// <see cref="Items"/> 可同时设置每项的格式 / 颜色 / 波形 / 使能等属性。
/// </summary>
public sealed class WatchRequest
{
    public List<string> Names { get; set; } = new();
    public List<WatchItemRequest> Items { get; set; } = new();
}
