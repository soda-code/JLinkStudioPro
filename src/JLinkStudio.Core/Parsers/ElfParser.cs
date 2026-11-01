using System.Text;
using JLinkStudio.Core.Models;

namespace JLinkStudio.Core.Parsers;

/// <summary>极简 ELF 读取器：解析节区表与符号表，不依赖第三方库。</summary>
public sealed class ElfFile
{
    public sealed class Section
    {
        public string Name { get; init; } = string.Empty;
        public uint Type { get; init; }
        public ulong Flags { get; init; }
        public ulong Address { get; init; }
        public ulong Offset { get; init; }
        public ulong Size { get; init; }
        public uint Link { get; init; }
        public ulong EntrySize { get; init; }
        public byte[] Data { get; init; } = Array.Empty<byte>();
    }

    public sealed class SymbolEntry
    {
        public string Name { get; init; } = string.Empty;
        public ulong Value { get; init; }
        public ulong Size { get; init; }
        public byte Info { get; init; }
        public ushort SectionIndex { get; init; }
        public int Type => Info & 0x0F;
        public int Bind => Info >> 4;
    }

    private readonly byte[] _data;
    private readonly List<Section> _sections = new();

    public bool Is64Bit { get; }
    public bool IsLittleEndian { get; }
    public string MachineText { get; }
    public IReadOnlyList<SymbolEntry> Symbols { get; private set; } = Array.Empty<SymbolEntry>();

    public ElfFile(string path)
    {
        _data = File.ReadAllBytes(path);
        if (_data.Length < 52 || _data[0] != 0x7F || _data[1] != (byte)'E' || _data[2] != (byte)'L' || _data[3] != (byte)'F')
            throw new InvalidDataException("不是有效的 ELF 文件。");

        Is64Bit = _data[4] == 2;
        IsLittleEndian = _data[5] != 2;
        var machine = ReadUInt16(18);
        MachineText = machine switch
        {
            40 => "ARM",
            183 => "AArch64",
            243 => "RISC-V",
            3 => "x86",
            62 => "x86-64",
            _ => $"Machine#{machine}",
        };

        ReadSections();
        ReadSymbols();
    }

    public Section? GetSection(string name) =>
        _sections.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));

    private void ReadSections()
    {
        ulong shoff = Is64Bit ? ReadUInt64(40) : ReadUInt32(32);
        int shentsize = ReadUInt16(Is64Bit ? 58 : 46);
        int shnum = ReadUInt16(Is64Bit ? 60 : 48);
        int shstrndx = ReadUInt16(Is64Bit ? 62 : 50);

        if (shoff == 0 || shentsize == 0) return;

        var headers = new List<(uint NameOffset, uint Type, ulong Flags, ulong Addr, ulong Offset, ulong Size, uint Link, ulong EntrySize)>();
        for (int i = 0; i < shnum; i++)
        {
            int o = (int)shoff + i * shentsize;
            if (o + shentsize > _data.Length) break;
            if (Is64Bit)
            {
                headers.Add((
                    ReadUInt32(o), ReadUInt32(o + 4), ReadUInt64(o + 8), ReadUInt64(o + 16),
                    ReadUInt64(o + 24), ReadUInt64(o + 32), ReadUInt32(o + 40), ReadUInt64(o + 56)));
            }
            else
            {
                headers.Add((
                    ReadUInt32(o), ReadUInt32(o + 4), ReadUInt32(o + 8), ReadUInt32(o + 12),
                    ReadUInt32(o + 16), ReadUInt32(o + 20), ReadUInt32(o + 24), ReadUInt32(o + 36)));
            }
        }

        if (headers.Count == 0) return;
        byte[]? shstrtab = null;
        if (shstrndx < headers.Count)
        {
            var h = headers[shstrndx];
            shstrtab = Slice((int)h.Offset, (int)h.Size);
        }

        foreach (var h in headers)
        {
            var name = shstrtab == null ? string.Empty : ReadCString(shstrtab, (int)h.NameOffset);
            _sections.Add(new Section
            {
                Name = name,
                Type = h.Type,
                Flags = h.Flags,
                Address = h.Addr,
                Offset = h.Offset,
                Size = h.Size,
                Link = h.Link,
                EntrySize = h.EntrySize,
                Data = Slice((int)h.Offset, (int)Math.Min(h.Size, (ulong)Math.Max(0, _data.Length - (int)h.Offset))),
            });
        }
    }

    private void ReadSymbols()
    {
        var symtab = GetSection(".symtab");
        if (symtab == null) return;

        var strtab = symtab.Link < _sections.Count ? _sections[(int)symtab.Link] : null;
        var list = new List<SymbolEntry>();
        int entrySize = Is64Bit ? 24 : 16;
        if (symtab.EntrySize > 0) entrySize = (int)symtab.EntrySize;

        for (int o = 0; o + entrySize <= symtab.Data.Length; o += entrySize)
        {
            uint nameOffset = ReadUInt32(symtab.Data, o);
            ulong value, size;
            byte info;
            ushort shndx;
            if (Is64Bit)
            {
                info = symtab.Data[o + 4];
                shndx = ReadUInt16(symtab.Data, o + 6);
                value = ReadUInt64(symtab.Data, o + 8);
                size = ReadUInt64(symtab.Data, o + 16);
            }
            else
            {
                value = ReadUInt32(symtab.Data, o + 4);
                size = ReadUInt32(symtab.Data, o + 8);
                info = symtab.Data[o + 12];
                shndx = ReadUInt16(symtab.Data, o + 14);
            }

            var name = strtab == null ? string.Empty : ReadCString(strtab.Data, (int)nameOffset);
            if (string.IsNullOrEmpty(name)) continue;
            list.Add(new SymbolEntry
            {
                Name = name,
                Value = value,
                Size = size,
                Info = info,
                SectionIndex = shndx,
            });
        }

        Symbols = list;
    }

    public static string ReadCString(byte[] buffer, int offset)
    {
        if (offset < 0 || offset >= buffer.Length) return string.Empty;
        int end = offset;
        while (end < buffer.Length && buffer[end] != 0) end++;
        return Encoding.UTF8.GetString(buffer, offset, end - offset);
    }

    private byte[] Slice(int offset, int size)
    {
        if (offset < 0 || offset >= _data.Length || size <= 0) return Array.Empty<byte>();
        size = Math.Min(size, _data.Length - offset);
        var result = new byte[size];
        Buffer.BlockCopy(_data, offset, result, 0, size);
        return result;
    }

    private ushort ReadUInt16(int offset) => ReadUInt16(_data, offset);

    private uint ReadUInt32(int offset) => ReadUInt32(_data, offset);

    private ulong ReadUInt64(int offset) => ReadUInt64(_data, offset);

    private ushort ReadUInt16(byte[] buffer, int offset)
    {
        if (offset + 2 > buffer.Length) return 0;
        return IsLittleEndian
            ? (ushort)(buffer[offset] | buffer[offset + 1] << 8)
            : (ushort)(buffer[offset] << 8 | buffer[offset + 1]);
    }

    private uint ReadUInt32(byte[] buffer, int offset)
    {
        if (offset + 4 > buffer.Length) return 0;
        return IsLittleEndian
            ? (uint)(buffer[offset] | buffer[offset + 1] << 8 | buffer[offset + 2] << 16 | buffer[offset + 3] << 24)
            : (uint)(buffer[offset] << 24 | buffer[offset + 1] << 16 | buffer[offset + 2] << 8 | buffer[offset + 3]);
    }

    private ulong ReadUInt64(byte[] buffer, int offset)
    {
        if (offset + 8 > buffer.Length) return 0;
        uint lo = ReadUInt32(buffer, offset);
        uint hi = ReadUInt32(buffer, offset + 4);
        return IsLittleEndian ? lo | (ulong)hi << 32 : (ulong)lo << 32 | hi;
    }
}

/// <summary>ELF/DWARF 符号解析器：输出可监控的全局变量树。</summary>
public sealed class ElfParser
{
    public sealed class ParseResult
    {
        public string FilePath { get; init; } = string.Empty;
        public List<WatchSymbol> Symbols { get; } = new();
        public uint? RttControlBlockAddress { get; set; }
        public bool DwarfLoaded { get; set; }
        public string? Warning { get; set; }

        public WatchSymbol? Find(string path)
        {
            var rootName = path.Split('.', '[')[0];
            foreach (var symbol in Symbols)
            {
                if (!string.Equals(symbol.Name, rootName, StringComparison.Ordinal)) continue;
                var current = symbol;
                foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries).Skip(1))
                {
                    var child = current.Children.FirstOrDefault(c =>
                        string.Equals(c.Name, segment, StringComparison.Ordinal) ||
                        string.Equals(c.Name.Trim('[', ']'), segment.Trim('[', ']'), StringComparison.Ordinal));
                    if (child == null) return null;
                    current = child;
                }
                return current;
            }
            return null;
        }
    }

    public ParseResult Parse(string elfPath)
    {
        var elf = new ElfFile(elfPath);
        var result = new ParseResult { FilePath = elfPath };
        var dwarf = DwarfParser.Parse(elf);

        var dwarfByAddress = new Dictionary<uint, DwarfParser.DwarfVariable>();
        foreach (var variable in dwarf.Variables)
            dwarfByAddress[variable.Address] = variable;

        foreach (var symbol in elf.Symbols)
        {
            if (symbol.Type != 1 /* STT_OBJECT */) continue;
            if (symbol.Bind == 0 /* LOCAL */ && !string.Equals(symbol.Name, "_SEGGER_RTT", StringComparison.Ordinal)) continue;
            if (symbol.Value == 0 || symbol.Value > uint.MaxValue) continue;

            var address = (uint)symbol.Value;
            if (string.Equals(symbol.Name, "_SEGGER_RTT", StringComparison.Ordinal))
                result.RttControlBlockAddress = address;

            WatchSymbol watch;
            if (dwarfByAddress.TryGetValue(address, out var dwarfVar) && dwarfVar.Type != null)
            {
                watch = DwarfParser.BuildWatchSymbol(symbol.Name, address, dwarfVar.Type);
                result.DwarfLoaded = true;
            }
            else
            {
                watch = new WatchSymbol
                {
                    Name = symbol.Name,
                    Address = address,
                    Type = symbol.Size == 1 ? DataType.UInt8
                        : symbol.Size == 2 ? DataType.UInt16
                        : symbol.Size == 4 ? DataType.UInt32
                        : symbol.Size == 8 ? DataType.UInt64
                        : DataType.Unknown,
                    ByteSize = (int)symbol.Size,
                };
            }

            if (result.Symbols.All(s => s.Name != watch.Name))
                result.Symbols.Add(watch);
        }

        result.Symbols.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        if (dwarf.Warning != null) result.Warning = dwarf.Warning;
        return result;
    }
}
