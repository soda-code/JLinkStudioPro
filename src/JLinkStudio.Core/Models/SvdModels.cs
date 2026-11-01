namespace JLinkStudio.Core.Models;

/// <summary>SVD 外设寄存器模型。</summary>
public sealed class SvdModel
{
    public string Name { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string? Description { get; set; }
    public List<SvdPeripheral> Peripherals { get; } = new();

    public SvdPeripheral? FindPeripheral(string name) =>
        Peripherals.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}

public sealed class SvdPeripheral
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? GroupName { get; set; }
    public ulong BaseAddress { get; set; }
    public List<SvdRegister> Registers { get; } = new();

    public SvdRegister? FindRegister(string name) =>
        Registers.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => $"{Name} (0x{BaseAddress:X8})";
}

public sealed class SvdRegister
{
    public string Name { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public ulong Offset { get; set; }
    public ulong BaseAddress { get; set; }
    public int SizeBits { get; set; } = 32;
    public ulong ResetValue { get; set; }
    public string Access { get; set; } = "read-write";
    /// <summary>SVD readAction 属性：clear / set / modify 等，表示读取会带来副作用。</summary>
    public string? ReadAction { get; set; }
    public List<SvdField> Fields { get; } = new();

    public ulong AbsoluteAddress => BaseAddress + Offset;
    public int ByteSize => Math.Max(1, SizeBits / 8);
    public bool HasReadSideEffect => !string.IsNullOrEmpty(ReadAction);
    /// <summary>是否需要 32 位对齐访问（大部分外设寄存器强制对齐）。</summary>
    public bool RequiresAlignedAccess => SizeBits >= 32;
    public string AddressText => $"0x{AbsoluteAddress:X8}";

    public SvdField? FindField(string name) =>
        Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
}

public sealed class SvdField
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int BitOffset { get; set; }
    public int BitWidth { get; set; } = 1;
    public string Access { get; set; } = "read-write";
    public Dictionary<ulong, string> EnumeratedValues { get; } = new();

    public ulong Mask => BitWidth >= 64 ? ulong.MaxValue : ((1UL << BitWidth) - 1) << BitOffset;

    public ulong Extract(ulong registerValue) =>
        BitWidth >= 64 ? registerValue : (registerValue >> BitOffset) & ((1UL << BitWidth) - 1);

    public ulong Insert(ulong registerValue, ulong fieldValue)
    {
        if (BitWidth >= 64) return fieldValue;
        ulong mask = (1UL << BitWidth) - 1;
        return (registerValue & ~(mask << BitOffset)) | ((fieldValue & mask) << BitOffset);
    }

    public string BitsText => BitWidth == 1 ? $"[{BitOffset}]" : $"[{BitOffset + BitWidth - 1}:{BitOffset}]";

    public string? Describe(ulong registerValue)
    {
        ulong v = Extract(registerValue);
        return EnumeratedValues.TryGetValue(v, out var text) ? text : null;
    }
}
