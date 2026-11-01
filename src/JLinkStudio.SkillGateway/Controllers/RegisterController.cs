using JLinkStudio.Core.Services;
using JLinkStudio.SkillGateway.Models;
using Microsoft.AspNetCore.Mvc;

namespace JLinkStudio.SkillGateway.Controllers;

/// <summary>硬件寄存器与故障诊断接口。</summary>
[ApiController]
[Route("api/v1/registers")]
public sealed class RegisterController : ControllerBase
{
    private readonly StudioContext _context;

    public RegisterController(StudioContext context) => _context = context;

    /// <summary>列出 SVD 中所有外设。</summary>
    [HttpGet("peripherals")]
    public IActionResult Peripherals() => Ok(_context.Registers.Peripherals.Select(p => new
    {
        p.Name,
        p.Description,
        p.GroupName,
        baseAddress = $"0x{p.BaseAddress:X8}",
        registerCount = p.Registers.Count,
    }));

    /// <summary>读取某个外设下全部寄存器及位域语义（readAction 寄存器默认跳过）。</summary>
    [HttpGet("peripheral/{peripheralName}")]
    public IActionResult ReadPeripheral(string peripheralName, [FromQuery] bool includeSideEffects = false)
    {
        var peripheral = _context.Registers.FindPeripheral(peripheralName);
        if (peripheral == null) return NotFound(new { error = $"SVD 中未找到外设 {peripheralName}" });

        var registers = _context.Registers.ReadPeripheral(peripheralName, includeSideEffects);
        return Ok(new
        {
            peripheral = peripheral.Name,
            description = peripheral.Description,
            baseAddress = $"0x{peripheral.BaseAddress:X8}",
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            registers = registers.Select(r => new
            {
                r.Register.Name,
                address = r.Register.AddressText,
                offset = $"0x{r.Register.Offset:X}",
                size = r.Register.SizeBits,
                resetValue = $"0x{r.Register.ResetValue:X8}",
                access = r.Register.Access,
                readAction = r.Register.ReadAction,
                description = r.Register.Description,
                skipped = r.Skipped,
                error = r.Error,
                value = r.Skipped ? null : r.ValueHex,
                numericValue = r.Skipped ? 0 : r.Value,
                fieldDescriptions = r.FieldDescriptions,
                fields = r.Register.Fields.Select(f => new
                {
                    f.Name,
                    bits = f.BitsText,
                    f.Description,
                    value = r.Skipped ? 0UL : f.Extract(r.Value),
                    text = r.Skipped ? null : f.Describe(r.Value),
                    access = f.Access,
                }),
            }),
        });
    }

    /// <summary>读取单个寄存器。</summary>
    [HttpGet("register/{peripheral}/{register}")]
    public IActionResult ReadRegister(string peripheral, string register)
    {
        var result = _context.Registers.ReadRegister(peripheral, register);
        if (result == null) return NotFound(new { error = $"未找到寄存器 {peripheral}.{register}" });

        return Ok(new
        {
            peripheral,
            register = result.Register.Name,
            address = result.Register.AddressText,
            value = result.ValueHex,
            numericValue = result.Value,
            resetValue = $"0x{result.Register.ResetValue:X8}",
            description = result.Register.Description,
            error = result.Error,
            fieldDescriptions = result.FieldDescriptions,
        });
    }

    /// <summary>位域安全写入（Read-Modify-Write，查看模式下禁止）。</summary>
    [HttpPost("write-field")]
    public IActionResult WriteField([FromBody] FieldWriteRequest request)
    {
        if (_context.IsInspectMode)
            return StatusCode(403, new { success = false, error = "查看模式已启用：禁止写入寄存器。" });

        bool ok = _context.Registers.WriteField(request.Peripheral, request.Register, request.Field,
            request.Value, out var error, out var newValue);

        _context.Audit.Add("skill", "WRITE-FIELD", "/api/v1/registers/write-field",
            $"{request.Peripheral}.{request.Register}.{request.Field} = {request.Value}", 0, ok);

        return ok
            ? Ok(new
            {
                success = true,
                peripheral = request.Peripheral,
                register = request.Register,
                field = request.Field,
                newValue = $"0x{newValue:X8}",
            })
            : StatusCode(400, new { success = false, error });
    }

    /// <summary>写入整个寄存器（查看模式下禁止）。</summary>
    [HttpPost("write-register")]
    public IActionResult WriteRegister([FromQuery] string peripheral, [FromQuery] string register, [FromQuery] uint value)
    {
        if (_context.IsInspectMode)
            return StatusCode(403, new { success = false, error = "查看模式已启用：禁止写入寄存器。" });

        bool ok = _context.Registers.WriteRegister(peripheral, register, value, out var error);
        _context.Audit.Add("skill", "WRITE-REG", "/api/v1/registers/write-register",
            $"{peripheral}.{register} = 0x{value:X8}", 0, ok);
        return ok ? Ok(new { success = true }) : StatusCode(400, new { success = false, error });
    }

    /// <summary>HardFault 现场快照：CPU 核心寄存器 + SCB 故障寄存器 + 最近 RTT 日志。</summary>
    [HttpGet("fault-snapshot")]
    public IActionResult FaultSnapshot([FromQuery] int lastLogs = 100)
    {
        var snapshot = _context.Registers.DumpFaultSnapshot();
        var core = snapshot.TryGetValue("coreRegisters", out var c)
            ? (Dictionary<string, string>)c! : new Dictionary<string, string>();
        var scb = snapshot.TryGetValue("scb", out var s) ? s : null;

        var logs = _context.Rtt.Query(0, lastLogs)
            .Select(l => new RttLineDto
            {
                Timestamp = l.TimestampMs,
                Channel = l.Channel,
                Level = l.Level.ToString(),
                Text = l.Text,
                Fields = l.Fields,
            })
            .ToList();

        var diagnosis = BuildDiagnosis(core, scb);

        _context.Audit.Add("skill", "SNAPSHOT", "/api/v1/registers/fault-snapshot", diagnosis);

        return Ok(new FaultSnapshotDto
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Device = _context.Device.Config.Device,
            CoreRegisters = core,
            FaultRegisters = scb is IEnumerable<object> list ? list.ToList() : new List<object?>(),
            RecentLogs = logs,
            Diagnosis = diagnosis,
        });
    }

    private static string BuildDiagnosis(Dictionary<string, string> core, object? scb)
    {
        var parts = new List<string>();
        if (core.Count > 0)
            parts.Add($"PC={core.GetValueOrDefault("PC")}, LR={core.GetValueOrDefault("LR")}, SP={core.GetValueOrDefault("SP")}");

        var json = System.Text.Json.JsonSerializer.Serialize(scb);
        if (json.Contains("\"CFSR\"") && json.Contains("\"PRECISERR\""))
            parts.Add("检测到 SCB.CFSR.PRECISERR=1：精确数据总线错误（BFAR 指向非法访问地址），常见于空指针解引用或数组越界。");
        if (json.Contains("IACCVIOL"))
            parts.Add("检测到取指访问违例：可能跳转到非法地址或 XN 区。");

        return parts.Count > 0 ? string.Join("；", parts) : "未发现明显故障标志位，请结合最近 RTT 日志进一步分析。";
    }
}
