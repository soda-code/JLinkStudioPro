using JLinkStudio.Core.Models;
using JLinkStudio.Core.Services;
using JLinkStudio.SkillGateway.Models;
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

    /// <summary>常见内存区域预置（与界面「内存」页一致），外部可按区域名直接读写。</summary>
    [HttpGet("memory/regions")]
    public IActionResult MemoryRegions() => Ok(new
    {
        maxBytesPerRequest = MaxRegionBytes,
        regions = new object[]
        {
            new
            {
                key = "ram", name = "RAM", address = "0x20000000",
                description = "Cortex-M 通常从 0x20000000 起，长度超出 RAM 容量会读失败",
            },
            new
            {
                key = "flash", name = "内部 Flash", address = "0x08000000",
                description = "片内 Flash，读回即当前已烧写的固件内容",
            },
            new
            {
                key = "extflash", name = "外部 Flash", address = "0x90000000",
                description = "基址随芯片与外部存储器接口而异：QSPI 0x90000000 / FMC 0x60000000 / SDRAM 映射 0xC0000000",
            },
        },
    });

    /// <summary>
    /// 按地址读取一段内存（只读）。address 与 region 二选一，region 取 ram / flash / extflash。
    /// 单次最多 64 KiB，内部按 4 KiB 分块读取。
    /// </summary>
    [HttpGet("memory")]
    public IActionResult ReadMemory([FromQuery] string? address = null, [FromQuery] int length = 64,
        [FromQuery] string format = "hex", [FromQuery] string? region = null)
    {
        var (start, resolveError) = ResolveAddress(address, region);
        if (resolveError != null) return BadRequest(new { error = resolveError });
        if (start is null) return BadRequest(new { error = "address 格式应为 0x20000000 或十进制" });

        uint from = start.Value;
        length = Math.Clamp(length, 1, MaxRegionBytes);

        var link = _context.Device.Link;
        if (link == null || !link.IsConnected)
            return Conflict(new { error = "目标未连接" });

        var buffer = new byte[length];
        int read = ReadChunked(link, from, buffer, out string readError);
        if (read <= 0)
            return StatusCode(500, new { error = string.IsNullOrEmpty(readError) ? $"内存读取失败 @0x{from:X8}" : readError });

        var bytes = buffer.Take(read).ToArray();

        return Ok(new
        {
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            address = $"0x{from:X8}",
            region = NormalizeRegion(region),
            length = read,
            complete = read == length,
            error = read == length ? null : readError,
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

    /// <summary>
    /// 按地址写入一段内存：hex 与 bytes 二选一，默认写后回读校验。
    /// 查看模式或写保护关闭时拒绝，与寄存器 / 变量写入的安全策略一致。
    /// </summary>
    [HttpPost("memory")]
    public IActionResult WriteMemory([FromBody] MemoryWriteRequest request)
    {
        if (request == null)
            return BadRequest(new { success = false, error = "请求体不能为空" });

        if (_context.IsInspectMode)
            return StatusCode(403, new { success = false, error = "查看模式已启用：禁止写入内存。" });

        if (!_context.Security.WriteEnabled)
            return StatusCode(403, new { success = false, error = "写保护总开关已关闭：先 POST /api/v1/session/settings 打开 writeEnabled。" });

        var (start, resolveError) = ResolveAddress(request.Address, null);
        if (resolveError != null) return BadRequest(new { success = false, error = resolveError });
        if (start is null)
            return BadRequest(new { success = false, error = "address 格式应为 0x20000000 或十进制" });

        if (!TryDecodeBytes(request, out byte[] payload, out string decodeError))
            return BadRequest(new { success = false, error = decodeError });

        if (payload.Length > MaxRegionBytes)
            return BadRequest(new
            {
                success = false,
                error = $"单次最多写入 {MaxRegionBytes} 字节（0x{MaxRegionBytes:X}），当前 {payload.Length} 字节",
            });

        var link = _context.Device.Link;
        if (link == null || !link.IsConnected)
            return Conflict(new { success = false, error = "目标未连接" });

        uint from = start.Value;
        int offset = 0;
        while (offset < payload.Length)
        {
            int chunk = Math.Min(ChunkSize, payload.Length - offset);
            var piece = new byte[chunk];
            Buffer.BlockCopy(payload, offset, piece, 0, chunk);

            int rc = link.WriteMemory((uint)(from + offset), piece);
            if (rc < 0)
            {
                _context.Audit.Add("skill", "WRITE-MEM", "/api/v1/data/memory",
                    $"0x{from:X8} 写入中断 @+{offset}", offset, false);
                return StatusCode(500, new
                {
                    success = false,
                    error = $"写入失败 @0x{from + offset:X8}（已写 {offset} 字节）",
                    written = offset,
                });
            }

            offset += chunk;
        }

        bool verified = true;
        string? verifyError = null;
        if (request.Verify)
        {
            var back = new byte[payload.Length];
            int readBack = ReadChunked(link, from, back, out string verifyReadError);
            if (readBack != payload.Length)
            {
                verified = false;
                verifyError = $"回读失败：{verifyReadError}（回读 {readBack}/{payload.Length} 字节）";
            }
            else if (!back.AsSpan().SequenceEqual(payload))
            {
                verified = false;
                int diff = 0;
                while (diff < payload.Length && back[diff] == payload[diff]) diff++;
                verifyError = $"回读不一致：首个不同字节在 +{diff}（写入 0x{payload[diff]:X2}，回读 0x{back[diff]:X2}）";
            }
        }

        _context.Audit.Add("skill", "WRITE-MEM", "/api/v1/data/memory",
            $"0x{from:X8} + {payload.Length} 字节", payload.Length, verified);

        return Ok(new
        {
            success = true,
            address = $"0x{from:X8}",
            length = payload.Length,
            verify = request.Verify,
            verified,
            error = verifyError,
        });
    }

    /// <summary>单次读写上限：64 KiB（与界面「内存」页一致）。</summary>
    private const int MaxRegionBytes = 0x10000;

    /// <summary>分块大小：每次 4 KiB，失败时能给出已读/已写偏移。</summary>
    private const int ChunkSize = 4096;

    private static int ReadChunked(JLinkStudio.Core.Interop.IJLinkLink link, uint start, byte[] buffer,
        out string error)
    {
        error = string.Empty;
        int offset = 0;
        while (offset < buffer.Length)
        {
            int chunk = Math.Min(ChunkSize, buffer.Length - offset);
            var piece = new byte[chunk];
            int rc = link.ReadMemory((uint)(start + offset), piece);
            if (rc < 0)
            {
                error = $"地址 0x{start + offset:X8} 读取失败（已读 {offset} 字节）";
                return offset;
            }

            Buffer.BlockCopy(piece, 0, buffer, offset, chunk);
            offset += chunk;
        }

        return offset;
    }

    /// <summary>hex 与 bytes 二选一解码；hex 允许空格、逗号、短横线与 0x 前缀。</summary>
    private static bool TryDecodeBytes(MemoryWriteRequest request, out byte[] payload, out string error)
    {
        payload = Array.Empty<byte>();
        error = string.Empty;

        if (!string.IsNullOrWhiteSpace(request.Hex))
        {
            string cleaned = request.Hex.Trim();
            if (cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) cleaned = cleaned[2..];
            cleaned = new string(cleaned.Where(Uri.IsHexDigit).ToArray());

            if (cleaned.Length == 0)
            {
                error = "hex 不含有效十六进制字符";
                return false;
            }

            if (cleaned.Length % 2 != 0)
            {
                error = "hex 长度必须为偶数（每两个字符一个字节）";
                return false;
            }

            payload = Convert.FromHexString(cleaned);
            return true;
        }

        if (request.Bytes is { Count: > 0 })
        {
            var bytes = new byte[request.Bytes.Count];
            for (int i = 0; i < bytes.Length; i++)
            {
                int v = request.Bytes[i];
                if (v < 0 || v > 255)
                {
                    error = $"bytes[{i}]={v} 超出 0~255";
                    return false;
                }

                bytes[i] = (byte)v;
            }

            payload = bytes;
            return true;
        }

        error = "hex 与 bytes 至少提供一个";
        return false;
    }

    /// <summary>把 address 或 region 解析成起始地址；两者都为空时返回 null 表示格式错误。</summary>
    private static (uint? Address, string? Error) ResolveAddress(string? address, string? region)
    {
        if (!string.IsNullOrWhiteSpace(address))
            return TryParseAddress(address, out uint parsed)
                ? (parsed, null)
                : (null, "address 格式应为 0x20000000 或十进制");

        string key = NormalizeRegion(region);
        if (key.Length == 0) return (null, "需要 address 或 region 参数");

        return key switch
        {
            "ram" => (0x20000000u, null),
            "flash" => (0x08000000u, null),
            "extflash" => (0x90000000u, null),
            _ => (null, "region 只支持 ram / flash / extflash；其它地址请用 address 参数")
        };
    }

    private static string NormalizeRegion(string? region) =>
        (region ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "ram" => "ram",
            "flash" => "flash",
            "extflash" or "ext" or "externalflash" => "extflash",
            _ => string.Empty,
        };

    private static bool TryParseAddress(string? text, out uint value)
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
