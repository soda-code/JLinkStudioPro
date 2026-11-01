using JLinkStudio.Core.Models;

namespace JLinkStudio.Core.Services;

/// <summary>
/// 变量高频轮询引擎：地址合并（Memory Coalescing）+ 后台采样 + 波形历史缓存，
/// 同时为界面表格、ScottPlot 波形与 Skill 时序采集接口提供统一数据源。
/// </summary>
public sealed class VariablePollEngine : IDisposable
{
    private const int MaxHistoryPoints = 8000;
    private const int CoalesceGapBytes = 64;
    private const int MaxBlockBytes = 1024;

    private readonly DeviceManager _device;
    private readonly object _lock = new();
    private readonly List<WatchSymbol> _roots = new();
    private readonly Dictionary<string, VariableSample> _last = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<(long Timestamp, double Value)>> _history = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private double _sampleRateHz = 200;

    public VariablePollEngine(DeviceManager device)
    {
        _device = device;
    }

    public double SampleRateHz
    {
        get => _sampleRateHz;
        set => _sampleRateHz = Math.Clamp(value, 1, 1000);
    }

    public bool IsRunning { get; private set; }
    private long _sampleCount;
    /// <summary>累计采样次数（多线程并发递增，使用 Interlocked 保证不丢失）。</summary>
    public long SampleCount => Interlocked.Read(ref _sampleCount);
    public int ActiveLeafCount => CollectLeaves().Count;

    public event EventHandler<List<VariableSample>>? ValuesUpdated;
    public event EventHandler? WatchListChanged;
    public event EventHandler<VariableWriteEventArgs>? ValueWritten;

    public sealed class VariableWriteEventArgs : EventArgs
    {
        public string Path { get; init; } = string.Empty;
        public object? Value { get; init; }
        public string Source { get; init; } = "ui";
        public long TimestampMs { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public IReadOnlyList<WatchSymbol> WatchedRoots
    {
        get
        {
            lock (_lock) return _roots.ToList();
        }
    }

    public List<VariableSample> LastSamples
    {
        get
        {
            lock (_lock) return _last.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
        }
    }

    public void Start()
    {
        if (IsRunning) return;
        IsRunning = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => LoopAsync(token), token);
    }

    public void Stop()
    {
        IsRunning = false;
        // 取出引用并置空：Dispose 可能被调用多次（OnFormClosed + using 双重释放），
        // 二次 Cancel 已释放的 CTS 会抛 ObjectDisposedException
        var cts = _cts;
        _cts = null;
        try
        {
            cts?.Cancel();
            cts?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // 已释放，忽略
        }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var started = Environment.TickCount64;
            try
            {
                PollOnce();
            }
            catch
            {
                // 单次采样失败不应终止引擎
            }

            int interval = (int)Math.Clamp(1000.0 / _sampleRateHz, 5, 1000);
            int elapsed = (int)(Environment.TickCount64 - started);
            int delay = Math.Max(2, interval - elapsed);
            try
            {
                await Task.Delay(delay, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void PollOnce()
    {
        var link = _device.Link;
        if (link == null || !link.IsConnected) return;

        var leaves = CollectLeaves();
        if (leaves.Count == 0) return;

        var samples = new List<VariableSample>(leaves.Count);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (var group in BuildGroups(leaves))
        {
            var buffer = new byte[group.Length];
            if (link.ReadMemory(group.StartAddress, buffer) < 0) continue;

            foreach (var symbol in group.Symbols)
            {
                int offset = (int)(symbol.Address - group.StartAddress);
                int size = Math.Max(1, symbol.ByteSize);
                if (offset < 0 || offset + size > buffer.Length) continue;

                var value = ValueCodec.Decode(buffer.AsSpan(offset, size), symbol.Type, size);
                var sample = new VariableSample
                {
                    Name = symbol.FullPath,
                    Address = symbol.Address,
                    Type = symbol.Type,
                    Value = value,
                    ValueText = ValueCodec.Format(value, symbol.Type, symbol.Format),
                    TimestampMs = timestamp,
                };

                symbol.CurrentValue = value;
                symbol.CurrentValueText = sample.ValueText;
                samples.Add(sample);

                lock (_lock)
                {
                    _last[sample.Name] = sample;
                    AppendHistoryUnsafe(sample.Name, timestamp, value);
                }
            }
        }

        Interlocked.Increment(ref _sampleCount);
        if (samples.Count > 0)
            ValuesUpdated?.Invoke(this, samples);
    }

    private void AppendHistoryUnsafe(string path, long timestamp, object? value)
    {
        if (!ValueCodec.TryToDouble(value, out var numeric)) return;
        if (!_history.TryGetValue(path, out var list))
        {
            list = new List<(long, double)>();
            _history[path] = list;
        }
        list.Add((timestamp, numeric));
        if (list.Count > MaxHistoryPoints)
            list.RemoveRange(0, list.Count - MaxHistoryPoints);
    }

    private List<WatchSymbol> CollectLeaves()
    {
        lock (_lock)
        {
            var result = new List<WatchSymbol>();
            foreach (var root in _roots)
            {
                foreach (var symbol in root.Flatten())
                {
                    if (!symbol.IsContainer && symbol.ByteSize > 0 && symbol.Enabled)
                        result.Add(symbol);
                }
            }
            return result;
        }
    }

    private sealed class ReadGroup
    {
        public uint StartAddress { get; set; }
        public int Length { get; set; }
        public List<WatchSymbol> Symbols { get; } = new();
        public uint EndAddress => StartAddress + (uint)Length;
    }

    /// <summary>把地址相邻（&lt;64B）的变量合并为一次块读取，显著提升 SWD 吞吐率。</summary>
    private static List<ReadGroup> BuildGroups(List<WatchSymbol> leaves)
    {
        var groups = new List<ReadGroup>();
        foreach (var symbol in leaves.OrderBy(s => s.Address))
        {
            int size = Math.Max(1, symbol.ByteSize);
            var group = groups.LastOrDefault();
            bool canMerge = group != null
                && symbol.Address <= group.EndAddress + CoalesceGapBytes
                && (Math.Max(group.EndAddress, symbol.Address + (uint)size) - group.StartAddress) <= MaxBlockBytes;

            if (canMerge)
            {
                uint newEnd = Math.Max(group!.EndAddress, symbol.Address + (uint)size);
                group.Length = (int)(newEnd - group.StartAddress);
                group.Symbols.Add(symbol);
            }
            else
            {
                var newGroup = new ReadGroup
                {
                    StartAddress = symbol.Address,
                    Length = size,
                };
                newGroup.Symbols.Add(symbol);
                groups.Add(newGroup);
            }
        }
        return groups;
    }

    // ---------------- 监控列表管理 ----------------

    public void AddWatch(WatchSymbol symbol)
    {
        lock (_lock)
        {
            if (_roots.Any(r => string.Equals(r.FullPath, symbol.FullPath, StringComparison.OrdinalIgnoreCase)))
                return;
            if (symbol.Parent != null) symbol = symbol.CloneShallow();
            _roots.Add(symbol);
        }
        WatchListChanged?.Invoke(this, EventArgs.Empty);
    }

    public void AddWatches(IEnumerable<WatchSymbol> symbols)
    {
        lock (_lock)
        {
            foreach (var raw in symbols)
            {
                if (_roots.Any(r => string.Equals(r.FullPath, raw.FullPath, StringComparison.OrdinalIgnoreCase))) continue;
                _roots.Add(raw.Parent != null ? raw.CloneShallow() : raw);
            }
        }
        WatchListChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveWatch(WatchSymbol symbol)
    {
        lock (_lock)
        {
            var target = _roots.FirstOrDefault(r => string.Equals(r.FullPath, symbol.FullPath, StringComparison.OrdinalIgnoreCase));
            if (target != null) _roots.Remove(target);
        }
        WatchListChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearWatches()
    {
        lock (_lock)
        {
            _roots.Clear();
            _last.Clear();
            _history.Clear();
        }
        WatchListChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------- 按名读写 ----------------

    public VariableSample? ReadByName(string path, out string error)
    {
        error = string.Empty;
        var symbol = Resolve(path);
        if (symbol == null)
        {
            error = $"未找到符号: {path}";
            return null;
        }

        return ReadSymbol(symbol, out error);
    }

    private VariableSample? ReadSymbol(WatchSymbol symbol, out string error)
    {
        error = string.Empty;
        var link = _device.Link;
        if (link == null || !link.IsConnected)
        {
            error = "目标未连接。";
            return null;
        }

        int size = Math.Max(1, symbol.ByteSize);
        var buffer = new byte[size];
        if (link.ReadMemory(symbol.Address, buffer) < 0)
        {
            error = $"内存读取失败 @0x{symbol.Address:X8}";
            return null;
        }

        var value = ValueCodec.Decode(buffer, symbol.Type, size);
        var sample = new VariableSample
        {
            Name = symbol.FullPath,
            Address = symbol.Address,
            Type = symbol.Type,
            Value = value,
            ValueText = ValueCodec.Format(value, symbol.Type, symbol.Format),
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        lock (_lock)
        {
            _last[sample.Name] = sample;
            AppendHistoryUnsafe(sample.Name, sample.TimestampMs, value);
        }

        return sample;
    }

    public List<VariableSample> ReadMany(IEnumerable<string> names, out List<string> errors)
    {
        errors = new List<string>();
        var result = new List<VariableSample>();
        foreach (var name in names)
        {
            var sample = ReadByName(name, out var error);
            if (sample != null) result.Add(sample);
            else if (!string.IsNullOrEmpty(error)) errors.Add(error);
        }
        return result;
    }

    public bool WriteByName(string path, object value, string source, out string error, out VariableSample? sample)
    {
        error = string.Empty;
        sample = null;

        var symbol = Resolve(path);
        if (symbol == null)
        {
            error = $"未找到可写符号: {path}";
            return false;
        }

        var link = _device.Link;
        if (link == null || !link.IsConnected)
        {
            error = "目标未连接。";
            return false;
        }

        int size = Math.Max(1, symbol.ByteSize);
        var payload = ValueCodec.Encode(value, symbol.Type, size);
        if (link.WriteMemory(symbol.Address, payload) < 0)
        {
            error = $"内存写入失败 @0x{symbol.Address:X8}";
            return false;
        }

        symbol.CurrentValue = value;
        sample = new VariableSample
        {
            Name = symbol.FullPath,
            Address = symbol.Address,
            Type = symbol.Type,
            Value = value,
            ValueText = ValueCodec.Format(value, symbol.Type, symbol.Format),
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            FromSkill = source != "ui",
        };

        lock (_lock)
        {
            _last[sample.Name] = sample;
        }

        ValueWritten?.Invoke(this, new VariableWriteEventArgs { Path = symbol.FullPath, Value = value, Source = source });
        return true;
    }

    private WatchSymbol? Resolve(string path)
    {
        WatchSymbol? symbol;
        lock (_lock)
        {
            symbol = _roots.SelectMany(r => r.Flatten())
                .FirstOrDefault(s => string.Equals(s.FullPath, path, StringComparison.OrdinalIgnoreCase));
        }
        return symbol ?? _device.Symbols.Find(path);
    }

    // ---------------- 时序块采集 ----------------

    public async Task<TimeSeriesResult> CaptureTimeSeriesAsync(string[] names, int sampleRateHz, int durationMs,
        CancellationToken token = default)
    {
        var symbols = new List<WatchSymbol>();
        var errors = new List<string>();
        foreach (var name in names)
        {
            var symbol = Resolve(name);
            if (symbol == null) errors.Add($"未找到符号: {name}");
            else symbols.Add(symbol);
        }

        if (symbols.Count == 0)
            throw new InvalidOperationException(string.Join("; ", errors.DefaultIfEmpty("没有可采集的变量")));

        sampleRateHz = Math.Clamp(sampleRateHz, 1, 1000);
        durationMs = Math.Clamp(durationMs, 10, 600000);

        var result = new TimeSeriesResult
        {
            Names = symbols.Select(s => s.FullPath).ToArray(),
            SampleRateHz = sampleRateHz,
            DurationMs = durationMs,
            StartTimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        int interval = Math.Max(1, 1000 / sampleRateHz);
        var groups = BuildGroups(symbols.Where(s => !s.IsContainer && s.ByteSize > 0).ToList());
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        long nextDueMs = 0;

        while (stopwatch.ElapsedMilliseconds < durationMs && !token.IsCancellationRequested)
        {
            var frame = new TimeSeriesFrame { TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            var link = _device.Link;
            if (link == null || !link.IsConnected) break;

            foreach (var group in groups)
            {
                var buffer = new byte[group.Length];
                if (link.ReadMemory(group.StartAddress, buffer) < 0) continue;
                foreach (var symbol in group.Symbols)
                {
                    int offset = (int)(symbol.Address - group.StartAddress);
                    int size = Math.Max(1, symbol.ByteSize);
                    if (offset < 0 || offset + size > buffer.Length) continue;
                    var value = ValueCodec.Decode(buffer.AsSpan(offset, size), symbol.Type, size);
                    if (ValueCodec.TryToDouble(value, out var numeric))
                        frame.Values[symbol.FullPath] = numeric;
                }
            }

            if (frame.Values.Count > 0) result.Frames.Add(frame);

            nextDueMs += interval;
            int wait = (int)(nextDueMs - stopwatch.ElapsedMilliseconds);
            try
            {
                await Task.Delay(Math.Clamp(wait, 1, interval), token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        result.EndTimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var name in result.Names)
            result.Statistics[name] = ComputeStatistics(result.Frames, name);
        return result;
    }

    private static TimeSeriesStatistics ComputeStatistics(List<TimeSeriesFrame> frames, string name)
    {
        var values = frames.Where(f => f.Values.ContainsKey(name)).Select(f => f.Values[name]).ToList();
        if (values.Count == 0) return new TimeSeriesStatistics();

        double min = values.Min();
        double max = values.Max();
        double mean = values.Average();
        double variance = values.Count > 1 ? values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1) : 0;
        double final = values.TakeLast(Math.Max(1, values.Count / 10)).Average();

        double overshoot = 0;
        if (Math.Abs(final) > 1e-9 && max > final)
            overshoot = (max - final) / Math.Abs(final) * 100.0;

        return new TimeSeriesStatistics
        {
            Min = min,
            Max = max,
            Mean = mean,
            StdDev = Math.Sqrt(variance),
            Overshoot = Math.Round(overshoot, 3),
            SteadyStateError = Math.Round(Math.Abs(final - values[^1]), 4),
            FinalValue = final,
            SampleCount = values.Count,
        };
    }

    public IReadOnlyList<(long Timestamp, double Value)> GetHistory(string path)
    {
        lock (_lock)
        {
            return _history.TryGetValue(path, out var list)
                ? list.ToList()
                : Array.Empty<(long, double)>();
        }
    }

    public void ClearHistory()
    {
        lock (_lock) _history.Clear();
    }

    public void Dispose()
    {
        // Stop 内部已完成取消与释放，且将 _cts 置空，天然幂等
        Stop();
    }
}
