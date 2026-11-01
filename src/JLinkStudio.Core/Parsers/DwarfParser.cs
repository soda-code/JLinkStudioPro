using System.Text;
using JLinkStudio.Core.Models;

namespace JLinkStudio.Core.Parsers;

/// <summary>
/// 轻量 DWARF 2~5 解析器：只提取“全局/静态变量 -> 复合类型 -> 成员偏移”所需信息，
/// 足以支撑结构体、数组、枚举在监控表格中的树状展开。任何解析异常都会降级为 symtab 扁平符号。
/// </summary>
public sealed class DwarfParser
{
    public sealed class DwarfType
    {
        public DataType Kind { get; set; } = DataType.Unknown;
        public int ByteSize { get; set; }
        public string? Name { get; set; }
        public List<DwarfMember> Members { get; } = new();
        public DwarfType? ElementType { get; set; }
        public int ElementCount { get; set; }
        public override string ToString() => $"{Kind}:{Name}({ByteSize})";
    }

    public sealed class DwarfMember
    {
        public string Name { get; init; } = string.Empty;
        public int Offset { get; init; }
        public DwarfType? Type { get; init; }
    }

    public sealed class DwarfVariable
    {
        public string Name { get; init; } = string.Empty;
        public uint Address { get; init; }
        public DwarfType? Type { get; init; }
    }

    public sealed class DwarfResult
    {
        public List<DwarfVariable> Variables { get; } = new();
        public string? Warning { get; set; }
    }

    // DWARF 常量表（只保留本解析器使用的部分）
    private const ushort DW_TAG_array_type = 0x01;
    private const ushort DW_TAG_enumeration_type = 0x04;
    private const ushort DW_TAG_formal_parameter = 0x05;
    private const ushort DW_TAG_member = 0x0D;
    private const ushort DW_TAG_pointer_type = 0x0F;
    private const ushort DW_TAG_structure_type = 0x13;
    private const ushort DW_TAG_subroutine_type = 0x15;
    private const ushort DW_TAG_typedef = 0x16;
    private const ushort DW_TAG_union_type = 0x17;
    private const ushort DW_TAG_inheritance = 0x1C;
    private const ushort DW_TAG_subrange_type = 0x21;
    private const ushort DW_TAG_base_type = 0x24;
    private const ushort DW_TAG_const_type = 0x26;
    private const ushort DW_TAG_enumerator = 0x28;
    private const ushort DW_TAG_variable = 0x34;
    private const ushort DW_TAG_volatile_type = 0x35;
    private const ushort DW_TAG_restrict_type = 0x37;
    private const ushort DW_TAG_atomic_type = 0x47;

    private const ushort DW_AT_location = 0x02;
    private const ushort DW_AT_name = 0x03;
    private const ushort DW_AT_byte_size = 0x0B;
    private const ushort DW_AT_data_member_location = 0x38;
    private const ushort DW_AT_type = 0x49;
    private const ushort DW_AT_upper_bound = 0x2F;
    private const ushort DW_AT_lower_bound = 0x22;
    private const ushort DW_AT_count = 0x37;
    private const ushort DW_AT_declaration = 0x3C;
    private const ushort DW_AT_encoding = 0x3E;
    private const ushort DW_AT_const_value = 0x1C;

    private readonly record struct AbbrevDecl(ushort Tag, bool HasChildren, List<(ushort Attr, ushort Form, long ImplicitConst)> Specs);

    private sealed class Die
    {
        public long Offset { get; init; }
        public ushort Tag { get; init; }
        public Dictionary<ushort, object?> Attrs { get; } = new();
        public List<Die> Children { get; } = new();
        public Die? Parent { get; set; }
    }

    private sealed class Reader
    {
        private readonly byte[] _buffer;
        public int Position { get; set; }
        public Reader(byte[] buffer) => _buffer = buffer;
        public bool Eof => Position >= _buffer.Length;
        public int End => _buffer.Length;

        public byte U8() => Position < _buffer.Length ? _buffer[Position++] : (byte)0;

        public ushort U16()
        {
            if (Position + 2 > _buffer.Length) { Position = _buffer.Length; return 0; }
            ushort v = (ushort)(_buffer[Position] | _buffer[Position + 1] << 8);
            Position += 2;
            return v;
        }

        public uint U32()
        {
            if (Position + 4 > _buffer.Length) { Position = _buffer.Length; return 0; }
            uint v = (uint)(_buffer[Position] | _buffer[Position + 1] << 8 | _buffer[Position + 2] << 16 | _buffer[Position + 3] << 24);
            Position += 4;
            return v;
        }

        public ulong U64()
        {
            ulong lo = U32();
            ulong hi = U32();
            return lo | hi << 32;
        }

        public ulong Uleb()
        {
            ulong result = 0;
            int shift = 0;
            while (Position < _buffer.Length && shift < 64)
            {
                byte b = _buffer[Position++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
            }
            return result;
        }

        public long Sleb()
        {
            long result = 0;
            int shift = 0;
            byte b = 0;
            while (Position < _buffer.Length && shift < 64)
            {
                b = _buffer[Position++];
                result |= (long)(b & 0x7F) << shift;
                shift += 7;
                if ((b & 0x80) == 0) break;
            }
            if (shift < 64 && (b & 0x40) != 0) result |= -1L << shift;
            return result;
        }

        public byte[] Bytes(int count)
        {
            count = Math.Max(0, Math.Min(count, _buffer.Length - Position));
            var slice = new byte[count];
            Buffer.BlockCopy(_buffer, Position, slice, 0, count);
            Position += count;
            return slice;
        }

        public string CStringAt(int offset)
        {
            if (offset < 0 || offset >= _buffer.Length) return string.Empty;
            int end = offset;
            while (end < _buffer.Length && _buffer[end] != 0) end++;
            return Encoding.UTF8.GetString(_buffer, offset, end - offset);
        }
    }

    public static DwarfResult Parse(ElfFile elf)
    {
        var result = new DwarfResult();
        try
        {
            var info = elf.GetSection(".debug_info");
            var abbrev = elf.GetSection(".debug_abbrev");
            var str = elf.GetSection(".debug_str");
            if (info == null || abbrev == null || info.Data.Length == 0) return result;

            var abbrevTable = ParseAbbrev(abbrev.Data);
            var dies = ParseDieTree(info.Data, abbrevTable, str?.Data ?? Array.Empty<byte>(), out var addressSize);
            var typeCache = new Dictionary<long, DwarfType>();
            var dieByOffset = new Dictionary<long, Die>();
            foreach (var die in Flatten(dies)) dieByOffset[die.Offset] = die;

            foreach (var die in Flatten(dies))
            {
                if (die.Tag != DW_TAG_variable) continue;
                if (die.Attrs.TryGetValue(DW_AT_declaration, out var decl) && ToInt(decl) == 1) continue;
                if (!die.Attrs.TryGetValue(DW_AT_location, out var loc) || loc is not byte[] expr) continue;
                if (expr.Length < 1 + addressSize || expr[0] != 0x03) continue; // 仅支持 DW_OP_addr（静态地址）

                ulong addr = 0;
                for (int i = 0; i < addressSize; i++) addr |= (ulong)expr[1 + i] << (8 * i);
                if (addr == 0 || addr > uint.MaxValue) continue;

                var name = die.Attrs.TryGetValue(DW_AT_name, out var n) ? n as string : null;
                if (string.IsNullOrEmpty(name)) continue;

                DwarfType? type = null;
                if (die.Attrs.TryGetValue(DW_AT_type, out var typeAttr) && ToInt(typeAttr) is long typeOffset)
                {
                    if (dieByOffset.TryGetValue(typeOffset, out var typeDie) || typeOffset == 0)
                        type = BuildType(typeDie!, typeCache, dieByOffset, addressSize, 0);
                }

                result.Variables.Add(new DwarfVariable { Name = name!, Address = (uint)addr, Type = type });
            }
        }
        catch (Exception ex)
        {
            result.Variables.Clear();
            result.Warning = $"DWARF 解析降级（仅使用符号表）: {ex.Message}";
        }

        return result;
    }

    private static IEnumerable<Die> Flatten(IEnumerable<Die> dies)
    {
        foreach (var die in dies)
        {
            yield return die;
            foreach (var child in Flatten(die.Children)) yield return child;
        }
    }

    private static Dictionary<ulong, List<AbbrevDecl>> ParseAbbrev(byte[] data)
    {
        var table = new Dictionary<ulong, List<AbbrevDecl>>();
        var reader = new Reader(data);
        while (!reader.Eof && reader.Position < data.Length)
        {
            long tableStart = reader.Position;
            var decls = new List<AbbrevDecl>();
            while (!reader.Eof)
            {
                ulong code = reader.Uleb();
                if (code == 0) break;
                ushort tag = (ushort)reader.Uleb();
                bool hasChildren = reader.U8() != 0;
                var specs = new List<(ushort, ushort, long)>();
                while (true)
                {
                    ushort attr = (ushort)reader.Uleb();
                    ushort form = (ushort)reader.Uleb();
                    if (attr == 0 && form == 0) break;
                    long implicitConst = 0;
                    if (form == 0x21) implicitConst = reader.Sleb();
                    specs.Add((attr, form, implicitConst));
                }
                decls.Add(new AbbrevDecl(tag, hasChildren, specs));
            }

            if (decls.Count == 0) break;
            table[(ulong)tableStart] = decls;
        }
        return table;
    }

    private static bool TryGetDecls(Dictionary<ulong, List<AbbrevDecl>> table, ulong abbrevOffset, out List<AbbrevDecl> decls)
    {
        if (table.TryGetValue(abbrevOffset, out decls!)) return true;
        ulong best = 0;
        bool found = false;
        foreach (var key in table.Keys)
        {
            if (key <= abbrevOffset && key >= best)
            {
                best = key;
                found = true;
            }
        }
        if (!found)
        {
            decls = new List<AbbrevDecl>();
            return false;
        }
        return table.TryGetValue(best, out decls!);
    }

    private static List<Die> ParseDieTree(byte[] info, Dictionary<ulong, List<AbbrevDecl>> abbrevTable, byte[] strSection, out int addressSize)
    {
        var roots = new List<Die>();
        var reader = new Reader(info);
        addressSize = 4;

        while (reader.Position + 12 < info.Length)
        {
            long unitStart = reader.Position;
            ulong length = reader.U32();
            bool dwarf64 = false;
            if (length == 0xFFFFFFFF)
            {
                dwarf64 = true;
                length = reader.U64();
            }
            if (length == 0 || unitStart + 4 + (long)length > info.Length) break;
            long unitEnd = unitStart + (dwarf64 ? 12 : 4) + (long)length;

            ushort version = reader.U16();
            ulong abbrevOffset;
            if (version >= 5)
            {
                byte unitType = reader.U8();
                addressSize = reader.U8();
                abbrevOffset = dwarf64 ? reader.U64() : reader.U32();
                if (unitType is not (0x01 or 0x03))
                {
                    reader.Position = (int)unitEnd;
                    continue;
                }
            }
            else
            {
                abbrevOffset = dwarf64 ? reader.U64() : reader.U32();
                addressSize = reader.U8();
            }

            if (!TryGetDecls(abbrevTable, abbrevOffset, out var decls) || decls.Count == 0)
            {
                reader.Position = (int)unitEnd;
                continue;
            }

            var stack = new Stack<Die>();
            while (reader.Position < unitEnd && reader.Position < info.Length)
            {
                long dieOffset = reader.Position;
                ulong code = reader.Uleb();
                if (code == 0)
                {
                    if (stack.Count > 0) stack.Pop();
                    if (stack.Count == 0) break;
                    continue;
                }
                if (code - 1 >= (ulong)decls.Count) break;
                var decl = decls[(int)(code - 1)];

                var die = new Die { Offset = dieOffset, Tag = decl.Tag };
                foreach (var (attr, form, implicitConst) in decl.Specs)
                {
                    if (attr == 0) continue;
                    die.Attrs[attr] = ReadForm(reader, form, implicitConst, addressSize, strSection, unitStart);
                }

                if (stack.Count > 0)
                {
                    die.Parent = stack.Peek();
                    die.Parent.Children.Add(die);
                }
                else
                {
                    roots.Add(die);
                }

                if (decl.HasChildren) stack.Push(die);
            }

            reader.Position = (int)unitEnd;
        }

        return roots;
    }

    private static object? ReadForm(Reader reader, ushort form, long implicitConst, int addressSize, byte[] strSection, long cuStart)
    {
        switch (form)
        {
            case 0x01: // addr
                return addressSize switch
                {
                    8 => reader.U64(),
                    2 => reader.U16(),
                    _ => reader.U32(),
                };
            case 0x0B: return (ulong)reader.U8();          // data1
            case 0x05: return (ulong)reader.U16();         // data2
            case 0x06: return (ulong)reader.U32();         // data4
            case 0x07: return reader.U64();                // data8
            case 0x1E: return reader.Bytes(16);            // data16
            case 0x0D: return (ulong)reader.Sleb();        // sdata
            case 0x0F: return reader.Uleb();               // udata
            case 0x08: return ReadInlineString(reader);    // string
            case 0x0E: return ElfFile.ReadCString(strSection, (int)reader.U32()); // strp
            case 0x1F: return ElfFile.ReadCString(Array.Empty<byte>(), 0);          // line_strp（不支持）
            case 0x0A: return reader.Bytes(reader.U8());   // block1
            case 0x03: return reader.Bytes(reader.U16());  // block2
            case 0x04: return reader.Bytes((int)reader.U32()); // block4
            case 0x09: return reader.Bytes((int)reader.Uleb()); // block
            case 0x18: return reader.Bytes((int)reader.Uleb()); // exprloc
            case 0x0C: return (ulong)reader.U8();          // flag
            case 0x19: return 1UL;                         // flag_present
            case 0x11: return cuStart + (long)reader.U8();   // ref1（CU 相对）
            case 0x12: return cuStart + (long)reader.U16();  // ref2
            case 0x13: return cuStart + (long)reader.U32();  // ref4
            case 0x14: return cuStart + (long)reader.U64();  // ref8
            case 0x15: return cuStart + (long)reader.Uleb(); // ref_udata
            case 0x10: return (long)(addressSize == 8 ? reader.U64() : reader.U32()); // ref_addr
            case 0x17: return (long)(addressSize == 8 ? reader.U64() : reader.U32()); // sec_offset
            case 0x21: return (ulong)implicitConst;        // implicit_const
            case 0x1A: return reader.Uleb();               // strx（无 .debug_str_offsets 支持）
            case 0x1B: return reader.Uleb();               // addrx
            case 0x25: return (ulong)reader.U8();          // strx1
            case 0x26: return (ulong)reader.U16();         // strx2
            case 0x27: return (ulong)reader.U32();         // strx3
            case 0x28: return reader.U64();                // strx4
            case 0x29: return (ulong)reader.U8();          // addrx1
            case 0x2A: return (ulong)reader.U16();         // addrx2
            case 0x2B: return (ulong)reader.U32();         // addrx3
            case 0x2C: return reader.U64();                // addrx4
            default:
                return null;
        }
    }

    private static string ReadInlineString(Reader reader)
    {
        var bytes = new List<byte>();
        while (!reader.Eof)
        {
            byte b = reader.U8();
            if (b == 0) break;
            bytes.Add(b);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static DwarfType? BuildType(Die? die, Dictionary<long, DwarfType> cache,
        Dictionary<long, Die> dieByOffset, int addressSize, int depth)
    {
        if (die == null || depth > 8) return null;
        if (cache.TryGetValue(die.Offset, out var cached)) return cached;

        var type = new DwarfType();
        type.Name = die.Attrs.TryGetValue(DW_AT_name, out var n) ? n as string : null;
        int byteSize = (int)(ToInt(die.Attrs.TryGetValue(DW_AT_byte_size, out var bs) ? bs : null) ?? 0);

        switch (die.Tag)
        {
            case DW_TAG_base_type:
                type.Kind = EncodingToDataType((int)(ToInt(die.Attrs.GetValueOrDefault(DW_AT_encoding)) ?? 0));
                type.ByteSize = byteSize > 0 ? byteSize : 4;
                break;

            case DW_TAG_pointer_type:
                type.Kind = DataType.Pointer;
                type.ByteSize = byteSize > 0 ? byteSize : addressSize;
                break;

            case DW_TAG_enumeration_type:
                type.Kind = DataType.Enum;
                type.ByteSize = byteSize > 0 ? byteSize : 4;
                break;

            case DW_TAG_structure_type:
            case DW_TAG_union_type:
                type.Kind = die.Tag == DW_TAG_structure_type ? DataType.Struct : DataType.Union;
                type.ByteSize = byteSize;
                cache[die.Offset] = type; // 先入缓存，防止递归类型死循环
                foreach (var child in die.Children)
                {
                    if (child.Tag is not (DW_TAG_member or DW_TAG_inheritance)) continue;
                    var memberName = child.Attrs.TryGetValue(DW_AT_name, out var mn) ? mn as string : null;
                    if (string.IsNullOrEmpty(memberName)) continue;
                    int offset = (int)(ToMemberOffset(child.Attrs.TryGetValue(DW_AT_data_member_location, out var ml) ? ml : null) ?? 0);
                    var memberType = child.Attrs.TryGetValue(DW_AT_type, out var mt) && ToInt(mt) is long memberTypeOffset
                        ? BuildType(dieByOffset.GetValueOrDefault(memberTypeOffset), cache, dieByOffset, addressSize, depth + 1)
                        : null;
                    type.Members.Add(new DwarfMember { Name = memberName!, Offset = offset, Type = memberType });
                    if (type.ByteSize == 0 && memberType != null)
                        type.ByteSize = Math.Max(type.ByteSize, offset + memberType.ByteSize);
                }
                break;

            case DW_TAG_array_type:
                type.Kind = DataType.Array;
                DwarfType? elementType = die.Attrs.TryGetValue(DW_AT_type, out var et) && ToInt(et) is long elementOffset
                    ? BuildType(dieByOffset.GetValueOrDefault(elementOffset), cache, dieByOffset, addressSize, depth + 1)
                    : null;
                type.ElementType = elementType;
                var subrange = die.Children.FirstOrDefault(c => c.Tag == DW_TAG_subrange_type);
                int count = 0;
                if (subrange != null)
                {
                    if (subrange.Attrs.TryGetValue(DW_AT_count, out var cnt)) count = (int)(ToInt(cnt) ?? 0);
                    else if (subrange.Attrs.TryGetValue(DW_AT_upper_bound, out var ub)) count = (int)(ToInt(ub) ?? 0) + 1;
                }
                type.ElementCount = Math.Max(0, count);
                type.ByteSize = (elementType?.ByteSize ?? 1) * type.ElementCount;
                break;

            case DW_TAG_typedef:
            case DW_TAG_const_type:
            case DW_TAG_volatile_type:
            case DW_TAG_restrict_type:
            case DW_TAG_atomic_type:
                DwarfType? underlying = die.Attrs.TryGetValue(DW_AT_type, out var ut) && ToInt(ut) is long underlyingOffset
                    ? BuildType(dieByOffset.GetValueOrDefault(underlyingOffset), cache, dieByOffset, addressSize, depth + 1)
                    : null;
                if (underlying != null)
                {
                    type.Kind = underlying.Kind;
                    type.ByteSize = underlying.ByteSize;
                    type.ElementType = underlying.ElementType;
                    type.ElementCount = underlying.ElementCount;
                    type.Members.AddRange(underlying.Members);
                }
                else
                {
                    type.Kind = DataType.Unknown;
                    type.ByteSize = byteSize;
                }
                break;

            case DW_TAG_subroutine_type:
                type.Kind = DataType.Pointer;
                type.ByteSize = addressSize;
                break;

            default:
                type.Kind = DataType.Unknown;
                type.ByteSize = byteSize;
                break;
        }

        cache[die.Offset] = type;
        return type;
    }

    private static DataType EncodingToDataType(int encoding) => encoding switch
    {
        0x02 => DataType.Bool,       // boolean
        0x06 => DataType.Int8,       // signed_char
        0x08 => DataType.UInt8,      // unsigned_char
        0x04 => DataType.Float,      // float
        0x05 => DataType.Int32,      // signed
        0x07 => DataType.UInt32,     // unsigned
        _ => DataType.Unknown,
    };

    private static long? ToInt(object? value) => value switch
    {
        ulong u => (long)u,
        long l => l,
        uint u32 => (long)u32,
        int i => (long)i,
        _ => null,
    };

    /// <summary>
    /// 解析 DW_AT_data_member_location：常量表单直接取值；
    /// exprloc/block 表单（DWARF4/5 常见）仅支持最普遍的 DW_OP_plus_uconst(0x23) &lt;uleb128&gt; 形态。
    /// </summary>
    private static long? ToMemberOffset(object? value)
    {
        if (value is byte[] expr)
        {
            if (expr.Length >= 2 && expr[0] == 0x23)
                return ReadUleb128(expr, start: 1);
            return null;
        }
        return ToInt(value);
    }

    private static long? ReadUleb128(byte[] bytes, int start)
    {
        ulong result = 0;
        int shift = 0;
        for (int i = start; i < bytes.Length; i++)
        {
            result |= (ulong)(bytes[i] & 0x7F) << shift;
            if ((bytes[i] & 0x80) == 0) return (long)result;
            shift += 7;
            if (shift > 63) return null;
        }
        return null;
    }

    /// <summary>把 DWARF 类型转换位界面可直接读取的 WatchSymbol 树。</summary>
    public static WatchSymbol BuildWatchSymbol(string name, uint address, DwarfType type, int depth = 0)
    {
        var symbol = new WatchSymbol
        {
            Name = name,
            Address = address,
            Type = type.Kind,
            ByteSize = type.ByteSize,
            IsWritable = type.Kind is not (DataType.Struct or DataType.Array or DataType.Union),
        };

        if (depth > 4) return symbol;

        if (type.Kind == DataType.Array && type.ElementType != null)
        {
            int count = Math.Min(type.ElementCount, 256);
            int elementSize = Math.Max(1, type.ElementType.ByteSize);
            for (int i = 0; i < count; i++)
            {
                var child = BuildWatchSymbol($"[{i}]", (uint)(address + i * elementSize), type.ElementType, depth + 1);
                child.OffsetInParent = i * elementSize;
                child.Parent = symbol;
                symbol.Children.Add(child);
            }
            return symbol;
        }

        if (type.Kind is DataType.Struct or DataType.Union)
        {
            foreach (var member in type.Members)
            {
                if (member.Type == null) continue;
                var child = BuildWatchSymbol(member.Name, (uint)(address + member.Offset), member.Type, depth + 1);
                child.OffsetInParent = member.Offset;
                child.Parent = symbol;
                symbol.Children.Add(child);
            }
            return symbol;
        }

        return symbol;
    }
}
