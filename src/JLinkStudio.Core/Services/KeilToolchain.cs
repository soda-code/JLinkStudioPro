using System.Diagnostics;
using System.Text;

namespace JLinkStudio.Core.Services;

/// <summary>Keil 编译 / 下载的执行结果。</summary>
public sealed class KeilResult
{
    public bool Success { get; init; }
    public int ExitCode { get; init; }
    public string Action { get; init; } = string.Empty;
    public string ProjectPath { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public string Uv4Path { get; init; } = string.Empty;
    public long DurationMs { get; init; }

    /// <summary>编译/下载日志（过长时保留尾部）。</summary>
    public string Output { get; init; } = string.Empty;

    public List<string> Errors { get; init; } = new();
    public List<string> Warnings { get; init; } = new();

    /// <summary>UV4 退出码含义（0 无错无警告 / 1 警告 / 2 错误 …）。</summary>
    public string ExitCodeMeaning { get; init; } = string.Empty;
}

/// <summary>
/// Keil MDK 工具链封装：调用 UV4.exe 完成命令行编译与下载。
/// 默认安装目录 D:\Keil_V5，可在界面或接口里改成实际路径。
/// </summary>
public sealed class KeilToolchain
{
    /// <summary>默认 Keil 安装目录。</summary>
    public const string DefaultInstallPath = @"D:\Keil_V5";

    private readonly object _lock = new();

    private string _installPath = DefaultInstallPath;
    private string _projectPath = string.Empty;

    public KeilToolchain()
    {
        // 环境变量 KEIL_ROOT 存在时优先（多版本共存场景）
        string? fromEnvironment = Environment.GetEnvironmentVariable("KEIL_ROOT");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && Directory.Exists(fromEnvironment))
            _installPath = fromEnvironment.Trim();
    }

    /// <summary>Keil 安装目录（不含 UV4 子目录），默认 D:\Keil_V5。</summary>
    public string InstallPath
    {
        get { lock (_lock) return _installPath; }
        set
        {
            string path = (value ?? string.Empty).Trim().TrimEnd('\\', '/');
            if (path.Length == 0) path = DefaultInstallPath;
            lock (_lock) _installPath = path;
        }
    }

    /// <summary>最近一次使用的 Keil 工程文件（.uvprojx / .uvproj）。</summary>
    public string ProjectPath
    {
        get { lock (_lock) return _projectPath; }
        set { lock (_lock) _projectPath = (value ?? string.Empty).Trim(); }
    }

    /// <summary>UV4.exe 完整路径。</summary>
    public string Uv4Path
    {
        get
        {
            string install = InstallPath;

            // 允许用户直接填 UV4/UV4.exe 的完整路径
            if (install.EndsWith("UV4.exe", StringComparison.OrdinalIgnoreCase))
                return install;

            return Path.Combine(install, "UV4", "UV4.exe");
        }
    }

    public bool IsAvailable => File.Exists(Uv4Path);

    /// <summary>编译工程（UV4 -b）。</summary>
    public Task<KeilResult> BuildAsync(string? projectPath = null, string? target = null,
        int timeoutMs = 600_000, CancellationToken token = default)
        => RunAsync("-b", "build", projectPath, target, timeoutMs, token);

    /// <summary>重新编译（UV4 -r）。</summary>
    public Task<KeilResult> RebuildAsync(string? projectPath = null, string? target = null,
        int timeoutMs = 600_000, CancellationToken token = default)
        => RunAsync("-r", "rebuild", projectPath, target, timeoutMs, token);

    /// <summary>下载到目标 Flash（UV4 -f）。</summary>
    public Task<KeilResult> FlashAsync(string? projectPath = null, string? target = null,
        int timeoutMs = 600_000, CancellationToken token = default)
        => RunAsync("-f", "flash", projectPath, target, timeoutMs, token);

    /// <summary>编译后立即下载（先 -b 再 -f）。</summary>
    public async Task<KeilResult> BuildAndFlashAsync(string? projectPath = null, string? target = null,
        int timeoutMs = 600_000, CancellationToken token = default)
    {
        var build = await BuildAsync(projectPath, target, timeoutMs, token).ConfigureAwait(false);
        if (!build.Success) return build;

        var flash = await FlashAsync(projectPath, target, timeoutMs, token).ConfigureAwait(false);

        return new KeilResult
        {
            Success = flash.Success,
            ExitCode = flash.ExitCode,
            Action = "build+flash",
            ProjectPath = flash.ProjectPath,
            Target = flash.Target,
            Uv4Path = flash.Uv4Path,
            DurationMs = build.DurationMs + flash.DurationMs,
            Output = build.Output + Environment.NewLine + flash.Output,
            Errors = build.Errors.Concat(flash.Errors).ToList(),
            Warnings = build.Warnings.Concat(flash.Warnings).ToList(),
            ExitCodeMeaning = flash.ExitCodeMeaning,
        };
    }

    private async Task<KeilResult> RunAsync(string uv4Switch, string action, string? projectPath,
        string? target, int timeoutMs, CancellationToken token)
    {
        string project = string.IsNullOrWhiteSpace(projectPath) ? ProjectPath : projectPath.Trim();

        if (string.IsNullOrWhiteSpace(project))
            return Fail(action, "", "未指定 Keil 工程文件（.uvprojx），请在设置里选择或在请求里传 projectPath");

        if (!File.Exists(project))
            return Fail(action, project, $"工程文件不存在：{project}");

        string uv4 = Uv4Path;
        if (!File.Exists(uv4))
        {
            return Fail(action, project,
                $"未找到 UV4.exe：{uv4}。请在设置里填写正确的 Keil 安装目录（默认 {DefaultInstallPath}）");
        }

        // UV4 的 -o 参数要求日志文件事先存在，否则不会写入
        string logPath = Path.Combine(Path.GetTempPath(), $"keil_{Guid.NewGuid():N}.log");
        try
        {
            File.WriteAllText(logPath, string.Empty, Encoding.UTF8);

            var args = new StringBuilder();
            args.Append(uv4Switch).Append(" \"").Append(project).Append('"');
            args.Append(" -j0");                       // 静默运行，不弹 IDE 窗口
            args.Append(" -o \"").Append(logPath).Append('"');
            if (!string.IsNullOrWhiteSpace(target))
                args.Append(" -t \"").Append(target.Trim()).Append('"');

            var startInfo = new ProcessStartInfo
            {
                FileName = uv4,
                Arguments = args.ToString(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(project) ?? Directory.GetCurrentDirectory(),
            };

            var stopwatch = Stopwatch.StartNew();
            using var process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException("无法启动 UV4.exe");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(token);
            var stderrTask = process.StandardError.ReadToEndAsync(token);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(Math.Clamp(timeoutMs, 5_000, 1_800_000));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                return Fail(action, project, $"{action} 超时（>{timeoutMs} ms），已终止 UV4");
            }

            stopwatch.Stop();

            string log = ReadLog(logPath);
            string stdout = await SafeReadAsync(stdoutTask).ConfigureAwait(false);
            string stderr = await SafeReadAsync(stderrTask).ConfigureAwait(false);

            if (log.Length == 0) log = (stdout + Environment.NewLine + stderr).Trim();

            return new KeilResult
            {
                Success = process.ExitCode == 0,
                ExitCode = process.ExitCode,
                Action = action,
                ProjectPath = project,
                Target = target ?? string.Empty,
                Uv4Path = uv4,
                DurationMs = stopwatch.ElapsedMilliseconds,
                Output = Truncate(log, 20_000),
                Errors = Extract(log, "error"),
                Warnings = Extract(log, "warning"),
                ExitCodeMeaning = DescribeExitCode(process.ExitCode),
            };
        }
        catch (Exception ex)
        {
            return Fail(action, project, $"调用 UV4 失败：{ex.Message}");
        }
        finally
        {
            try { if (File.Exists(logPath)) File.Delete(logPath); } catch { }
        }
    }

    private KeilResult Fail(string action, string project, string message) => new()
    {
        Success = false,
        ExitCode = -1,
        Action = action,
        ProjectPath = project,
        Uv4Path = Uv4Path,
        Output = message,
        Errors = new List<string> { message },
        ExitCodeMeaning = "未执行",
    };

    private static async Task<string> SafeReadAsync(Task<string> task)
    {
        try { return await task.ConfigureAwait(false); }
        catch { return string.Empty; }
    }

    private static string ReadLog(string path)
    {
        try
        {
            if (!File.Exists(path)) return string.Empty;

            // UV4 输出为本地代码页，直接按默认编码读取即可
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.Default, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd().Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static List<string> Extract(string text, string keyword)
    {
        var items = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return items;

        // 只认「error: / warning:」形式的诊断行；
        // 汇总行「0 Error(s), 0 Warning(s).」不是真正的错误，必须排除
        string tag = keyword + ":";

        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (!trimmed.Contains(tag, StringComparison.OrdinalIgnoreCase)) continue;
            if (trimmed.Contains("Error(s)", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("Warning(s)", StringComparison.OrdinalIgnoreCase)) continue;

            items.Add(trimmed);
        }

        return items.Count > 200 ? items.GetRange(0, 200) : items;
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : "…（日志过长，仅保留尾部）" + Environment.NewLine + text[^max..];

    private static string DescribeExitCode(int code) => code switch
    {
        0 => "成功（无错误、无警告）",
        1 => "成功但有警告",
        2 => "编译/链接错误",
        3 => "致命错误",
        11 => "无法打开工程文件",
        12 => "器件/目标不存在",
        13 => "无法打开输出文件",
        15 => "调试驱动错误",
        20 => "license 问题",
        _ => $"UV4 退出码 {code}",
    };
}
