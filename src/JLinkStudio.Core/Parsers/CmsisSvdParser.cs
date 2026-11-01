using System.Globalization;
using System.Xml.Linq;
using JLinkStudio.Core.Models;

namespace JLinkStudio.Core.Parsers;

/// <summary>
/// CMSIS-SVD 文件解析器（LINQ to XML）。
/// 输出 外设 -> 寄存器 -> 位域 三级模型，支持 derivedFrom 继承、dim 数组展开与枚举值解释。
/// </summary>
public sealed class CmsisSvdParser
{
    private static readonly XNamespace Ns = string.Empty;

    public SvdModel Parse(string svdPath)
    {
        if (!File.Exists(svdPath))
            throw new FileNotFoundException($"SVD 文件不存在: {svdPath}");

        var doc = XDocument.Load(svdPath);
        var root = doc.Root ?? throw new InvalidDataException("SVD 文件格式错误：缺少根节点 <device>。");

        var model = new SvdModel
        {
            Name = Str(root.Element("name")) ?? Path.GetFileNameWithoutExtension(svdPath),
            Version = Str(root.Element("version")),
            Description = Str(root.Element("description")),
        };

        var peripheralsNode = root.Element("peripherals")
            ?? throw new InvalidDataException("SVD 文件缺少 <peripherals> 节点。");

        var rawPeripherals = peripheralsNode.Elements("peripheral").ToList();
        var byName = rawPeripherals.ToDictionary(
            e => Str(e.Element("name")) ?? Guid.NewGuid().ToString(),
            e => e,
            StringComparer.OrdinalIgnoreCase);

        foreach (var element in rawPeripherals)
        {
            var peripheral = ParsePeripheral(element, byName, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            if (peripheral != null && model.Peripherals.All(p => !string.Equals(p.Name, peripheral.Name, StringComparison.OrdinalIgnoreCase)))
                model.Peripherals.Add(peripheral);
        }

        model.Peripherals.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return model;
    }

    private SvdPeripheral? ParsePeripheral(XElement element, Dictionary<string, XElement> byName, HashSet<string> visited)
    {
        var name = Str(element.Element("name"));
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (!visited.Add(name)) return null;

        XElement merged = element;
        var derived = (string?)element.Attribute("derivedFrom");
        if (!string.IsNullOrEmpty(derived) && byName.TryGetValue(derived, out var baseElement))
        {
            // 自身元素在前、基元素在后：同名子节点（registers/description 等）取第一个即自身优先，
            // 缺失的子节点（如基类独有的 registers）仍可继承。
            merged = new XElement(element.Name, element.Elements(), baseElement.Elements());
        }

        var peripheral = new SvdPeripheral
        {
            Name = name,
            Description = Str(Merged(merged, element, "description")),
            GroupName = Str(Merged(merged, element, "groupName")),
            BaseAddress = ParseULong(Str(Merged(merged, element, "baseAddress"))) ?? 0,
        };

        var registersNode = merged.Element("registers");
        if (registersNode != null)
        {
            foreach (var registerElement in ExpandDim(registersNode.Elements("register")))
            {
                var register = ParseRegister(registerElement, peripheral);
                if (register != null) peripheral.Registers.Add(register);
            }
        }

        return peripheral;
    }

    private SvdRegister? ParseRegister(XElement element, SvdPeripheral peripheral)
    {
        var name = Str(element.Element("name"));
        if (string.IsNullOrWhiteSpace(name)) return null;

        var register = new SvdRegister
        {
            Name = name,
            DisplayName = Str(element.Element("displayName")),
            Description = Str(element.Element("description")),
            Offset = ParseULong(Str(element.Element("addressOffset"))) ?? 0,
            BaseAddress = peripheral.BaseAddress,
            SizeBits = (int)(ParseULong(Str(element.Element("size"))) ?? 32UL),
            ResetValue = ParseULong(Str(element.Element("resetValue"))) ?? 0,
            Access = Str(element.Element("access")) ?? "read-write",
            ReadAction = Str(element.Element("readAction")),
        };

        var fieldsNode = element.Element("fields");
        if (fieldsNode != null)
        {
            foreach (var fieldElement in ExpandDim(fieldsNode.Elements("field")))
            {
                var field = ParseField(fieldElement);
                if (field != null) register.Fields.Add(field);
            }
        }

        // derivedFrom 寄存器继承（同名寄存器定义）
        return register;
    }

    private SvdField? ParseField(XElement element)
    {
        var name = Str(element.Element("name"));
        if (string.IsNullOrWhiteSpace(name)) return null;

        var lsb = (int)(ParseULong(Str(element.Element("bitOffset"))) ?? 0);
        var width = (int)(ParseULong(Str(element.Element("bitWidth"))) ?? 1);

        if (element.Element("bitOffset") == null && element.Element("lsb") != null)
            lsb = (int)(ParseULong(Str(element.Element("lsb"))) ?? 0);
        if (element.Element("bitWidth") == null && element.Element("msb") != null)
        {
            var msb = (int)(ParseULong(Str(element.Element("msb"))) ?? (ulong)lsb);
            width = Math.Max(1, msb - lsb + 1);
        }

        // <bitRange>[7:0]</bitRange> 写法（大量厂商 SVD 使用）
        var bitRangeText = Str(element.Element("bitRange"));
        if (element.Element("bitOffset") == null && element.Element("lsb") == null && bitRangeText != null)
        {
            var match = System.Text.RegularExpressions.Regex.Match(bitRangeText,
                @"\[\s*(\d+)\s*:\s*(\d+)\s*\]");
            if (match.Success)
            {
                lsb = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                width = Math.Max(1, int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) - lsb + 1);
            }
        }

        var field = new SvdField
        {
            Name = name,
            Description = Str(element.Element("description")),
            BitOffset = lsb,
            BitWidth = width,
            Access = Str(element.Element("access")) ?? "read-write",
        };

        foreach (var ev in element.Elements("enumeratedValues"))
        {
            foreach (var item in ev.Elements("enumeratedValue"))
            {
                var valueText = Str(item.Element("value"));
                if (valueText == null || !TryParseULong(valueText, out var value)) continue;
                var description = Str(item.Element("description")) ?? Str(item.Element("name")) ?? valueText;
                field.EnumeratedValues[value] = description;
            }
        }

        return field;
    }

    private static IEnumerable<XElement> ExpandDim(IEnumerable<XElement> elements)
    {
        foreach (var element in elements)
        {
            var dim = Str(element.Element("dim"));
            var dimIndex = Str(element.Element("dimIndex"));
            var name = Str(element.Element("name"));

            if (dim == null || !int.TryParse(dim, out int count) || count <= 0 || name == null)
            {
                yield return element;
                continue;
            }

            var indices = new List<string>();
            if (!string.IsNullOrWhiteSpace(dimIndex))
            {
                var parts = dimIndex.Split(',');
                foreach (var part in parts)
                {
                    var p = part.Trim();
                    if (p.Contains('-'))
                    {
                        var range = p.Split('-');
                        if (range.Length == 2 && int.TryParse(range[0], out int from) && int.TryParse(range[1], out int to))
                        {
                            for (int i = from; i <= to; i++) indices.Add(i.ToString());
                            continue;
                        }
                    }
                    indices.Add(p);
                }
            }
            if (indices.Count != count)
            {
                indices.Clear();
                for (int i = 0; i < count; i++) indices.Add(i.ToString());
            }

            var increment = ParseULong(Str(element.Element("dimIncrement"))) ?? 0;
            for (int i = 0; i < count; i++)
            {
                var clone = new XElement(element);
                clone.Element("name")!.Value = name.Replace("%s", indices[i]);
                clone.SetElementValue("displayName", name.Replace("%s", indices[i]));
                if (increment > 0)
                {
                    var offsetElement = clone.Element("addressOffset") ?? clone.Element("bitOffset");
                    if (offsetElement != null && TryParseULong(offsetElement.Value, out var offset))
                        offsetElement.Value = "0x" + (offset + increment * (ulong)i).ToString("X");
                }
                clone.Elements("dim").Remove();
                clone.Elements("dimIndex").Remove();
                clone.Elements("dimIncrement").Remove();
                yield return clone;
            }
        }
    }

    private static XElement? Merged(XElement merged, XElement element, string name)
    {
        var own = element.Element(name);
        return own ?? merged.Element(name);
    }

    private static string? Str(XElement? element) => element?.Value?.Trim();

    private static ulong? ParseULong(string? text) =>
        text != null && TryParseULong(text, out var value) ? value : null;

    private static bool TryParseULong(string text, out ulong value)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || text.StartsWith("#"))
        {
            var digits = text.StartsWith('#') ? text[1..] : text[2..];
            return ulong.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }
        // 0b 前缀必须先于十进制解析判断（"0b1010" 不含 x，走不到旧分支）
        if (text.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            return TryParseBinary(text[2..], out value);
        return ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseBinary(string digits, out ulong value)
    {
        value = 0;
        foreach (var c in digits)
        {
            if (c != '0' && c != '1' && c != '_') return false;
            if (c == '_') continue;
            value = (value << 1) | (ulong)(c - '0');
        }
        return true;
    }
}
