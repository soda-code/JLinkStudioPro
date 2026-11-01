using JLinkStudio.Core.Models;

namespace JLinkStudio.Core.Services;

/// <summary>符号索引：支持按 C 语言路径快速查找全局变量、结构体成员与数组元素。</summary>
public sealed class SymbolIndex
{
    // 后台 ELF 重载与 UI 读取可能并发，所有访问必须经 _lock 串行化
    private readonly object _lock = new();
    private Dictionary<string, WatchSymbol> _byPath = new(StringComparer.Ordinal);
    private List<WatchSymbol> _roots = new();

    public IReadOnlyList<WatchSymbol> Roots
    {
        get { lock (_lock) return _roots.ToArray(); }
    }
    public int Count { get { lock (_lock) return _byPath.Count; } }
    public string? SourceFile { get; private set; }

    public void Load(IEnumerable<WatchSymbol> roots, string? sourceFile = null)
    {
        // 先在临时集合中构建完整索引，再原子替换，避免读取方看到半初始化状态
        var newRoots = new List<WatchSymbol>();
        var newByPath = new Dictionary<string, WatchSymbol>(StringComparer.Ordinal);

        foreach (var root in roots)
        {
            newRoots.Add(root);
            foreach (var item in root.Flatten())
                newByPath[item.FullPath] = item;
        }

        lock (_lock)
        {
            _roots = newRoots;
            _byPath = newByPath;
            SourceFile = sourceFile;
        }
    }

    public void Clear() => Load(Array.Empty<WatchSymbol>(), null);

    /// <summary>支持 g_motorPid.kp / g_adcBuffer[3] / g_adcBuffer.3 三种写法。</summary>
    public WatchSymbol? Find(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = path.Trim();

        lock (_lock)
        {
            if (_byPath.TryGetValue(path, out var symbol)) return symbol;

            var normalized = path.Replace("->", ".").Replace(".0x", ".0x");
            if (_byPath.TryGetValue(normalized, out symbol)) return symbol;

            // 数组下标统一为 [n] 形式
            var bracket = System.Text.RegularExpressions.Regex.Replace(normalized, @"\.(\d+)(?![\d\w])", "[$1]");
            if (_byPath.TryGetValue(bracket, out symbol)) return symbol;

            return _byPath.FirstOrDefault(kv => string.Equals(kv.Key, normalized, StringComparison.OrdinalIgnoreCase)).Value;
        }
    }

    public IEnumerable<WatchSymbol> Search(string keyword, int max = 300)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(keyword))
                return _roots.Take(max).ToArray();

            var lower = keyword.Trim();
            return _byPath
                .Where(kv => kv.Key.Contains(lower, StringComparison.OrdinalIgnoreCase) && !kv.Value.IsContainer)
                .Take(max)
                .Select(kv => kv.Value)
                .ToArray();
        }
    }

    public IEnumerable<WatchSymbol> AllLeaves()
    {
        lock (_lock)
            return _roots.SelectMany(r => r.Flatten()).Where(s => !s.IsContainer).ToArray();
    }
}
