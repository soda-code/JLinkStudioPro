using System.Diagnostics;
using JLinkStudio.Core.Models;
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
    /// <summary>与界面「程序下载」页保持一致的默认起始地址（片内 Flash）。</summary>
    private const uint DefaultFlashAddress = 0x0800_0000;

    /// <summary>片外存储器默认基址（QSPI 常见映射，实际随芯片与硬件而定）。</summary>
    private const uint DefaultExternalAddress = 0x9000_0000;

    private static readonly string[] SupportedExtensions =
        [".bin", ".hex", ".elf", ".axf", ".mot", ".srec"];

    private readonly StudioContext _context;

    public FlashController(StudioContext context) => _context = context;

    /// <summary>烧写前可查：支持的格式、下载模式、当前型号、连接状态与默认地址。</summary>
    [HttpGet("status")]
    public IActionResult Status() => Ok(new
    {
        connected = _context.Device.IsConnected,
        device = _context.Device.Config.Device,
        inspectMode = _context.IsInspectMode,
        defaultAddress = $"0x{DefaultFlashAddress:X8}",
        supportedExtensions = SupportedExtensions,
        binaryNeedsAddress = true,
        modes = new[]
        {
            new
            {
                mode = "internal", name = "片内 Flash", defaultAddress = $"0x{DefaultFlashAddress:X8}",
                note = "走芯片自带 Flash 算法，用当前型号即可",
            },
            new
            {
                mode = "external", name = "片外 Flash", defaultAddress = $"0x{DefaultExternalAddress:X8}",
                note = "需要带外部存储支持的 J-Link 型号（如 STM32H743XI_QSPI），留空 device 则沿用当前型号",
            },
        },
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

        string mode = (request.Mode ?? "internal").Trim().ToLowerInvariant();
        if (mode is not ("internal" or "external"))
            return BadRequest(new { success = false, error = $"mode 只支持 internal（片内）/ external（片外），当前 {request.Mode}" });

        bool external = mode == "external";

        uint externalBase = DefaultExternalAddress;
        if (!string.IsNullOrWhiteSpace(request.BaseAddress) &&
            !TryParseAddress(request.BaseAddress!, out externalBase))
            return BadRequest(new { success = false, error = $"baseAddress 格式无效：{request.BaseAddress}（示例 0x90000000）" });

        uint address = 0;
        if (!string.IsNullOrWhiteSpace(request.Address) &&
            !TryParseAddress(request.Address!, out address))
            return BadRequest(new { success = false, error = $"address 格式无效：{request.Address}（示例 0x08000000）" });

        // 片外 .bin 未给 address 时落到片外基址，省得调用方自己拼地址
        if (isBinary && address == 0)
        {
            if (external) address = externalBase;
            else return BadRequest(new { success = false, error = ".bin 固件必须提供 address（示例 0x08000000）" });
        }

        var link = _context.Device.Link;
        if (link == null || !link.IsConnected)
            return StatusCode(409, new { success = false, error = "目标未连接，请先调用 /api/v1/session/config 连接。" });

        string externalDevice = (request.Device ?? string.Empty).Trim();
        var options = new FlashDownloadOptions
        {
            Target = external ? FlashTarget.External : FlashTarget.Internal,
            ExternalDevice = externalDevice,
            ExternalBase = externalBase,
        };
        string modeLabel = external
            ? $"片外 Flash（{(externalDevice.Length == 0 ? "沿用当前型号" : externalDevice)}）"
            : "片内 Flash";

        var stopwatch = Stopwatch.StartNew();
        var (ok, error) = await Task.Run(() =>
        {
            bool done = link.DownloadFile(path, address, options, out string message);
            return (done, message);
        }, token);

        string resetError = string.Empty;
        bool resetOk = true;
        if (ok && request.Reset)
            resetOk = _context.Device.Reset(out resetError) && _context.Device.Go(out resetError);

        stopwatch.Stop();

        string detail = $"{(isBinary ? $"0x{address:X8} " : string.Empty)}{Path.GetFileName(path)}"
                        + $" · {modeLabel} · {stopwatch.ElapsedMilliseconds}ms"
                        + (ok ? (request.Reset ? (resetOk ? " · 已复位运行" : $" · 复位失败：{resetError}") : " · 未复位") : $" · {error}");

        _context.LogSession(ok ? $"程序下载成功（skill）：{detail}" : $"程序下载失败（skill）：{detail}");
        _context.Audit.Add("skill", "DOWNLOAD", "/api/v1/flash/download", detail,
            stopwatch.ElapsedMilliseconds, ok);

        if (!ok)
            return StatusCode(400, new
            {
                success = false,
                file = path,
                mode,
                device = externalDevice.Length == 0 ? null : externalDevice,
                address = isBinary ? $"0x{address:X8}" : null,
                elapsedMs = stopwatch.ElapsedMilliseconds,
                error,
            });

        return Ok(new
        {
            success = true,
            file = path,
            mode,
            device = externalDevice.Length == 0 ? null : externalDevice,
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
