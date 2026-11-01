using JLinkStudio.Core.Models;

namespace JLinkStudio.Core.Services;

/// <summary>连接失败诊断结果：把底层错误码/错误文本翻译成可执行的自查建议。</summary>
public sealed class ConnectionDiagnosis
{
    public string Summary { get; init; } = string.Empty;
    public IReadOnlyList<string> Causes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Suggestions { get; init; } = Array.Empty<string>();

    public IEnumerable<string> ToLogLines()
    {
        yield return $"连接异常分析：{Summary}";
        foreach (var cause in Causes) yield return $"  · 可能原因：{cause}";
        foreach (var suggestion in Suggestions) yield return $"  · 建议：{suggestion}";
    }
}

/// <summary>
/// J-Link 连接异常分析：结合驱动状态、调试器枚举结果、错误文本与当前连接参数，
/// 给出分层的原因判断与处理建议（供连接浮层、运行日志与 Skill 接口复用）。
/// </summary>
public static class ConnectionDiagnostics
{
    public static ConnectionDiagnosis Analyze(
        DeviceSessionConfig config,
        string error,
        int emulatorCount,
        bool driverAvailable)
    {
        var causes = new List<string>();
        var suggestions = new List<string>();
        string text = error ?? string.Empty;

        bool driverMissing = !driverAvailable || text.Contains("未找到 J-Link 驱动", StringComparison.Ordinal);
        bool emulatorMissing = emulatorCount <= 0 ||
                               text.Contains("未检测到调试器", StringComparison.Ordinal) ||
                               text.Contains("打开失败", StringComparison.Ordinal) ||
                               text.Contains("JLINKARM_OpenEx", StringComparison.OrdinalIgnoreCase);
        bool targetMissing = text.Contains("目标未连接", StringComparison.Ordinal) ||
                             text.Contains("Could not connect", StringComparison.OrdinalIgnoreCase) ||
                             text.Contains("RDDI", StringComparison.OrdinalIgnoreCase);
        bool speedTooHigh = config.SpeedKhz > 4000;
        bool deviceSuspect = string.IsNullOrWhiteSpace(config.Device);

        if (driverMissing)
        {
            causes.Add("未加载 SEGGER J-Link SDK（JLink_x64.dll）");
            suggestions.Add("在「设置 → 驱动与关于」中选择 J-Link 安装目录，再重新连接");
        }

        if (emulatorMissing)
        {
            causes.Add("上位机未枚举到调试器，或调试器被其它进程独占");
            suggestions.Add("确认 USB 线已插好、驱动正常（设备管理器中出现 J-Link）");
            suggestions.Add("关闭可能占用 J-Link 的软件：J-Link Commander、Keil、IAR、Ozone 等");
        }

        if (targetMissing)
        {
            causes.Add("调试口握手失败：目标未供电、SWD 接线错误或芯片处于低功耗/读保护状态");
            suggestions.Add("确认目标板已上电，且 GND 与调试器共地");
            if (config.Interface == JLinkInterface.JTAG)
                suggestions.Add("若目标板只引出 SWD 引脚，请把连接方式改为 SWD");
            suggestions.Add("尝试按住复位键点击连接，或使用 Connect under reset");
        }

        if (speedTooHigh)
        {
            causes.Add($"当前通信速率 {config.SpeedKhz}kHz 对目标板/排线可能过高");
            suggestions.Add("先把速率降到 100 ~ 500kHz 建立连接，成功后再逐级提高");
        }

        if (deviceSuspect)
        {
            causes.Add("未指定目标芯片型号，J-Link 无法加载对应的 Flash/RAM 算法");
            suggestions.Add("点击「自动识别」自动探测型号，或从「常用型号」中选择");
        }

        if (causes.Count == 0)
        {
            causes.Add("非典型错误，可能是驱动版本与调试器固件不匹配");
            suggestions.Add("升级 SEGGER J-Link 软件包，并复位调试器后重试");
        }

        if (string.IsNullOrWhiteSpace(text))
            causes.Insert(0, "连接过程被中断或返回空错误信息");

        return new ConnectionDiagnosis
        {
            Summary = $"{causes[0]}（原始错误：{Truncate(text)}）",
            Causes = causes,
            Suggestions = suggestions.Distinct().ToList(),
        };
    }

    private static string Truncate(string text)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 120 ? text : text[..120] + "…";
    }
}
