using JLinkStudio.Core.Models;
using JLinkStudio.Core.Services;
using JLinkStudio.SkillGateway.Models;
using Microsoft.AspNetCore.Mvc;

namespace JLinkStudio.SkillGateway.Controllers;

/// <summary>Skill 管控中心接口：审计流、白名单与安全写保护策略。</summary>
[ApiController]
[Route("api/v1/skill")]
public sealed class SkillController : ControllerBase
{
    private readonly StudioContext _context;

    public SkillController(StudioContext context) => _context = context;

    /// <summary>最近的 Skill 调用审计记录。</summary>
    [HttpGet("audit")]
    public IActionResult Audit([FromQuery] int count = 100) =>
        Ok(_context.Audit.Recent(Math.Clamp(count, 1, 2000)).Select(a => new
        {
            timestamp = a.TimestampMs,
            time = a.TimeText,
            source = a.Source,
            method = a.Method,
            path = a.Path,
            detail = a.Detail,
            success = a.Success,
            elapsedMs = Math.Round(a.ElapsedMs, 2),
        }));

    /// <summary>查询当前安全策略。</summary>
    [HttpGet("policy")]
    public IActionResult GetPolicy() => Ok(new
    {
        writeEnabled = _context.Security.WriteEnabled,
        guards = _context.Security.Guards.Select(g => new
        {
            pattern = g.Pattern,
            min = g.Min,
            max = g.Max,
            allowWrite = g.AllowWrite,
        }),
        exposedSymbols = _context.Security.ExposedSymbols,
    });

    /// <summary>更新安全策略（总开关 / 变量范围 / 白名单）。</summary>
    [HttpPost("policy")]
    public IActionResult SetPolicy([FromBody] SkillPolicyRequest request)
    {
        if (request.WriteEnabled.HasValue) _context.Security.WriteEnabled = request.WriteEnabled.Value;

        if (request.Guards != null)
        {
            _context.Security.SetGuards(request.Guards.Select(g => new WriteGuard
            {
                Pattern = g.Pattern,
                Min = g.Min,
                Max = g.Max,
                AllowWrite = g.AllowWrite,
            }));
        }

        if (request.ExposedSymbols != null)
        {
            foreach (var symbol in _context.Security.ExposedSymbols.ToList())
                _context.Security.SetExposed(symbol, false);
            foreach (var symbol in request.ExposedSymbols)
                _context.Security.SetExposed(symbol, true);
        }

        _context.Audit.Add("skill", "POLICY", "/api/v1/skill/policy", "安全策略已更新");
        return GetPolicy();
    }

    /// <summary>Skill 网关心跳与能力声明（便于 Agent 自动发现）。</summary>
    [HttpGet("manifest")]
    public IActionResult Manifest() => Ok(new
    {
        name = "JLink-Studio-Pro",
        version = "1.0.0",
        protocol = new[] { "rest", "websocket" },
        capabilities = new[]
        {
            "session.config", "session.settings", "session.control", "session.devices",
            "session.logs", "flash.download", "flash.status",
            "variables.all", "variables.search", "variables.read", "variables.write",
            "variables.write-batch", "variables.capture-timeseries", "variables.watch",
            "variables.unwatch", "variables.history", "registers.read", "registers.write-field",
            "registers.fault-snapshot", "data.snapshot", "data.registers", "data.memory",
            "rtt.logs", "rtt.send", "ui.prompt", "telemetry.stream",
            "keil.config", "keil.build", "keil.flash",
        },
        endpoints = new
        {
            status = "/api/v1/session/status",
            sessionLogs = "/api/v1/session/logs?take=200",
            config = "/api/v1/session/config",
            settingsGet = "/api/v1/session/settings",
            settingsSet = "/api/v1/session/settings",
            devices = "/api/v1/session/devices",
            flashStatus = "/api/v1/flash/status",
            flashDownload = "/api/v1/flash/download",
            dataSnapshot = "/api/v1/data/snapshot",
            dataRegisters = "/api/v1/data/registers",
            dataMemory = "/api/v1/data/memory?address=0x20000000&length=64",
            variablesAll = "/api/v1/variables/all?offset=0&limit=200",
            variablesSearch = "/api/v1/variables/search?keyword=",
            variablesRead = "/api/v1/variables/read",
            variablesWrite = "/api/v1/variables/write",
            variablesWriteBatch = "/api/v1/variables/write-batch",
            variablesWatch = "/api/v1/variables/watch",
            variablesWatchlist = "/api/v1/variables/watchlist",
            variablesHistory = "/api/v1/variables/history?names=g_pid.kp,g_pid.ki",
            variablesHistoryClear = "/api/v1/variables/history/clear",
            variablesUnwatch = "/api/v1/variables/unwatch",
            capture = "/api/v1/variables/capture-timeseries",
            registers = "/api/v1/registers/peripheral/{name}",
            registersAll = "/api/v1/registers/peripherals",
            faultSnapshot = "/api/v1/registers/fault-snapshot",
            rttLogs = "/api/v1/rtt/logs",
            rttStatus = "/api/v1/rtt/status",
            uiPrompt = "/api/v1/ui/prompt",
            uiState = "/api/v1/ui/state",
            policy = "/api/v1/skill/policy",
            audit = "/api/v1/skill/audit",
            keilStatus = "/api/v1/keil/status",
            keilConfig = "/api/v1/keil/config",
            keilBuild = "/api/v1/keil/build",
            keilFlash = "/api/v1/keil/flash",
            websocket = "/ws/stream",
            swagger = "/swagger",
        },
        examples = new
        {
            writeBatch = new
            {
                writes = new[]
                {
                    new { name = "g_pid.kp", values = new[] { 1.0, 2.0, 3.0 } },
                },
                intervalMs = 10,
                readBack = true,
            },
            snapshot = "/api/v1/data/snapshot?logs=50&registers=GPIOA.MODER,RCC.CR",
            captureOne = "/api/v1/variables/capture-timeseries {names:[one],sampleRateHz:1000,durationMs:2000}",
            captureMany = "/api/v1/variables/capture-timeseries {names:[a,b,c],sampleRateHz:500,durationMs:3000}",
            memory = "/api/v1/data/memory?address=0x20000000&length=64",
        },
    });
}
