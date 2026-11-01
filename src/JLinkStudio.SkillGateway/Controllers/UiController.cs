using JLinkStudio.Core.Models;
using JLinkStudio.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace JLinkStudio.SkillGateway.Controllers;

/// <summary>
/// 界面交互接口：需要人工介入时让上位机弹框提示用户。
/// 仅在「无法通过命令解决」的场景使用（按复位键、重新上电、检查接线等），避免频繁打扰。
/// </summary>
[ApiController]
[Route("api/v1/ui")]
public sealed class UiController : ControllerBase
{
    private readonly StudioContext _context;

    public UiController(StudioContext context) => _context = context;

    /// <summary>界面是否就绪（能弹框）。无界面时为 false，Skill 应改用文字说明告知用户。</summary>
    [HttpGet("state")]
    public IActionResult State() => Ok(new
    {
        uiReady = _context.PromptHandler != null,
        hint = _context.PromptHandler != null
            ? "可以弹框"
            : "界面未就绪，请把需要人工操作的步骤直接告诉用户",
    });

    /// <summary>
    /// 让上位机弹框提示用户。
    /// waitForConfirm=true（默认）时等待用户点按钮，返回 confirmed；超过 timeoutMs 返回 timeout=true。
    /// </summary>
    [HttpPost("prompt")]
    public async Task<IActionResult> Prompt([FromBody] UiPromptRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { error = "message 不能为空" });

        if (_context.PromptHandler == null)
        {
            _context.Audit.Add("skill", "PROMPT-SKIPPED", "/api/v1/ui/prompt",
                request.Message, 0, false);
            return Ok(new
            {
                displayed = false,
                reason = "界面未就绪（无窗口或正在关闭），请用文字告知用户",
                confirmed = false,
            });
        }

        var promptTask = _context.PromptAsync(request);

        // 非阻塞：只提示，立即返回
        if (!request.WaitForConfirm)
        {
            _context.Audit.Add("skill", "PROMPT", "/api/v1/ui/prompt", "非阻塞：" + request.Message);
            return Ok(new { displayed = true, waited = false, confirmed = (bool?)null, selected = (string?)null });
        }

        int timeoutMs = Math.Clamp(request.TimeoutMs, 1000, 300_000);
        var completed = await Task.WhenAny(promptTask, Task.Delay(timeoutMs, token));

        if (completed != promptTask)
        {
            _context.Audit.Add("skill", "PROMPT", "/api/v1/ui/prompt",
                $"等待确认超时：{request.Message}", 0, false);
            return Ok(new { displayed = true, waited = true, timeout = true, confirmed = false });
        }

        var result = await promptTask;
        string choice = result.Selected is { Length: > 0 } picked ? $"用户选择「{picked}」" : "用户未选择";
        _context.Audit.Add("skill", "PROMPT", "/api/v1/ui/prompt", $"{request.Message} → {choice}", 0, result.Delivered);

        return Ok(new
        {
            displayed = result.Delivered,
            waited = true,
            timeout = false,
            confirmed = result.Confirmed,
            selected = result.Selected,
        });
    }
}
