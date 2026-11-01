namespace JLinkStudio.Core.Models;

/// <summary>变量/寄存器数据类型（由 ELF/DWARF 或 SVD 解析得出）。</summary>
public enum DataType
{
    Unknown = 0,
    Bool,
    UInt8,
    Int8,
    UInt16,
    Int16,
    UInt32,
    Int32,
    UInt64,
    Int64,
    Float,
    Double,
    Pointer,
    Struct,
    Union,
    Array,
    Enum,
}

public static class DataTypeExtensions
{
    public static int SizeOf(this DataType type) => type switch
    {
        DataType.Bool or DataType.UInt8 or DataType.Int8 => 1,
        DataType.UInt16 or DataType.Int16 => 2,
        DataType.UInt32 or DataType.Int32 or DataType.Float or DataType.Pointer => 4,
        DataType.UInt64 or DataType.Int64 or DataType.Double => 8,
        _ => 0,
    };

    public static bool IsNumericIntegral(this DataType type) => type is
        DataType.Bool or DataType.UInt8 or DataType.Int8 or DataType.UInt16 or DataType.Int16
        or DataType.UInt32 or DataType.Int32 or DataType.UInt64 or DataType.Int64
        or DataType.Pointer or DataType.Enum;

    public static bool IsFloat(this DataType type) => type is DataType.Float or DataType.Double;

    public static string ToCName(this DataType type) => type switch
    {
        DataType.Bool => "bool",
        DataType.UInt8 => "uint8",
        DataType.Int8 => "int8",
        DataType.UInt16 => "uint16",
        DataType.Int16 => "int16",
        DataType.UInt32 => "uint32",
        DataType.Int32 => "int32",
        DataType.UInt64 => "uint64",
        DataType.Int64 => "int64",
        DataType.Float => "float",
        DataType.Double => "double",
        DataType.Pointer => "pointer",
        DataType.Struct => "struct",
        DataType.Union => "union",
        DataType.Array => "array",
        DataType.Enum => "enum",
        _ => "unknown",
    };
}

/// <summary>界面数值显示进制。</summary>
public enum ValueFormat
{
    Dec = 0,
    Hex,
    Bin,
    Float,
    Ascii,
}

/// <summary>被监控的变量（结构体/数组会展开为 Children 树）。</summary>
public sealed class WatchSymbol
{
    public string Name { get; set; } = string.Empty;
    public uint Address { get; set; }
    public DataType Type { get; set; }
    public int ByteSize { get; set; }
    public object? CurrentValue { get; set; }
    public string CurrentValueText { get; set; } = string.Empty;
    public double RefreshRateHz { get; set; } = 100;
    /// <summary>使能：勾选后参与轮询刷新与波形绘制。</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>波形曲线颜色（ARGB），0 表示未指定，由界面按调色板自动分配。</summary>
    public int ColorArgb { get; set; }
    public bool PlotEnabled { get; set; }
    public bool ExposeToSkill { get; set; }
    public bool IsWritable { get; set; } = true;
    public ValueFormat Format { get; set; } = ValueFormat.Dec;
    public int OffsetInParent { get; set; }
    public WatchSymbol? Parent { get; set; }
    public List<WatchSymbol> Children { get; set; } = new();

    /// <summary>树状显示时是否展开子节点（仅界面状态，不影响采样）。</summary>
    public bool Expanded { get; set; } = true;

    /// <summary>仅用于结构体/数组容器的展示，不直接读取内存。</summary>
    public bool IsContainer => Children.Count > 0 && Type is DataType.Struct or DataType.Union or DataType.Array;

    /// <summary>完整路径，例如 g_motorPid.kp。</summary>
    public string FullPath => Parent == null
        ? Name
        : Name.StartsWith("[") ? $"{Parent.FullPath}{Name}" : $"{Parent.FullPath}.{Name}";

    public IEnumerable<WatchSymbol> Flatten()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var item in child.Flatten())
                yield return item;
        }
    }

    public WatchSymbol CloneShallow()
    {
        var clone = (WatchSymbol)MemberwiseClone();
        clone.Parent = null;
        clone.Children = new List<WatchSymbol>();
        return clone;
    }
}

/// <summary>RTT 日志级别。</summary>
public enum RttLevel
{
    Trace = 0,
    Info,
    Warn,
    Error,
}

/// <summary>一行 RTT 日志（已完成换行切分）。</summary>
public sealed class RttLine
{
    public long TimestampMs { get; init; }
    public int Channel { get; init; }
    public string Text { get; init; } = string.Empty;
    public RttLevel Level { get; set; } = RttLevel.Info;
    public string RawText { get; init; } = string.Empty;
    public Dictionary<string, string> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string TimeText => DateTimeOffset.FromUnixTimeMilliseconds(TimestampMs).ToLocalTime().ToString("HH:mm:ss.fff");
}

/// <summary>J-Link 接口类型。</summary>
public enum JLinkInterface
{
    SWD = 0,
    JTAG = 1,
}

/// <summary>
/// 需要人工介入时向上位机界面推送的提示。
/// Skill 在必须人工操作（按复位、重新上电、检查接线等）时使用，避免让用户去翻日志。
/// </summary>
public sealed class UiPromptRequest
{
    public string Title { get; init; } = "需要人工操作";

    public string Message { get; init; } = string.Empty;

    /// <summary>info / warn / error，仅影响图标与配色。</summary>
    public string Level { get; init; } = "info";

    /// <summary>true：等待用户点确认后返回；false：只提示不等待。</summary>
    public bool WaitForConfirm { get; init; } = true;

    /// <summary>等待用户确认的最长时间（毫秒），超时按未确认处理。</summary>
    public int TimeoutMs { get; init; } = 60_000;

    /// <summary>来源标记（skill / ui / system），写入审计。</summary>
    public string Source { get; init; } = "skill";

    /// <summary>
    /// 可选项：非空时提示框显示为列表，用户点选其中一项。
    /// 例如 ["已重新上电，继续", "改用 AutoScan 扫内存", "跳过 RTT，只调变量"]。
    /// </summary>
    public List<string> Options { get; init; } = new();
}

/// <summary>界面提示的执行结果。</summary>
public sealed class UiPromptResult
{
    public static readonly UiPromptResult NotDelivered = new() { Delivered = false };

    /// <summary>是否成功显示到界面。</summary>
    public bool Delivered { get; init; }

    /// <summary>用户是否确认（选了任意选项 / 点了确定）。</summary>
    public bool Confirmed { get; init; }

    /// <summary>用户选中的选项文本；无选项列表时返回「确定」或 null。</summary>
    public string? Selected { get; init; }
}

/// <summary>RTT 控制块寻址策略。</summary>
public enum RttSearchMode
{
    AutoFromElf = 0,
    AutoScan,
    ManualAddress,
}

/// <summary>设备连接状态。</summary>
public enum ConnectionState
{
    Disconnected = 0,
    Connecting,
    Connected,
    Faulted,
}

/// <summary>会话配置（既可由界面设置，也可由 Skill REST 接口设置）。</summary>
public sealed class DeviceSessionConfig
{
    public string? SerialNumber { get; set; }
    public string Device { get; set; } = "STM32F407VG";
    public JLinkInterface Interface { get; set; } = JLinkInterface.SWD;
    public int SpeedKhz { get; set; } = 4000;
    public string? ElfPath { get; set; }
    public string? SvdPath { get; set; }
    public RttSearchMode RttSearchMode { get; set; } = RttSearchMode.AutoFromElf;
    public uint? RttControlBlockAddress { get; set; }
    public bool AutoConnect { get; set; }

    public DeviceSessionConfig Clone() => (DeviceSessionConfig)MemberwiseClone();
}

/// <summary>某个变量的实时采样值。</summary>
public sealed class VariableSample
{
    public string Name { get; init; } = string.Empty;
    public uint Address { get; init; }
    public DataType Type { get; init; }
    public object? Value { get; init; }
    public string ValueText { get; init; } = string.Empty;
    public long TimestampMs { get; init; }
    public bool FromSkill { get; init; }
}

/// <summary>时序块采集结果。</summary>
public sealed class TimeSeriesResult
{
    public string[] Names { get; init; } = Array.Empty<string>();
    public int SampleRateHz { get; init; }
    public int DurationMs { get; init; }
    public long StartTimestampMs { get; init; }
    public long EndTimestampMs { get; set; }
    public List<TimeSeriesFrame> Frames { get; init; } = new();
    public Dictionary<string, TimeSeriesStatistics> Statistics { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class TimeSeriesFrame
{
    public long TimestampMs { get; init; }
    public Dictionary<string, double> Values { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class TimeSeriesStatistics
{
    public double Min { get; set; }
    public double Max { get; set; }
    public double Mean { get; set; }
    public double StdDev { get; set; }
    public double PeakToPeak => Max - Min;
    public double Overshoot { get; set; }
    public double SteadyStateError { get; set; }
    public double FinalValue { get; set; }
    public int SampleCount { get; set; }
}

/// <summary>Skill 网关调用审计记录。</summary>
public sealed class SkillAuditEntry
{
    public long TimestampMs { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public string Source { get; init; } = "unknown";
    public string Method { get; init; } = "GET";
    public string Path { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public bool Success { get; init; } = true;
    public double ElapsedMs { get; init; }
    public string TimeText => DateTimeOffset.FromUnixTimeMilliseconds(TimestampMs).ToLocalTime().ToString("HH:mm:ss");
}

/// <summary>Skill 写保护规则：允许外部 AI 修改的变量及其安全范围。</summary>
public sealed class WriteGuard
{
    public string Pattern { get; set; } = "*";
    public double? Min { get; set; }
    public double? Max { get; set; }
    public bool AllowWrite { get; set; } = true;

    public bool IsInRange(double value) =>
        (Min is null || value >= Min.Value) && (Max is null || value <= Max.Value);
}
