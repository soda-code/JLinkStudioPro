using JLinkStudio.Core.Models;

namespace JLinkStudio.Core.Services;

/// <summary>Skill 调用审计日志（内存环形保留，供界面与 API 查询）。</summary>
public sealed class SkillAuditLog
{
    private const int MaxEntries = 2000;
    private readonly List<SkillAuditEntry> _entries = new();
    private readonly object _lock = new();

    public event EventHandler<SkillAuditEntry>? EntryAdded;

    public void Add(SkillAuditEntry entry)
    {
        lock (_lock)
        {
            _entries.Add(entry);
            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(0, _entries.Count - MaxEntries);
        }
        EntryAdded?.Invoke(this, entry);
    }

    public void Add(string source, string method, string path, string detail, double elapsedMs = 0, bool success = true) =>
        Add(new SkillAuditEntry
        {
            Source = source,
            Method = method,
            Path = path,
            Detail = detail,
            ElapsedMs = elapsedMs,
            Success = success,
        });

    public IReadOnlyList<SkillAuditEntry> Recent(int count = 200)
    {
        lock (_lock)
        {
            int skip = Math.Max(0, _entries.Count - count);
            return _entries.GetRange(skip, _entries.Count - skip);
        }
    }
}

/// <summary>
/// Skill 安全策略：白名单 + 写入范围保护。
/// 防止外部 AI 在参数整定时写入越界值导致硬件损坏。
/// </summary>
public sealed class SkillSecurityPolicy
{
    private readonly object _lock = new();
    private readonly List<WriteGuard> _guards = new();
    private readonly HashSet<string> _exposed = new(StringComparer.OrdinalIgnoreCase);

    public SkillSecurityPolicy()
    {
        WriteEnabled = true;
        _guards.Add(new WriteGuard { Pattern = "g_motorPid.kp", Min = 0, Max = 10 });
        _guards.Add(new WriteGuard { Pattern = "g_motorPid.ki", Min = 0, Max = 5 });
        _guards.Add(new WriteGuard { Pattern = "g_motorPid.kd", Min = 0, Max = 5 });
        _guards.Add(new WriteGuard { Pattern = "g_targetSpeed", Min = -5000, Max = 5000 });
    }

    /// <summary>总写保护开关（关闭后 Skill 只能读）。</summary>
    public bool WriteEnabled { get; set; }

    public event EventHandler? PolicyChanged;

    public IReadOnlyList<WriteGuard> Guards
    {
        get
        {
            lock (_lock) return _guards.ToList();
        }
    }

    public IReadOnlyCollection<string> ExposedSymbols
    {
        get
        {
            lock (_lock) return _exposed.ToList();
        }
    }

    public void SetGuards(IEnumerable<WriteGuard> guards)
    {
        lock (_lock)
        {
            _guards.Clear();
            _guards.AddRange(guards);
        }
        PolicyChanged?.Invoke(this, EventArgs.Empty);
    }

    public void AddGuard(WriteGuard guard)
    {
        lock (_lock) _guards.Add(guard);
        PolicyChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetExposed(string symbolPath, bool exposed, bool writable = true)
    {
        lock (_lock)
        {
            if (exposed) _exposed.Add(symbolPath);
            else _exposed.Remove(symbolPath);
        }
        PolicyChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool IsExposed(string symbolPath)
    {
        lock (_lock)
        {
            if (_exposed.Count == 0) return true; // 空白名单 = 不限制读取
            return _exposed.Contains(symbolPath);
        }
    }

    public bool ValidateWrite(string symbolPath, double value, out string reason)
    {
        reason = string.Empty;
        if (!WriteEnabled)
        {
            reason = "Skill 写保护总开关已关闭，当前仅允许读取。";
            return false;
        }

        if (!IsExposed(symbolPath))
        {
            reason = $"变量 {symbolPath} 不在 Skill 白名单中。";
            return false;
        }

        List<WriteGuard> guards;
        lock (_lock) guards = _guards.ToList();

        var guard = guards.FirstOrDefault(g =>
            g.Pattern == "*" || string.Equals(g.Pattern, symbolPath, StringComparison.OrdinalIgnoreCase));

        if (guard == null) return true; // 未配置规则的变量不限制

        if (!guard.AllowWrite)
        {
            reason = $"变量 {symbolPath} 已禁止 Skill 写入。";
            return false;
        }

        if (!guard.IsInRange(value))
        {
            reason = $"变量 {symbolPath} 的写入值 {value} 超出安全范围 [{guard.Min} ~ {guard.Max}]。";
            return false;
        }

        return true;
    }
}
