using System.Text;
using System.Text.RegularExpressions;
using JLinkStudio.Core.Models;

namespace JLinkStudio.Core.Services;

/// <summary>RTT 高速采集引擎：后台线程持续读取通道数据，与界面刷新解耦。</summary>
public sealed class RttEngine : IDisposable
{
    private const int MaxLinesPerChannel = 20000;

    private static readonly Regex LevelRegex = new(@"\b(ERROR|ERR|FATAL|FAIL|ASSERT)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WarnRegex = new(@"\b(WARN|WARNING)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DebugRegex = new(@"\b(DEBUG|TRACE|VERBOSE)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FieldRegex = new(@"([A-Za-z_][A-Za-z0-9_\.]*)\s*=\s*(-?\d+(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex AnsiRegex = new(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);
    private static readonly Regex AlarmRegex = new(@"(HardFault|Watchdog|Assert|Kernel Panic|死机|复位原因)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly DeviceManager _device;
    private readonly List<RttLine>[] _channels;
    private readonly StringBuilder[] _partial;
    private readonly object[] _locks;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _totalLines;
    private long _totalBytes;

    public RttEngine(DeviceManager device)
    {
        _device = device;
        _channels = new[] { new List<RttLine>(), new List<RttLine>(), new List<RttLine>() };
        _partial = new[] { new StringBuilder(), new StringBuilder(), new StringBuilder() };
        _locks = new object[] { new(), new(), new() };
    }

    public bool IsRunning { get; private set; }
    public long TotalLines => Interlocked.Read(ref _totalLines);
    public long TotalBytes => Interlocked.Read(ref _totalBytes);
    public uint? ControlBlockAddress => _device.Config.RttControlBlockAddress;
    public string? LastError { get; private set; }

    public event EventHandler<List<RttLine>>? LinesReceived;
    public event EventHandler<RttLine>? AlarmRaised;

    public static string StripAnsi(string text) => AnsiRegex.Replace(text, string.Empty);

    public bool Start(out string error)
    {
        error = string.Empty;
        if (IsRunning)
        {
            StartLoop();
            return true;
        }

        var link = _device.Link;
        if (link == null || !link.IsConnected)
        {
            error = "目标未连接，无法启动 RTT。";
            LastError = error;
            return false;
        }

        uint? address = _device.Config.RttSearchMode switch
        {
            RttSearchMode.ManualAddress => _device.Config.RttControlBlockAddress,
            RttSearchMode.AutoFromElf => _device.ElfInfo?.RttControlBlockAddress ?? _device.Config.RttControlBlockAddress,
            _ => _device.Config.RttControlBlockAddress,
        };

        if (!link.RttStart(address, out error))
        {
            LastError = error;
            return false;
        }

        IsRunning = true;
        StartLoop();
        return true;
    }

    private void StartLoop()
    {
        var old = _cts;
        try
        {
            old?.Cancel();
            old?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // 已释放，忽略
        }

        var cts = new CancellationTokenSource();
        _cts = cts;
        var token = cts.Token;
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
        try
        {
            _device.Link?.RttStop();
        }
        catch
        {
            // ignore
        }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        var buffer = new byte[8192];
        while (!token.IsCancellationRequested)
        {
            var link = _device.Link;
            if (link == null || !link.IsConnected)
            {
                if (!await DelayAsync(50, token)) break;
                continue;
            }

            bool gotData = false;
            for (int channel = 0; channel < _channels.Length; channel++)
            {
                int count;
                try
                {
                    count = link.RttRead(channel, buffer);
                }
                catch
                {
                    count = -1;
                }

                if (count <= 0) continue;
                gotData = true;
                Interlocked.Add(ref _totalBytes, count);
                ProcessBytes(channel, buffer, count);
            }

            if (!await DelayAsync(gotData ? 1 : 4, token)) break;
        }
    }

    private static async Task<bool> DelayAsync(int ms, CancellationToken token)
    {
        try
        {
            await Task.Delay(ms, token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void ProcessBytes(int channel, byte[] buffer, int count)
    {
        var text = Encoding.UTF8.GetString(buffer, 0, count);
        var newLines = new List<RttLine>();

        lock (_locks[channel])
        {
            foreach (var ch in text)
            {
                if (ch == '\n')
                {
                    var lineText = _partial[channel].ToString().TrimEnd('\r');
                    _partial[channel].Clear();
                    if (lineText.Length > 0)
                        newLines.Add(CreateLine(channel, lineText));
                }
                else
                {
                    if (_partial[channel].Length > 8192) _partial[channel].Clear();
                    _partial[channel].Append(ch);
                }
            }

            if (newLines.Count > 0)
            {
                _channels[channel].AddRange(newLines);
                if (_channels[channel].Count > MaxLinesPerChannel)
                    _channels[channel].RemoveRange(0, _channels[channel].Count - MaxLinesPerChannel);
            }
        }

        if (newLines.Count == 0) return;

        Interlocked.Add(ref _totalLines, newLines.Count);
        LinesReceived?.Invoke(this, newLines);

        foreach (var line in newLines)
        {
            if (line.Level >= RttLevel.Warn || AlarmRegex.IsMatch(line.Text))
                AlarmRaised?.Invoke(this, line);
        }
    }

    private static RttLine CreateLine(int channel, string text)
    {
        var clean = StripAnsi(text);
        var level = RttLevel.Info;
        if (LevelRegex.IsMatch(clean)) level = RttLevel.Error;
        else if (WarnRegex.IsMatch(clean)) level = RttLevel.Warn;
        else if (DebugRegex.IsMatch(clean)) level = RttLevel.Trace;

        var line = new RttLine
        {
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Channel = channel,
            Text = clean,
            RawText = text,
            Level = level,
        };

        foreach (Match match in FieldRegex.Matches(clean))
        {
            if (line.Fields.Count > 32) break;
            line.Fields[match.Groups[1].Value] = match.Groups[2].Value;
        }

        return line;
    }

    /// <summary>向目标下发数据（文本或 Hex 字节流）。</summary>
    public bool Send(string text, int channel = 0, bool appendNewLine = true)
    {
        var link = _device.Link;
        if (link == null || !link.IsConnected) return false;

        var payload = appendNewLine ? text + "\r\n" : text;
        var bytes = Encoding.UTF8.GetBytes(payload);
        return link.RttWrite(channel, bytes) >= 0;
    }

    public bool SendHex(string hex, int channel = 0)
    {
        var link = _device.Link;
        if (link == null || !link.IsConnected) return false;

        var cleaned = hex.Replace(" ", string.Empty).Replace("-", string.Empty).Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase);
        if (cleaned.Length % 2 != 0) cleaned = "0" + cleaned;

        var bytes = new byte[cleaned.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(cleaned.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out bytes[i]))
                return false;
        }

        return link.RttWrite(channel, bytes) >= 0;
    }

    public IReadOnlyList<RttLine> Query(int channel, int lastLines, string? regexFilter = null, RttLevel? minLevel = null)
    {
        if (channel < 0 || channel >= _channels.Length) return Array.Empty<RttLine>();

        List<RttLine> snapshot;
        lock (_locks[channel])
        {
            int skip = Math.Max(0, _channels[channel].Count - Math.Max(1, lastLines));
            snapshot = _channels[channel].GetRange(skip, _channels[channel].Count - skip);
        }

        IEnumerable<RttLine> query = snapshot;
        if (minLevel.HasValue) query = query.Where(l => l.Level >= minLevel.Value);

        if (!string.IsNullOrWhiteSpace(regexFilter))
        {
            try
            {
                var regex = new Regex(regexFilter, RegexOptions.IgnoreCase);
                query = query.Where(l => regex.IsMatch(l.Text));
            }
            catch (ArgumentException)
            {
                query = query.Where(l => l.Text.Contains(regexFilter, StringComparison.OrdinalIgnoreCase));
            }
        }

        return query.ToList();
    }

    public IReadOnlyList<RttLine> Recent(int maxLines = 200)
    {
        var all = new List<RttLine>();
        for (int ch = 0; ch < _channels.Length; ch++)
        {
            lock (_locks[ch])
            {
                int skip = Math.Max(0, _channels[ch].Count - maxLines);
                all.AddRange(_channels[ch].GetRange(skip, _channels[ch].Count - skip));
            }
        }
        return all.OrderBy(l => l.TimestampMs).TakeLast(maxLines).ToList();
    }

    public void Clear(int channel = -1)
    {
        for (int ch = 0; ch < _channels.Length; ch++)
        {
            if (channel >= 0 && ch != channel) continue;
            lock (_locks[ch]) _channels[ch].Clear();
        }
    }

    public void Dispose()
    {
        // Stop 内部已完成取消与释放，且将 _cts 置空，天然幂等
        try
        {
            Stop();
        }
        catch
        {
            // ignore
        }
    }
}
