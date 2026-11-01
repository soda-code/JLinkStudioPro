using JLinkStudio.Core.Models;
using JLinkStudio.Core.Services;
using JLinkStudio.SkillGateway.Models;
using Microsoft.AspNetCore.Mvc;

namespace JLinkStudio.SkillGateway.Controllers;

/// <summary>系统与连接控制接口：Skill 可远程配置会话并连接硬件。</summary>
[ApiController]
[Route("api/v1/session")]
public sealed class SessionController : ControllerBase
{
    private readonly StudioContext _context;

    public SessionController(StudioContext context) => _context = context;

    /// <summary>配置上位机参数并按需连接目标（会同步更新界面显示）。</summary>
    [HttpPost("config")]
    public async Task<IActionResult> Configure([FromBody] SessionConfigRequest request)
    {
        var config = _context.Device.Config.Clone();

        if (!string.IsNullOrWhiteSpace(request.Device)) config.Device = request.Device!;
        if (!string.IsNullOrWhiteSpace(request.Interface) &&
            Enum.TryParse<JLinkInterface>(request.Interface, true, out var iface))
            config.Interface = iface;
        if (request.SpeedKhz is int speed) config.SpeedKhz = Math.Clamp(speed, 5, 50000);
        if (!string.IsNullOrWhiteSpace(request.ElfPath)) config.ElfPath = request.ElfPath;
        if (!string.IsNullOrWhiteSpace(request.SvdPath)) config.SvdPath = request.SvdPath;
        if (!string.IsNullOrWhiteSpace(request.RttSearchMode) &&
            Enum.TryParse<RttSearchMode>(request.RttSearchMode, true, out var rttMode))
            config.RttSearchMode = rttMode;
        if (request.RttControlBlockAddress.HasValue)
        {
            config.RttControlBlockAddress = request.RttControlBlockAddress;
            config.RttSearchMode = RttSearchMode.ManualAddress;
        }
        if (!string.IsNullOrWhiteSpace(request.SerialNumber)) config.SerialNumber = request.SerialNumber;
        if (request.AutoConnect.HasValue) config.AutoConnect = request.AutoConnect.Value;

        bool shouldConnect = request.AutoConnect ?? true;
        if (!shouldConnect)
        {
            // 参数必须落地，否则 autoConnect=false 时型号 / 速率等设置会被静默丢弃
            _context.Device.ApplyConfig(config);
            if (!string.IsNullOrWhiteSpace(config.ElfPath))
                _context.Device.LoadElf(config.ElfPath);
            if (!string.IsNullOrWhiteSpace(config.SvdPath))
                _context.Device.LoadSvd(config.SvdPath);
            return Ok(new { connected = _context.Device.IsConnected, message = "配置已更新（未连接）", status = _context.BuildStatus() });
        }

        bool ok = await _context.StartSessionAsync(config);
        if (!ok)
            return StatusCode(503, new { connected = false, error = _context.Device.LastError, status = _context.BuildStatus() });

        _context.Audit.Add("skill", "CONFIG", "/api/v1/session/config",
            $"{config.Device} · {config.Interface} {config.SpeedKhz}kHz · ELF={config.ElfPath ?? "-"}");

        return Ok(new
        {
            connected = true,
            message = "已连接目标",
            status = _context.BuildStatus(),
        });
    }

    /// <summary>查询当前会话状态、符号表与统计信息。</summary>
    [HttpGet("status")]
    public IActionResult Status() => Ok(_context.BuildStatus());

    /// <summary>读取「运行日志」页的会话日志（最近 take 条，可按关键字过滤）。</summary>
    [HttpGet("logs")]
    public IActionResult Logs([FromQuery] int take = 200, [FromQuery] string? keyword = null)
    {
        var entries = _context.RecentSessionLogs(take, keyword);
        return Ok(new
        {
            count = entries.Count,
            logs = entries.Select(e => new { timestamp = e.TimestampMs, text = e.Text }).ToList(),
        });
    }

    /// <summary>当前可外部调整的运行参数（Skill 可先读后改）。</summary>
    [HttpGet("settings")]
    public IActionResult GetSettings() => Ok(BuildSettings());

    /// <summary>外部设置运行参数：查看模式 / 写开关 / 变量采样 / RTT 采集。</summary>
    [HttpPost("settings")]
    public IActionResult SetSettings([FromBody] SettingsRequest request)
    {
        var applied = new List<string>();
        var errors = new List<string>();

        if (request.InspectMode.HasValue)
        {
            _context.IsInspectMode = request.InspectMode.Value;
            applied.Add($"inspectMode={request.InspectMode.Value.ToString().ToLowerInvariant()}");
        }

        if (request.WriteEnabled.HasValue)
        {
            _context.Security.WriteEnabled = request.WriteEnabled.Value;
            applied.Add($"writeEnabled={request.WriteEnabled.Value.ToString().ToLowerInvariant()}");
        }

        if (request.VariablesSampling.HasValue)
        {
            try
            {
                if (request.VariablesSampling.Value)
                {
                    _context.Variables.Start();
                    applied.Add("variablesSampling=true");
                }
                else
                {
                    _context.Variables.Stop();
                    applied.Add("variablesSampling=false");
                }
            }
            catch (Exception ex)
            {
                errors.Add($"变量采样切换失败：{ex.Message}");
            }
        }

        if (request.RttEnabled.HasValue)
        {
            try
            {
                if (request.RttEnabled.Value)
                {
                    if (_context.Rtt.Start(out string rttError)) applied.Add("rttEnabled=true");
                    else errors.Add(rttError);
                }
                else
                {
                    _context.Rtt.Stop();
                    applied.Add("rttEnabled=false");
                }
            }
            catch (Exception ex)
            {
                errors.Add($"RTT 切换失败：{ex.Message}");
            }
        }

        if (request.SampleRateHz.HasValue)
        {
            try
            {
                _context.Variables.SampleRateHz = request.SampleRateHz.Value;
                applied.Add($"sampleRateHz={Math.Round(_context.Variables.SampleRateHz, 1)}");
            }
            catch (Exception ex)
            {
                errors.Add($"采样率设置失败：{ex.Message}");
            }
        }

        if (request.AutoReloadElf.HasValue)
        {
            _context.Device.AutoReloadElf = request.AutoReloadElf.Value;
            applied.Add($"autoReloadElf={request.AutoReloadElf.Value.ToString().ToLowerInvariant()}");
        }

        _context.Audit.Add("skill", "SETTINGS", "/api/v1/session/settings",
            string.Join(", ", applied), 0, errors.Count == 0);

        return Ok(new { success = errors.Count == 0, applied, errors, settings = BuildSettings() });
    }

    /// <summary>当前全部可调参数（Skill 先读后改，改完再读核对）。</summary>
    private object BuildSettings() => new
    {
        connected = _context.Device.IsConnected,
        state = _context.Device.State.ToString(),

        // 连接参数
        device = _context.Device.Config.Device,
        interfaceType = _context.Device.Config.Interface.ToString(),
        speedKhz = _context.Device.Config.SpeedKhz,
        elfPath = _context.Device.Config.ElfPath,
        elfLoaded = _context.Device.Symbols.Count > 0,
        symbolCount = _context.Device.Symbols.Count,
        serialNumber = _context.Device.Config.SerialNumber,
        rttSearchMode = _context.Device.Config.RttSearchMode.ToString(),
        rttControlBlockAddress = _context.Device.Config.RttControlBlockAddress,
        autoReloadElf = _context.Device.AutoReloadElf,

        // 安全与采样
        inspectMode = _context.IsInspectMode,
        writeEnabled = _context.Security.WriteEnabled,
        variablesSampling = _context.Variables.IsRunning,
        sampleRateHz = Math.Round(_context.Variables.SampleRateHz, 1),
        sampleRateRange = new { min = 1, max = 1000 },
        rttEnabled = _context.Rtt.IsRunning,

        // 能力边界（避免 skill 传入越界值）
        limits = new
        {
            captureSampleRateHz = new { min = 1, max = 1000 },
            captureDurationMs = new { min = 10, max = 600000 },
            batchIntervalMs = new { min = 0, max = 60000 },
            memoryReadBytes = new { min = 1, max = 65536 },
            memoryWriteBytes = new { min = 1, max = 65536 },
            promptTimeoutMs = new { min = 1000, max = 300000 },
        },
    };

    /// <summary>内置寄存器库支持的芯片家族（填这些型号前缀能拿到外设寄存器表）。</summary>
    [HttpGet("devices")]
    public IActionResult Devices() => Ok(new
    {
        families = new[]
        {
            new { family = "STM32F1xx", prefix = "STM32F1 / STM32F3 / STM32L1", style = "GPIO CRL/CRH", note = "RM0008 风格" },
            new { family = "STM32F4xx", prefix = "STM32F2 / F4 / F7 / H7 / L4 / G0 / G4", style = "GPIO MODER", note = "其他 STM32 近似复用" },
            new { family = "GD32F1xx", prefix = "GD32F1 / GD32F3", style = "GPIO CTL0/CTL1", note = "GD32 用户手册命名" },
            new { family = "GD32F4xx", prefix = "GD32F4", style = "GPIO MODER", note = "与 STM32F4 兼容" },
            new { family = "LPC17xx", prefix = "LPC17 / LPC11", style = "FIO0-4", note = "NXP LPC17xx 用户手册" },
            new { family = "i.MX RT10xx", prefix = "MIMXRT / MK", style = "GPIO1-5", note = "RT1050/RT1060" },
        },
        fallback = "未收录型号仍可连接与读写变量，寄存器页只提供内核 SCB",
        example = "STM32F407VE / GD32F303CC / LPC1768 / MIMXRT1052DVL6B",
    });

    /// <summary>断开目标连接。</summary>
    [HttpPost("disconnect")]
    public IActionResult Disconnect()
    {
        _context.StopSession();
        return Ok(new { connected = false, message = "已断开" });
    }

    /// <summary>硬件控制：reset / halt / go（查看模式下禁止）。</summary>
    [HttpPost("control")]
    public IActionResult Control([FromQuery] string action)
    {
        if (_context.IsInspectMode)
            return StatusCode(403, new { success = false, error = "查看模式已启用：禁止复位 / 暂停 / 运行等改动目标现场的操作。" });

        bool ok;
        string error;
        switch (action.ToLowerInvariant())
        {
            case "reset": ok = _context.Device.Reset(out error); break;
            case "halt": ok = _context.Device.Halt(out error); break;
            case "go": ok = _context.Device.Go(out error); break;
            default: return BadRequest(new { error = "action 仅支持 reset / halt / go" });
        }

        return ok
            ? Ok(new { success = true, action })
            : StatusCode(409, new { success = false, error });
    }

    /// <summary>重新加载 ELF / SVD 工程文件。</summary>
    [HttpPost("reload")]
    public IActionResult Reload([FromQuery] string? elfPath, [FromQuery] string? svdPath)
    {
        var errors = new List<string>();
        bool elfOk = true, svdOk = true;

        if (!string.IsNullOrWhiteSpace(elfPath) && !_context.Device.LoadElf(elfPath))
        {
            elfOk = false;
            errors.Add(_context.Device.LastError);
        }
        if (!string.IsNullOrWhiteSpace(svdPath) && !_context.Device.LoadSvd(svdPath))
        {
            svdOk = false;
            errors.Add(_context.Device.LastError);
        }

        return Ok(new
        {
            elfLoaded = elfOk,
            svdLoaded = svdOk,
            symbolCount = _context.Device.Symbols.Count,
            peripheralCount = _context.Device.Svd?.Peripherals.Count ?? 0,
            errors,
        });
    }
}
