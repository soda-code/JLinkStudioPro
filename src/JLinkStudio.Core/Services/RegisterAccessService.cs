using JLinkStudio.Core.Models;

namespace JLinkStudio.Core.Services;

public sealed class RegisterReadResult
{
    public SvdRegister Register { get; init; } = new();
    public uint Value { get; init; }
    public string? Error { get; init; }
    public bool Skipped { get; init; }
    public Dictionary<string, string> FieldDescriptions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string ValueHex => "0x" + Value.ToString("X8");
}

/// <summary>寄存器访问服务：基于 SVD 模型完成外设寄存器读取、位域掩码写入与异常现场抓取。</summary>
public sealed class RegisterAccessService
{
    private readonly DeviceManager _device;

    private static readonly string[] CoreRegisterNames =
    {
        "R0", "R1", "R2", "R3", "R4", "R5", "R6", "R7",
        "R8", "R9", "R10", "R11", "R12", "SP", "LR", "PC", "xPSR", "MSP", "PSP",
    };

    public RegisterAccessService(DeviceManager device)
    {
        _device = device;
    }

    public SvdModel? Svd => _device.Svd;

    public IReadOnlyList<SvdPeripheral> Peripherals => _device.Svd?.Peripherals ?? new List<SvdPeripheral>();

    public SvdPeripheral? FindPeripheral(string name) =>
        _device.Svd?.Peripherals.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>读取一个外设下的寄存器；带读副作用（readAction）的寄存器默认跳过。</summary>
    public List<RegisterReadResult> ReadPeripheral(string peripheralName, bool includeSideEffects = false)
    {
        var results = new List<RegisterReadResult>();
        var peripheral = FindPeripheral(peripheralName);
        if (peripheral == null) return results;

        var link = _device.Link;
        if (link == null || !link.IsConnected)
            return results;

        foreach (var register in peripheral.Registers)
        {
            if (register.HasReadSideEffect && !includeSideEffects)
            {
                results.Add(new RegisterReadResult { Register = register, Skipped = true });
                continue;
            }

            var buffer = new byte[register.RequiresAlignedAccess ? 4 : Math.Max(1, register.ByteSize)];
            if (link.ReadMemory((uint)register.AbsoluteAddress, buffer) < 0)
            {
                results.Add(new RegisterReadResult { Register = register, Error = "读取失败" });
                continue;
            }

            uint value = buffer.Length >= 4
                ? BitConverter.ToUInt32(buffer, 0)
                : buffer.Length == 2 ? BitConverter.ToUInt16(buffer, 0) : buffer[0];

            var result = new RegisterReadResult { Register = register, Value = value };
            foreach (var field in register.Fields)
            {
                var description = field.Describe(value);
                if (!string.IsNullOrEmpty(description))
                    result.FieldDescriptions[field.Name] = description;
            }
            results.Add(result);
        }

        return results;
    }

    public RegisterReadResult? ReadRegister(string peripheralName, string registerName)
    {
        var peripheral = FindPeripheral(peripheralName);
        var register = peripheral?.FindRegister(registerName);
        if (register == null) return null;

        var link = _device.Link;
        if (link == null || !link.IsConnected) return null;

        var buffer = new byte[register.RequiresAlignedAccess ? 4 : Math.Max(1, register.ByteSize)];
        if (link.ReadMemory((uint)register.AbsoluteAddress, buffer) < 0)
            return new RegisterReadResult { Register = register, Error = "读取失败" };

        uint value = buffer.Length >= 4
            ? BitConverter.ToUInt32(buffer, 0)
            : buffer.Length == 2 ? BitConverter.ToUInt16(buffer, 0) : buffer[0];

        var result = new RegisterReadResult { Register = register, Value = value };
        foreach (var field in register.Fields)
        {
            var description = field.Describe(value);
            if (!string.IsNullOrEmpty(description)) result.FieldDescriptions[field.Name] = description;
        }
        return result;
    }

    public bool WriteRegister(string peripheralName, string registerName, uint value, out string error)
    {
        error = string.Empty;
        var register = FindPeripheral(peripheralName)?.FindRegister(registerName);
        if (register == null)
        {
            error = $"SVD 中未找到寄存器 {peripheralName}.{registerName}";
            return false;
        }

        var link = _device.Link;
        if (link == null || !link.IsConnected)
        {
            error = "目标未连接。";
            return false;
        }

        var payload = register.RequiresAlignedAccess
            ? BitConverter.GetBytes(value)
            : register.ByteSize == 2 ? BitConverter.GetBytes((ushort)value) : new[] { (byte)value };

        if (link.WriteMemory((uint)register.AbsoluteAddress, payload) < 0)
        {
            error = $"寄存器写入失败 @{register.AddressText}";
            return false;
        }
        return true;
    }

    /// <summary>位域安全写入：Read-Modify-Write，避免影响同寄存器其它位。</summary>
    public bool WriteField(string peripheralName, string registerName, string fieldName, ulong fieldValue, out string error,
        out uint newValue)
    {
        error = string.Empty;
        newValue = 0;

        var peripheral = FindPeripheral(peripheralName);
        var register = peripheral?.FindRegister(registerName);
        if (register == null)
        {
            error = $"SVD 中未找到寄存器 {peripheralName}.{registerName}";
            return false;
        }

        var field = register.FindField(fieldName);
        if (field == null)
        {
            error = $"寄存器 {registerName} 中未找到位域 {fieldName}";
            return false;
        }

        var current = ReadRegister(peripheralName, registerName);
        if (current == null)
        {
            error = "读取寄存器当前值失败。";
            return false;
        }

        ulong updated = field.Insert(current.Value, fieldValue);
        newValue = (uint)updated;
        return WriteRegister(peripheralName, registerName, newValue, out error);
    }

    public Dictionary<string, string> ReadCoreRegisters()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var link = _device.Link;
        if (link == null || !link.IsConnected) return result;

        for (int i = 0; i < CoreRegisterNames.Length; i++)
            result[CoreRegisterNames[i]] = "0x" + link.ReadRegister(i).ToString("X8");

        return result;
    }

    /// <summary>抓取 HardFault 现场：CPU 核心寄存器 + SCB 故障寄存器 + RTT 尾部日志。</summary>
    public Dictionary<string, object?> DumpFaultSnapshot()
    {
        var snapshot = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["device"] = _device.Config.Device,
            ["connected"] = _device.IsConnected,
        };

        snapshot["coreRegisters"] = ReadCoreRegisters();

        var scb = ReadPeripheral("SCB", includeSideEffects: true);
        snapshot["scb"] = scb.Select(r => new
        {
            r.Register.Name,
            address = r.Register.AddressText,
            value = r.ValueHex,
            resetValue = "0x" + r.Register.ResetValue.ToString("X8"),
            description = r.Register.Description,
            fields = r.Register.Fields
                .Where(f => f.Extract(r.Value) != 0)
                .Select(f => new { f.Name, value = f.Extract(r.Value), bits = f.BitsText, text = f.Describe(r.Value) })
                .ToList(),
        }).ToList();

        return snapshot;
    }
}
