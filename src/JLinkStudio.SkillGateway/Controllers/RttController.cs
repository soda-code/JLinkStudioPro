using JLinkStudio.Core.Models;
using JLinkStudio.Core.Services;
using JLinkStudio.SkillGateway.Models;
using Microsoft.AspNetCore.Mvc;

namespace JLinkStudio.SkillGateway.Controllers;

/// <summary>RTT 日志查询与下行指令注入接口。</summary>
[ApiController]
[Route("api/v1/rtt")]
public sealed class RttController : ControllerBase
{
    private readonly StudioContext _context;

    public RttController(StudioContext context) => _context = context;

    /// <summary>拉取最近 N 条 RTT 日志，支持正则过滤与级别过滤。</summary>
    [HttpGet("logs")]
    public IActionResult Logs([FromQuery] int channel = 0, [FromQuery] int lastLines = 200,
        [FromQuery] string? regexFilter = null, [FromQuery] string? level = null)
    {
        RttLevel? minLevel = Enum.TryParse<RttLevel>(level, true, out var parsed) ? parsed : null;
        var lines = _context.Rtt.Query(channel, Math.Clamp(lastLines, 1, 5000), regexFilter, minLevel);

        return Ok(new
        {
            channel,
            count = lines.Count,
            lines = lines.Select(l => new RttLineDto
            {
                Timestamp = l.TimestampMs,
                Channel = l.Channel,
                Level = l.Level.ToString(),
                Text = l.Text,
                Fields = l.Fields,
            }),
        });
    }

    /// <summary>向目标板发送 RTT 命令（文本或 Hex）。</summary>
    [HttpPost("send")]
    public IActionResult Send([FromBody] RttSendRequest request)
    {
        bool ok = request.Hex
            ? _context.Rtt.SendHex(request.Text, request.Channel)
            : _context.Rtt.Send(request.Text, request.Channel, request.AppendNewLine);

        _context.Audit.Add("skill", "RTT-SEND", "/api/v1/rtt/send",
            $"Ch{request.Channel}: {request.Text}", 0, ok);

        return ok ? Ok(new { success = true }) : StatusCode(409, new { success = false, error = "发送失败，目标未连接。" });
    }

    /// <summary>清空 RTT 缓冲（-1 表示全部通道）。</summary>
    [HttpPost("clear")]
    public IActionResult Clear([FromQuery] int channel = -1)
    {
        _context.Rtt.Clear(channel);
        return Ok(new { success = true, channel });
    }

    /// <summary>RTT 运行状态统计。</summary>
    [HttpGet("status")]
    public IActionResult Status() => Ok(new
    {
        running = _context.Rtt.IsRunning,
        totalLines = _context.Rtt.TotalLines,
        totalBytes = _context.Rtt.TotalBytes,
        controlBlock = _context.Rtt.ControlBlockAddress is uint addr ? $"0x{addr:X8}" : null,
        searchMode = _context.Device.Config.RttSearchMode.ToString(),
        error = _context.Rtt.LastError,
    });
}
