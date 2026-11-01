using System.Globalization;
using System.Text;
using JLinkStudio.Core.Models;

namespace JLinkStudio.Core.Services;

/// <summary>原始字节与变量值之间的编解码、格式化工具。</summary>
public static class ValueCodec
{
    public static object? Decode(ReadOnlySpan<byte> data, DataType type, int size)
    {
        if (size <= 0) size = Math.Max(1, type.SizeOf());
        if (data.Length < size) return null;

        return type switch
        {
            DataType.Bool => data[0] != 0,
            DataType.UInt8 => data[0],
            DataType.Int8 => (sbyte)data[0],
            DataType.UInt16 => size >= 2 ? BitConverter.ToUInt16(data[..2]) : data[0],
            DataType.Int16 => size >= 2 ? BitConverter.ToInt16(data[..2]) : (sbyte)data[0],
            DataType.UInt32 or DataType.Enum =>
                size switch
                {
                    1 => data[0],
                    2 => (uint)BitConverter.ToUInt16(data[..2]),
                    8 => (uint)BitConverter.ToUInt64(data[..8]),
                    _ => BitConverter.ToUInt32(data[..4]),
                },
            DataType.Int32 => size switch
            {
                1 => (int)(sbyte)data[0],
                2 => BitConverter.ToInt16(data[..2]),
                _ => BitConverter.ToInt32(data[..4]),
            },
            DataType.UInt64 => BitConverter.ToUInt64(data[..8]),
            DataType.Int64 => BitConverter.ToInt64(data[..8]),
            DataType.Float => BitConverter.ToSingle(data[..4]),
            DataType.Double => BitConverter.ToDouble(data[..8]),
            DataType.Pointer => size == 8 ? BitConverter.ToUInt64(data[..8]) : BitConverter.ToUInt32(data[..4]),
            _ => data[0],
        };
    }

    public static byte[] Encode(object? value, DataType type, int size)
    {
        if (size <= 0) size = Math.Max(1, type.SizeOf());
        var buffer = new byte[size];

        switch (type)
        {
            case DataType.Bool:
                buffer[0] = ToBool(value) ? (byte)1 : (byte)0;
                break;
            case DataType.UInt8:
                buffer[0] = (byte)Math.Clamp(Convert.ToInt64(value, CultureInfo.InvariantCulture), 0, 255);
                break;
            case DataType.Int8:
                buffer[0] = unchecked((byte)(sbyte)Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case DataType.UInt16:
            case DataType.Int16:
                BitConverter.GetBytes((short)Convert.ToInt64(value, CultureInfo.InvariantCulture)).CopyTo(buffer, 0);
                break;
            case DataType.UInt32:
            case DataType.Enum:
            case DataType.Pointer:
                WriteUInt(buffer, ClampToUInt64(value));
                break;
            case DataType.Int32:
                BitConverter.GetBytes(Convert.ToInt32(value, CultureInfo.InvariantCulture)).CopyTo(buffer, 0);
                break;
            case DataType.UInt64:
                BitConverter.GetBytes(Convert.ToUInt64(value, CultureInfo.InvariantCulture)).CopyTo(buffer, 0);
                break;
            case DataType.Int64:
                BitConverter.GetBytes(Convert.ToInt64(value, CultureInfo.InvariantCulture)).CopyTo(buffer, 0);
                break;
            case DataType.Float:
                BitConverter.GetBytes(Convert.ToSingle(value, CultureInfo.InvariantCulture)).CopyTo(buffer, 0);
                break;
            case DataType.Double:
                BitConverter.GetBytes(Convert.ToDouble(value, CultureInfo.InvariantCulture)).CopyTo(buffer, 0);
                break;
            default:
                buffer[0] = (byte)Convert.ToInt64(value, CultureInfo.InvariantCulture);
                break;
        }

        return buffer;
    }

    /// <summary>
    /// 无符号类型写入时允许负数（如 -1 表示 0xFFFFFFFF），按补码回绕，不再抛 OverflowException。
    /// </summary>
    private static ulong ClampToUInt64(object? value)
    {
        try
        {
            return Convert.ToUInt64(value, CultureInfo.InvariantCulture);
        }
        catch (OverflowException)
        {
            try
            {
                return unchecked((ulong)Convert.ToInt64(value, CultureInfo.InvariantCulture));
            }
            catch
            {
                return 0;
            }
        }
        catch (FormatException)
        {
            return 0;
        }
    }

    private static void WriteUInt(byte[] buffer, ulong value)
    {
        if (buffer.Length >= 8) BitConverter.GetBytes(value).CopyTo(buffer, 0);
        else if (buffer.Length >= 4) BitConverter.GetBytes((uint)value).CopyTo(buffer, 0);
        else if (buffer.Length >= 2) BitConverter.GetBytes((ushort)value).CopyTo(buffer, 0);
        else buffer[0] = (byte)value;
    }

    public static bool ToBool(object? value) => value switch
    {
        null => false,
        bool b => b,
        string s => !string.IsNullOrEmpty(s) && s != "0",
        _ => Convert.ToDouble(value, CultureInfo.InvariantCulture) != 0,
    };

    public static bool TryToDouble(object? value, out double result)
    {
        result = 0;
        if (value == null) return false;
        try
        {
            result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string Format(object? value, DataType type, ValueFormat format)
    {
        if (value == null) return "-";

        if (type.IsFloat())
        {
            double d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (format == ValueFormat.Hex && type == DataType.Float)
                return "0x" + BitConverter.SingleToUInt32Bits((float)d).ToString("X8");
            return d.ToString(Math.Abs(d) >= 1000 ? "F1" : "0.######", CultureInfo.InvariantCulture);
        }

        if (!type.IsNumericIntegral()) return value.ToString() ?? "-";

        long signed = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        ulong unsigned = value is ulong u ? u : unchecked((ulong)signed);
        if (value is uint ui) unsigned = ui;

        return format switch
        {
            ValueFormat.Hex => "0x" + unsigned.ToString("X" + Math.Max(2, type.SizeOf() * 2)),
            ValueFormat.Bin => "0b" + Convert.ToString(unchecked((long)unsigned), 2).PadLeft(Math.Max(1, type.SizeOf() * 8), '0'),
            ValueFormat.Float => signed.ToString(CultureInfo.InvariantCulture),
            ValueFormat.Ascii => unsigned is > 31 and < 127 ? ((char)unsigned).ToString() : ".",
            _ => type == DataType.Enum ? unsigned.ToString(CultureInfo.InvariantCulture) : signed.ToString(CultureInfo.InvariantCulture),
        };
    }

    public static string ToHexString(ulong value, int bytes = 4) =>
        "0x" + value.ToString("X" + Math.Max(2, bytes * 2));

    /// <summary>把 @"0x20000100" / "1234" / "1.5" 解析为可写入的数值。</summary>
    public static bool TryParseInput(string text, DataType type, out object value)
    {
        value = 0;
        text = text.Trim();
        if (text.Length == 0) return false;

        if (type.IsFloat() && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            value = type == DataType.Float ? (float)d : d;
            return true;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!ulong.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)) return false;
            value = type == DataType.Float ? (float)hex : hex;
            return true;
        }

        if (text.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                value = Convert.ToUInt64(text[2..], 2);
                return true;
            }
            catch
            {
                return false;
            }
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
        {
            value = type == DataType.Float ? (float)l : l;
            return true;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
        {
            value = type == DataType.Float ? (float)f : f;
            return true;
        }

        return false;
    }

    public static string DescribeBytes(byte[] data)
    {
        var sb = new StringBuilder();
        foreach (var b in data) sb.Append(b.ToString("X2")).Append(' ');
        return sb.ToString().TrimEnd();
    }
}
