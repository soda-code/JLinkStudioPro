using System.Diagnostics;
using JLinkStudio.Core.Services;
using JLinkStudio.SkillGateway.Models;
using Microsoft.AspNetCore.Mvc;

namespace JLinkStudio.SkillGateway.Controllers;

/// <summary>
/// 固件烧写接口：与界面「程序下载」页等价，走 J-Link 内置 Flash 算法（擦除 + 编程 + 校验）。
/// </summary>
[ApiController]
[Route("api/v1/flash")]
public sealed class FlashController : ControllerBase
{
    /// <summary>与界面「程序下载」页保持一致的默认起始地址。</summary>
    private const uint DefaultFlashAddress = 0x0800_0000;

    private static readonly string[] SupportedExtensions =
        [".bin", ".hex", ".elf", ".axf", ".mot", ".srec"];

    private readonly StudioContext _context;

    public FlashController(StudioContext context) => _context = context;

    /// <summary>烧写前可查：支持的格式、当前型号、连接状态与默认地址。</summary>
    [HttpGet("status")]
    public IActionResult Status() => Ok(new
    {
        connected = _context.Device.IsConnected,
        device = _context.Device.Config.Device,
        inspectMode = _context.IsInspectMode,
        defaultAddress = $"0x{DefaultFlashAddress:X8}",
        supportedExtensions = SupportedExtensions,
        binaryNeedsAddress = true,
    });

    /// <summary>
    /// 把固件烧写到目标 Flash。.bin 必须给 address，.hex/.elf/.axf/.mot/.srec 用文件内地址。
    /// 烧写完成后可按 reset 开关复位并运行。
    /// </summary>
    [HttpPost("download")]
    public async Task<IActionResult> Download([FromBody] FlashDownloadRequest request, CancellationToken token)
    {
        if (_context.IsInspectMode)
            return StatusCode(403, new { success = false, error = "查看模式已启用：禁止烧写固件。" });

        if (string.IsNullOrWhiteSpace(request.FilePath))
            return BadRequest(new { success = false, error = "filePath 不能为空" });

        string path;
        try
        {
            path = Path.GetFullPath(request.FilePath.Trim());
        }
        catch (Exception ex)
        {
            return BadRequest(new { success = false, error = $"filePath 无效：{ex.Message}" });
        }

        if (!System.IO.File.Exists(path))
            return BadRequest(new { success = false, error = $"固件文件不存在：{path}" });

        string extension = Path.GetExtension(path);
        if (!SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return BadRequest(new
            {
                success = false,
                error = $"不支持的固件格式 {extension}，支持 {string.Join(" / ", SupportedExtensions)}",
            });

        bool isBinary = extension.Equals(".bin", StringComparison.OrdinalIgnoreCase);
        uint address = 0;
        if (!string.IsNullOrWhiteSpace(request.Address) &&
            !TryParseAddress(request.Address!, out address))
            return BadRequest(new { success = false, error = $"address 格式无效：{request.Address}（示例 0x08000000）" });

        if (isBinary && address == 0)
            return BadRequest(new { success = false, error = ".bin 固件必须提供 address（示例 0x08000000）" });

        var link = _context.Device.Link;
        if (link == null || !link.IsConnected)
            return StatusCode(409, new { success = false, error = "目标未连接，请先调用 /api/v1/session/config 连接。" });

        var stopwatch = Stopwatch.StartNew();
        var (ok, error) = await Task.Run(() =>
        {
            bool done = link.DownloadFile(path, address, out string message);
            return (done, message);
        }, token);

        string resetError = string.Empty;
        bool resetOk = true;
        if (ok && request.Reset)
            resetOk = _context.Device.Reset(out resetError) && _context.Device.Go(out resetError);

        stopwatch.Stop();

        string detail = $"{(isBinary ? $"0x{address:X8} " : string.Empty)}{Path.GetFileName(path)}"
                        + $" · {stopwatch.ElapsedMilliseconds}ms"
                        + (ok ? (request.Reset ? (resetOk ? " · 已复位运行" : $" · 复位失败：{resetError}") : " · 未复位") : $" · {error}");

        _context.LogSession(ok ? $"程序下载成功（skill）：{detail}" : $"程序下载失败（skill）：{detail}");
        _context.Audit.Add("skill", "DOWNLOAD", "/api/v1/flash/download", detail,
            stopwatch.ElapsedMilliseconds, ok);

        if (!ok)
            return StatusCode(400, new
            {
                success = false,
                file = path,
                address = isBinary ? $"0x{address:X8}" : null,
                elapsedMs = stopwatch.ElapsedMilliseconds,
                error,
            });

        return Ok(new
        {
            success = true,
            file = path,
            address = isBinary ? $"0x{address:X8}" : null,
            elapsedMs = stopwatch.ElapsedMilliseconds,
            reset = request.Reset,
            running = request.Reset && resetOk,
            resetError = resetError.Length > 0 ? resetError : null,
        });
    }

    private static bool TryParseAddress(string text, out uint address)
    {
        address = 0;
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out address);
        return uint.TryParse(text, out address);
    }
}
