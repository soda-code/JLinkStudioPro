using JLinkStudio.Core.Models;
using JLinkStudio.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace JLinkStudio.SkillGateway.Controllers;

/// <summary>数据汇聚接口：一次请求返回上位机当前的全部可观测数据。</summary>
[ApiController]
[Route("api/v1/data")]
public sealed class DataController : ControllerBase
{
    private readonly StudioContext _context;

    public DataController(StudioContext context) => _context = context;

    /// <summary>
    /// 全量快照：会话状态 + 监控变量最新值 + 指定寄存器 + RTT 日志 + 安全策略，一次读全。
    /// </summary>
    /// <param name="logs">返回的 RTT 日志条数（0 表示不要日志）。</param>
    /// <param name="registers">逗号分隔的「外设.寄存器」，如 GPIOA.MODER,RCC.CR；不传则不读寄存器。</param>
    [HttpGet("snapshot")]
    public IActionResult Snapshot([FromQuery] int logs = 100, [FromQuery] string? registers = null)
    {
        var watchlist = _context.Variables.LastSamples
            .Select(s => new
            {
                name = s.Name,
                address = $"0x{s.Address:X8}",
                type = s.Type.ToCName(),
                value = s.Value,
                valueText = s.ValueText,
                timestamp = s.TimestampMs,
            })
            .ToList();

        var rttLines = _context.Rtt.Recent(Math.Clamp(logs, 0, 2000))
            .Select(l => new
            {
                timestamp = l.TimestampMs,
                channel = l.Channel,
                level = l.Level.ToString(),
                text = l.Text,
            })
            .ToList();

        var registerValues = new List<object>();
        var errors = new List<string>();

        if (!string.IsNullOrWhiteSpace(registers))
        {
            foreach (string item in registers.Split(',',
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int dot = item.IndexOf('.');
                if (dot <= 0)
                {
                    errors.Add($"寄存器格式应为「外设.寄存器」: {item}");
                    continue;
                }

                string peripheralName = item[..dot];
                string registerName = item[(dot + 1)..];

                var result = _context.Registers.ReadRegister(peripheralName, registerName);
                if (result == null)
                {
                    errors.Add($"读取失败: {item}");
                    continue;
                }

                registerValues.Add(new
                {
                    register = $"{peripheralName}.{registerName}",
                    value = result.Value,
                    valueHex = result.ValueHex,
                    skipped = result.Skipped,
                    error = result.Error,
                });
            }
        }

        return Ok(new
        {
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            session = _context.BuildStatus(),
            security = new
            {
                inspectMode = _context.IsInspectMode,
                writeEnabled = _context.Security.WriteEnabled,
            },
            watchlist,
            variables = new
            {
                count = _context.Variables.ActiveLeafCount,
                sampling = _context.Variables.IsRunning,
                sampleRateHz = Math.Round(_context.Variables.SampleRateHz, 1),
            },
            rtt = new
            {
                running = _context.Rtt.IsRunning,
                totalLines = _context.Rtt.TotalLines,
                lines = rttLines,
            },
            registers = registerValues,
            errors,
        });
    }

    /// <summary>一次读取多个寄存器（等价于 snapshot 的 registers 参数，返回更精简）。</summary>
    [HttpGet("registers")]
    public IActionResult ReadRegisters([FromQuery] string registers)
    {
        if (string.IsNullOrWhiteSpace(registers))
            return BadRequest(new { error = "registers 不能为空，格式如 GPIOA.MODER,RCC.CR" });

        var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        foreach (string item in registers.Split(',',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int dot = item.IndexOf('.');
            if (dot <= 0)
            {
                errors.Add($"寄存器格式应为「外设.寄存器」: {item}");
                continue;
            }

            var result = _context.Registers.ReadRegister(item[..dot], item[(dot + 1)..]);
            if (result == null)
            {
                errors.Add($"读取失败: {item}");
                continue;
            }

            data[item] = new { value = result.Value, valueHex = result.ValueHex };
        }

        return Ok(new { timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), data, errors });
    }

    /// <summary>
    /// 按地址读取一段内存（只读，用于查看无符号数据的缓冲区 / 栈 / DMA 描述符）。
    /// </summary>
    [HttpGet("memory")]
    public IActionResult ReadMemory([FromQuery] string address, [FromQuery] int length = 64,
        [FromQuery] string format = "hex")
    {
        if (!TryParseAddress(address, out uint start))
            return BadRequest(new { error = "address 格式应为 0x20000000 或十进制" });

        length = Math.Clamp(length, 1, 4096);

        var link = _context.Device.Link;
        if (link == null || !link.IsConnected)
            return Conflict(new { error = "目标未连接" });

        var buffer = new byte[length];
        int read = link.ReadMemory(start, buffer);
        if (read < 0)
            return StatusCode(500, new { error = $"内存读取失败 @0x{start:X8}" });

        var bytes = buffer.Take(read).ToArray();

        return Ok(new
        {
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            address = $"0x{start:X8}",
            length = read,
            format,
            hex = Convert.ToHexString(bytes),
            u8 = bytes,
            u16 = Enumerable.Range(0, bytes.Length / 2)
                .Select(i => BitConverter.ToUInt16(bytes, i * 2)).ToArray(),
            u32 = Enumerable.Range(0, bytes.Length / 4)
                .Select(i => BitConverter.ToUInt32(bytes, i * 4)).ToArray(),
            f32 = Enumerable.Range(0, bytes.Length / 4)
                .Select(i => BitConverter.ToSingle(bytes, i * 4)).ToArray(),
        });
    }

    private static bool TryParseAddress(string text, out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out value);

        return uint.TryParse(text, out value);
    }
}
