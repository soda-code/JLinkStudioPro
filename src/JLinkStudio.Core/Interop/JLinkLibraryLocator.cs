using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace JLinkStudio.Core.Interop;

/// <summary>
/// J-Link SDK 动态库定位器：默认从 SEGGER 安装目录加载，使程序无需把 DLL 复制到输出目录。
/// 解析优先级：环境变量 → 默认安装目录 → Program Files 下最高版本 → x86 目录 → 程序目录 → 系统 PATH。
/// </summary>
public static class JLinkLibraryLocator
{
    public const string EnvironmentVariable = "JLINK_STUDIO_DLL_DIR";

#if X86
    public const string LibraryFileName = "JLinkARM.dll";
#else
    public const string LibraryFileName = "JLink_x64.dll";
#endif

    private static readonly object SyncRoot = new();
    private static readonly Regex VersionPattern = new(@"JLink_V(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string? _configuredDirectory;
    private static string? _resolvedDirectory;
    private static string? _resolvedDllPath;
    private static string? _versionLabel;
    private static bool _resolverInstalled;
    private static bool _loadAttempted;

    /// <summary>默认安装目录（SEGGER J-Link V9.48）。</summary>
    public static string DefaultInstallDirectory { get; set; } = @"D:\Program Files\SEGGER\JLink_V948";

    /// <summary>已解析到的 DLL 完整路径，未找到时为 null。</summary>
    public static string? DllPath
    {
        get
        {
            EnsureResolved();
            return _resolvedDllPath;
        }
    }

    /// <summary>已解析到的安装目录，未找到时为 null。</summary>
    public static string? Directory
    {
        get
        {
            EnsureResolved();
            return _resolvedDirectory;
        }
    }

    /// <summary>从目录名推导的版本标签，例如 V9.48。</summary>
    public static string? VersionLabel
    {
        get
        {
            EnsureResolved();
            return _versionLabel;
        }
    }

    public static bool IsAvailable => !string.IsNullOrEmpty(DllPath);

    /// <summary>手工指定安装目录（传 null 恢复自动探测）。</summary>
    public static void Configure(string? directory)
    {
        lock (SyncRoot)
        {
            _configuredDirectory = string.IsNullOrWhiteSpace(directory) ? null : directory.Trim();
            _resolvedDirectory = null;
            _resolvedDllPath = null;
            _versionLabel = null;
            _loadAttempted = false;
        }
    }

    /// <summary>把当前 DLL 目录注册为程序集的 DllImport 解析器。</summary>
    public static void EnsureResolverInstalled()
    {
        lock (SyncRoot)
        {
            if (_resolverInstalled) return;
            NativeLibrary.SetDllImportResolver(typeof(JLinkLibraryLocator).Assembly, ResolveLibrary);
            _resolverInstalled = true;
        }
    }

    /// <summary>尝试加载 DLL，用于“驱动是否可用”的判定。</summary>
    public static bool TryLoad()
    {
        lock (SyncRoot)
        {
            if (_loadAttempted) return _resolvedDllPath != null;
            _loadAttempted = true;

            try
            {
                EnsureResolverInstalled();
                var path = DllPath;
                if (path == null) return false;
                var handle = NativeLibrary.Load(path);
                return handle != IntPtr.Zero;
            }
            catch
            {
                return false;
            }
        }
    }

    private static IntPtr ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        bool isNativeName = libraryName.Equals("JLinkARM.dll", StringComparison.OrdinalIgnoreCase)
                            || libraryName.Equals("JLink_x64.dll", StringComparison.OrdinalIgnoreCase);
        if (!isNativeName) return IntPtr.Zero;

        var path = DllPath;
        if (path == null) return IntPtr.Zero;

        return NativeLibrary.TryLoad(path, out var handle) ? handle : IntPtr.Zero;
    }

    private static void EnsureResolved()
    {
        if (_resolvedDllPath != null) return;

        lock (SyncRoot)
        {
            if (_resolvedDllPath != null) return;

            foreach (var directory in EnumerateCandidateDirectories())
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;
                var candidate = Path.Combine(directory, LibraryFileName);
                if (!File.Exists(candidate)) continue;

                _resolvedDirectory = directory;
                _resolvedDllPath = candidate;
                _versionLabel = DeriveVersionLabel(directory);
                return;
            }
        }
    }

    private static IEnumerable<string> EnumerateCandidateDirectories()
    {
        if (!string.IsNullOrWhiteSpace(_configuredDirectory))
            yield return _configuredDirectory!;

        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            yield return fromEnvironment!;

        if (!string.IsNullOrWhiteSpace(DefaultInstallDirectory))
            yield return DefaultInstallDirectory;

        foreach (var latest in EnumerateInstalledVersions(Environment.SpecialFolder.ProgramFiles))
            yield return latest;

        foreach (var latest in EnumerateInstalledVersions(Environment.SpecialFolder.ProgramFilesX86))
            yield return latest;

        yield return AppContext.BaseDirectory;
    }

    /// <summary>枚举 %ProgramFiles%\SEGGER\JLink_V* 并按版本号从高到低排序。</summary>
    private static IEnumerable<string> EnumerateInstalledVersions(Environment.SpecialFolder folder)
    {
        List<(int Version, string Path)> versions = new();
        try
        {
            var root = Environment.GetFolderPath(folder);
            if (string.IsNullOrWhiteSpace(root)) return Array.Empty<string>();

            var segger = Path.Combine(root, "SEGGER");
            if (!System.IO.Directory.Exists(segger)) return Array.Empty<string>();

            foreach (var directory in System.IO.Directory.GetDirectories(segger))
            {
                var match = VersionPattern.Match(Path.GetFileName(directory));
                if (!match.Success) continue;
                versions.Add((int.Parse(match.Groups[1].Value), directory));
            }
        }
        catch
        {
            return Array.Empty<string>();
        }

        return versions.OrderByDescending(v => v.Version).Select(v => v.Path).ToList();
    }

    private static string? DeriveVersionLabel(string directory)
    {
        var match = VersionPattern.Match(Path.GetFileName(directory.TrimEnd('\\', '/')));
        if (!match.Success) return null;

        var digits = match.Groups[1].Value;
        if (digits.Length < 3) return $"V{digits}";

        var major = digits[..^2];
        var minor = digits[^2..];
        return $"V{major}.{minor}";
    }
}
