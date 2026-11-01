using JLinkStudio.Core.Services;
using JLinkStudio.SkillGateway.Models;
using Microsoft.AspNetCore.Mvc;

namespace JLinkStudio.SkillGateway.Controllers;

/// <summary>
/// Keil MDK 工具链：查看/设置安装目录，命令行编译（UV4 -b）与下载（UV4 -f）。
/// </summary>
[ApiController]
[Route("api/v1/keil")]
public sealed class KeilController : ControllerBase
{
    private readonly StudioContext _context;

    public KeilController(StudioContext context) => _context = context;

    /// <summary>Keil 工具链状态：安装目录、UV4 是否存在、默认工程。</summary>
    [HttpGet("status")]
    public IActionResult Status() => Ok(new
    {
        installPath = _context.Keil.InstallPath,
        uv4Path = _context.Keil.Uv4Path,
        available = _context.Keil.IsAvailable,
        projectPath = _context.Keil.ProjectPath,
        defaultInstallPath = KeilToolchain.DefaultInstallPath,
        actions = new[] { "build (UV4 -b)", "rebuild (UV4 -r)", "flash (UV4 -f)", "build-flash (先编译再下载)" },
        hint = _context.Keil.IsAvailable
            ? "UV4.exe 已就绪，可直接 build / flash"
            : $"未找到 UV4.exe，请用 POST /api/v1/keil/config 设置 installPath（默认 {KeilToolchain.DefaultInstallPath}）",
    });

    /// <summary>设置 Keil 安装目录与默认工程（可在界面「设置 → 设备与工程配置」里选）。</summary>
    [HttpPost("config")]
    public IActionResult Configure([FromBody] KeilConfigRequest request)
    {
        var applied = new List<string>();
        var errors = new List<string>();

        if (!string.IsNullOrWhiteSpace(request.InstallPath))
        {
            string path = request.InstallPath.Trim().TrimEnd('\\', '/');

            // 允许用户直接给 UV4.exe 路径，自动纠正到安装目录层级
            if (path.EndsWith("UV4.exe", StringComparison.OrdinalIgnoreCase))
            {
                var directory = Path.GetDirectoryName(path);
                if (directory != null && Path.GetFileName(directory).Equals("UV4", StringComparison.OrdinalIgnoreCase))
                    path = Path.GetDirectoryName(directory) ?? path;
            }

            if (!Directory.Exists(path))
                errors.Add($"目录不存在：{path}");

            _context.Keil.InstallPath = path;
            applied.Add($"installPath={_context.Keil.InstallPath}");
        }

        if (!string.IsNullOrWhiteSpace(request.ProjectPath))
        {
            string project = request.ProjectPath.Trim();
            if (!System.IO.File.Exists(project))
                errors.Add($"工程文件不存在：{project}");

            _context.Keil.ProjectPath = project;
            applied.Add($"projectPath={project}");
        }

        _context.Audit.Add("skill", "KEIL-CONFIG", "/api/v1/keil/config",
            string.Join(", ", applied), 0, errors.Count == 0);

        return Ok(new
        {
            success = errors.Count == 0,
            applied,
            errors,
            installPath = _context.Keil.InstallPath,
            uv4Path = _context.Keil.Uv4Path,
            available = _context.Keil.IsAvailable,
            projectPath = _context.Keil.ProjectPath,
        });
    }

    /// <summary>命令行编译工程（UV4 -b，默认目标；可在请求里指定 target）。</summary>
    [HttpPost("build")]
    public Task<IActionResult> Build([FromBody] KeilActionRequest request, CancellationToken token)
        => RunAsync("build", (p, t, ms) => _context.Keil.BuildAsync(p, t, ms, token), request, token);

    /// <summary>重新完整编译（UV4 -r）。</summary>
    [HttpPost("rebuild")]
    public Task<IActionResult> Rebuild([FromBody] KeilActionRequest request, CancellationToken token)
        => RunAsync("rebuild", (p, t, ms) => _context.Keil.RebuildAsync(p, t, ms, token), request, token);

    /// <summary>下载到目标 Flash（UV4 -f）。查看模式下拒绝执行。</summary>
    [HttpPost("flash")]
    public Task<IActionResult> Flash([FromBody] KeilActionRequest request, CancellationToken token)
        => RunAsync("flash", (p, t, ms) => _context.Keil.FlashAsync(p, t, ms, token), request, token);

    /// <summary>先编译成功后自动下载。</summary>
    [HttpPost("build-flash")]
    public Task<IActionResult> BuildFlash([FromBody] KeilActionRequest request, CancellationToken token)
        => RunAsync("build+flash", (p, t, ms) => _context.Keil.BuildAndFlashAsync(p, t, ms, token), request, token);

    private async Task<IActionResult> RunAsync(string action,
        Func<string?, string?, int, Task<KeilResult>> runner, KeilActionRequest request, CancellationToken token)
    {
        // 下载会改写目标 Flash：查看模式与写保护关闭时一律拒绝
        bool writesTarget = action.Contains("flash", StringComparison.OrdinalIgnoreCase);
        if (writesTarget && _context.IsInspectMode)
            return StatusCode(403, new { error = "查看模式已启用：禁止下载程序，请先切回调试模式。" });

        if (!_context.Keil.IsAvailable)
        {
            return StatusCode(412, new
            {
                error = $"未找到 UV4.exe：{_context.Keil.Uv4Path}",
                fix = $"请先设置 Keil 安装目录：POST /api/v1/keil/config {{\"installPath\":\"{KeilToolchain.DefaultInstallPath}\"}}",
            });
        }

        int timeout = Math.Clamp(request.TimeoutMs, 5_000, 1_800_000);
        var result = await runner(request.ProjectPath, request.Target, timeout).ConfigureAwait(false);

        // 记录当前工程，方便后续不传 projectPath
        if (result.Success || result.ExitCode >= 0)
            _context.Keil.ProjectPath = result.ProjectPath;

        _context.Audit.Add("skill", $"KEIL-{action.ToUpperInvariant()}", $"/api/v1/keil/{action}",
            $"{Path.GetFileName(result.ProjectPath)} · {result.ExitCodeMeaning}", result.DurationMs, result.Success);

        return Ok(new
        {
            success = result.Success,
            action = result.Action,
            projectPath = result.ProjectPath,
            target = result.Target,
            uv4Path = result.Uv4Path,
            exitCode = result.ExitCode,
            exitCodeMeaning = result.ExitCodeMeaning,
            durationMs = result.DurationMs,
            errors = result.Errors,
            warnings = result.Warnings,
            output = result.Output,
        });
    }
}
