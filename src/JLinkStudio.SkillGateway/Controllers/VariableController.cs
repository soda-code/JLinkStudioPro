using System.Globalization;
using System.Text.Json;
using JLinkStudio.Core.Models;
using JLinkStudio.Core.Services;
using JLinkStudio.SkillGateway.Models;
using Microsoft.AspNetCore.Mvc;

namespace JLinkStudio.SkillGateway.Controllers;

/// <summary>变量读写与批量采样接口（支持符号名直接操作）。</summary>
[ApiController]
[Route("api/v1/variables")]
public sealed class VariableController : ControllerBase
{
    private readonly StudioContext _context;

    public VariableController(StudioContext context) => _context = context;

    /// <summary>按符号名列表读取当前物理值（自动查符号表并合并地址读取）。</summary>
    [HttpPost("read")]
    public IActionResult Read([FromBody] VariableReadRequest request)
    {
        if (request.Names.Count == 0)
            return BadRequest(new { error = "names 不能为空" });

        var response = new ReadResponse { Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        foreach (var name in request.Names)
        {
            var sample = _context.Variables.ReadByName(name, out var error);
            if (sample == null)
            {
                response.Errors.Add(error);
                continue;
            }

            response.Data[sample.Name] = new VariableSampleDto
            {
                Name = sample.Name,
                Address = $"0x{sample.Address:X8}",
                Type = sample.Type.ToCName(),
                Value = sample.Value,
                ValueText = sample.ValueText,
            };
        }

        return Ok(response);
    }

    /// <summary>Skill 分析后反向写入变量（受查看模式、白名单与安全范围保护）。</summary>
    [HttpPost("write")]
    public IActionResult Write([FromBody] VariableWriteRequest request)
    {
        if (_context.IsInspectMode)
            return StatusCode(403, new { error = "查看模式已启用：禁止向目标写入，请先切回调试模式。" });

        if (request.Writes.Count == 0)
            return BadRequest(new { error = "writes 不能为空" });

        var results = new List<object>();
        foreach (var write in request.Writes)
        {
            var symbol = _context.Device.Symbols.Find(write.Name)
                         ?? _context.Variables.WatchedRoots.SelectMany(w => w.Flatten())
                             .FirstOrDefault(s => string.Equals(s.FullPath, write.Name, StringComparison.OrdinalIgnoreCase));

            if (symbol == null)
            {
                results.Add(new { name = write.Name, success = false, error = "未找到符号" });
                continue;
            }

            string rawText = write.Value.ValueKind == JsonValueKind.String
                ? write.Value.GetString() ?? string.Empty
                : write.Value.GetRawText();

            if (!ValueCodec.TryParseInput(rawText, symbol.Type, out var typedValue))
            {
                results.Add(new { name = write.Name, success = false, error = $"无法解析写入值: {rawText}" });
                continue;
            }

            double numeric = 0;
            if (ValueCodec.TryToDouble(typedValue, out var asDouble)) numeric = asDouble;

            if (!_context.Security.ValidateWrite(symbol.FullPath, numeric, out var reason))
            {
                _context.Audit.Add("skill", "WRITE-DENIED", "/api/v1/variables/write", $"{symbol.FullPath} = {rawText}：{reason}", 0, false);
                results.Add(new { name = write.Name, success = false, error = reason });
                continue;
            }

            bool ok = _context.Variables.WriteByName(symbol.FullPath, typedValue, "skill", out var error2, out var sample);
            _context.Audit.Add("skill", "WRITE", "/api/v1/variables/write",
                $"{symbol.FullPath} = {rawText} → {sample?.ValueText}", 0, ok);

            results.Add(new
            {
                name = write.Name,
                success = ok,
                error = ok ? null : error2,
                address = $"0x{symbol.Address:X8}",
                value = sample?.Value,
            });
        }

        return Ok(new { timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), results });
    }

    /// <summary>
    /// 批量写入：一次请求写多个变量，每个变量可带一串值（按 intervalMs 依次下发）。
    /// 例：{"writes":[{"name":"g_pid.kp","values":[1,2,3]},{"name":"g_pid.ki","values":[0.1,0.2,0.3]}],"intervalMs":10}
    /// </summary>
    [HttpPost("write-batch")]
    public async Task<IActionResult> WriteBatch([FromBody] BatchWriteRequest request, CancellationToken token)
    {
        if (_context.IsInspectMode)
            return StatusCode(403, new { error = "查看模式已启用：禁止向目标写入，请先切回调试模式。" });

        if (request.Writes.Count == 0)
            return BadRequest(new { error = "writes 不能为空" });

        int steps = request.Writes.Max(w => w.Values.Count);
        if (steps == 0)
            return BadRequest(new { error = "每个变量的 values 不能为空" });

        int interval = Math.Clamp(request.IntervalMs, 0, 60_000);

        // 先解析全部符号：名字写错时直接报错，避免只写了一部分
        var symbols = new Dictionary<string, WatchSymbol>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        foreach (var item in request.Writes)
        {
            var symbol = _context.Device.Symbols.Find(item.Name)
                         ?? _context.Variables.WatchedRoots.SelectMany(w => w.Flatten())
                             .FirstOrDefault(s => string.Equals(s.FullPath, item.Name, StringComparison.OrdinalIgnoreCase));

            if (symbol == null)
            {
                errors.Add($"未找到符号: {item.Name}");
                continue;
            }

            symbols[item.Name] = symbol;
        }

        var stepResults = new List<object>();

        for (int step = 0; step < steps; step++)
        {
            var writes = new List<object>();

            foreach (var item in request.Writes)
            {
                // 序列长度可以不一致：短的那一路写完即停
                if (step >= item.Values.Count) continue;

                if (!symbols.TryGetValue(item.Name, out var symbol))
                {
                    if (step == 0) writes.Add(new { name = item.Name, success = false, error = "未找到符号" });
                    continue;
                }

                var value = item.Values[step];
                string rawText = value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? string.Empty
                    : value.GetRawText();

                if (!ValueCodec.TryParseInput(rawText, symbol.Type, out var typedValue))
                {
                    writes.Add(new { name = item.Name, success = false, error = $"无法解析写入值: {rawText}" });
                    continue;
                }

                double numeric = 0;
                if (ValueCodec.TryToDouble(typedValue, out var asDouble)) numeric = asDouble;

                if (!_context.Security.ValidateWrite(symbol.FullPath, numeric, out var reason))
                {
                    _context.Audit.Add("skill", "WRITE-DENIED", "/api/v1/variables/write-batch",
                        $"{symbol.FullPath} = {rawText}：{reason}", 0, false);
                    writes.Add(new { name = item.Name, success = false, error = reason });
                    continue;
                }

                bool ok = _context.Variables.WriteByName(symbol.FullPath, typedValue, "skill", out var error2, out var sample);
                writes.Add(new
                {
                    name = item.Name,
                    success = ok,
                    error = ok ? null : error2,
                    address = $"0x{symbol.Address:X8}",
                    written = rawText,
                    value = sample?.Value,
                });
            }

            stepResults.Add(new
            {
                step,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                writes,
            });

            if (interval > 0 && step < steps - 1)
                await Task.Delay(interval, token);
        }

        _context.Audit.Add("skill", "WRITE-BATCH", "/api/v1/variables/write-batch",
            $"{request.Writes.Count} 个变量 · 最多 {steps} 步 · 间隔 {interval}ms");

        Dictionary<string, object>? readBack = null;
        if (request.ReadBack)
        {
            readBack = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in request.Writes)
            {
                var sample = _context.Variables.ReadByName(item.Name, out _);
                if (sample == null) continue;
                readBack[sample.Name] = new { value = sample.Value, valueText = sample.ValueText };
            }
        }

        return Ok(new
        {
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            variableCount = request.Writes.Count,
            stepCount = steps,
            intervalMs = interval,
            errors,
            results = stepResults,
            readBack,
        });
    }

    /// <summary>同步块采集：以指定频率采集 N 毫秒，一次性返回原始数组与统计特征。</summary>
    [HttpPost("capture-timeseries")]
    public async Task<IActionResult> CaptureTimeSeries([FromBody] TimeSeriesRequest request, CancellationToken token)
    {
        if (request.Names.Count == 0)
            return BadRequest(new { error = "names 不能为空" });

        try
        {
            var result = await _context.Variables.CaptureTimeSeriesAsync(
                request.Names.ToArray(), request.SampleRateHz, request.DurationMs, token);

            _context.Audit.Add("skill", "CAPTURE", "/api/v1/variables/capture-timeseries",
                $"{string.Join(",", result.Names)} · {result.SampleRateHz}Hz · {result.DurationMs}ms · {result.Frames.Count} 帧");

            var durationSeconds = Math.Max(0.001, (result.EndTimestampMs - result.StartTimestampMs) / 1000.0);
            return Ok(new
            {
                names = result.Names,
                sampleRateHz = result.SampleRateHz,
                durationMs = result.DurationMs,
                actualSampleRateHz = Math.Round(result.Frames.Count / durationSeconds, 2),
                startTimestampMs = result.StartTimestampMs,
                endTimestampMs = result.EndTimestampMs,
                statistics = result.Statistics.ToDictionary(
                    kv => kv.Key,
                    kv => new
                    {
                        kv.Value.Min,
                        kv.Value.Max,
                        kv.Value.Mean,
                        kv.Value.StdDev,
                        kv.Value.PeakToPeak,
                        kv.Value.Overshoot,
                        kv.Value.SteadyStateError,
                        kv.Value.FinalValue,
                        kv.Value.SampleCount,
                    }),
                timestamps = result.Frames.Select(f => f.TimestampMs).ToArray(),
                data = result.Names.ToDictionary(
                    name => name,
                    name => result.Frames.Select(f => f.Values.TryGetValue(name, out var v) ? v : double.NaN).ToArray()),
            });
        }
        catch (Exception ex) when (ex.Message.Contains("未找到符号"))
        {
            // 符号名写错属于调用方问题，用 404 而不是 500
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>查看当前监控列表（界面与 Skill 共享，含每项显示属性）。</summary>
    [HttpGet("watchlist")]
    public IActionResult WatchList()
    {
        var list = _context.Variables.LastSamples.Select(s => new
        {
            name = s.Name,
            address = $"0x{s.Address:X8}",
            type = s.Type.ToCName(),
            value = s.Value,
            valueText = s.ValueText,
            timestamp = s.TimestampMs,
        });

        var items = _context.Variables.WatchedRoots
            .SelectMany(root => new[] { root }.Concat(root.Flatten().Where(s => !ReferenceEquals(s, root))))
            .Select(s => new
            {
                name = s.FullPath,
                format = s.Format.ToString(),
                color = s.ColorArgb == 0 ? null : $"0x{s.ColorArgb:X8}",
                plotEnabled = s.PlotEnabled,
                enabled = s.Enabled,
                isWritable = s.IsWritable,
                exposeToSkill = s.ExposeToSkill,
                refreshRateHz = s.RefreshRateHz,
            })
            .ToList();

        return Ok(new { count = _context.Variables.ActiveLeafCount, samples = list, items });
    }

    /// <summary>
    /// 把变量加入监控列表（界面表格与波形会立即显示）。
    /// names 只加名字；items 可在加入的同时设置格式 / 颜色 / 波形 / 使能 / 可写 / 暴露属性。
    /// </summary>
    [HttpPost("watch")]
    public IActionResult Watch([FromBody] WatchRequest request)
    {
        var symbols = new List<WatchSymbol>();
        var errors = new List<string>();
        var applied = new List<string>();

        foreach (var name in request.Names)
        {
            var symbol = _context.Device.Symbols.Find(name);
            if (symbol == null) errors.Add($"未找到符号: {name}");
            else symbols.Add(symbol);
        }

        foreach (var item in request.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Name))
            {
                errors.Add("items[].name 不能为空");
                continue;
            }

            var symbol = _context.Device.Symbols.Find(item.Name);
            if (symbol == null)
            {
                errors.Add($"未找到符号: {item.Name}");
                continue;
            }

            var changes = new List<string>();

            if (!string.IsNullOrWhiteSpace(item.Format))
            {
                if (Enum.TryParse<ValueFormat>(item.Format, true, out var format))
                    changes.Add($"format={format}");
                else
                    errors.Add($"format 仅支持 Dec / Hex / Bin / Float / Ascii：{item.Format}");
            }

            if (!string.IsNullOrWhiteSpace(item.Color))
            {
                if (TryParseColor(item.Color, out int argb)) changes.Add($"color=0x{argb:X8}");
                else errors.Add($"color 格式应为 0xAARRGGBB 或 #RRGGBB：{item.Color}");
            }

            if (item.PlotEnabled.HasValue) changes.Add($"plotEnabled={item.PlotEnabled.Value.ToString().ToLowerInvariant()}");
            if (item.Enabled.HasValue) changes.Add($"enabled={item.Enabled.Value.ToString().ToLowerInvariant()}");
            if (item.IsWritable.HasValue) changes.Add($"isWritable={item.IsWritable.Value.ToString().ToLowerInvariant()}");
            if (item.ExposeToSkill.HasValue) changes.Add($"exposeToSkill={item.ExposeToSkill.Value.ToString().ToLowerInvariant()}");
            if (item.RefreshRateHz.HasValue) changes.Add($"refreshRateHz={item.RefreshRateHz.Value}");

            // 容器（结构体 / 数组）同时也把属性下发到叶子，保证波形与显示一致
            var targets = new List<WatchSymbol> { symbol };
            targets.AddRange(symbol.Flatten()
                .Where(s => !ReferenceEquals(s, symbol) && !s.IsContainer));

            foreach (var target in targets)
            {
                if (!string.IsNullOrWhiteSpace(item.Format) &&
                    Enum.TryParse<ValueFormat>(item.Format, true, out var fmt))
                    target.Format = fmt;
                if (!string.IsNullOrWhiteSpace(item.Color) && TryParseColor(item.Color, out int argb2))
                    target.ColorArgb = argb2;
                if (item.PlotEnabled.HasValue) target.PlotEnabled = item.PlotEnabled.Value;
                if (item.Enabled.HasValue) target.Enabled = item.Enabled.Value;
                if (item.IsWritable.HasValue) target.IsWritable = item.IsWritable.Value;
                if (item.ExposeToSkill.HasValue) target.ExposeToSkill = item.ExposeToSkill.Value;
                if (item.RefreshRateHz.HasValue) target.RefreshRateHz = Math.Max(0, item.RefreshRateHz.Value);
            }

            symbols.Add(symbol);
            applied.Add($"{symbol.FullPath}: {string.Join(", ", changes)}");
        }

        if (symbols.Count > 0) _context.Variables.AddWatches(symbols);

        _context.Audit.Add("skill", "WATCH", "/api/v1/variables/watch",
            $"加入 {symbols.Count} 项" + (applied.Count > 0 ? $" · 属性 {applied.Count} 项" : string.Empty),
            0, errors.Count == 0);

        return Ok(new
        {
            added = symbols.Select(s => s.FullPath).Distinct().ToList(),
            applied,
            errors,
        });
    }

    /// <summary>读取已采集的波形历史（界面波形用的同一份环形缓冲，最多 8000 点/变量）。</summary>
    [HttpGet("history")]
    public IActionResult History([FromQuery] string names, [FromQuery] int maxPoints = 1000)
    {
        if (string.IsNullOrWhiteSpace(names))
            return BadRequest(new { error = "names 不能为空，多个用逗号分隔" });

        maxPoints = Math.Clamp(maxPoints, 2, 8000);

        var series = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        foreach (string name in names.Split(',',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var history = _context.Variables.GetHistory(name);
            if (history.Count == 0)
            {
                errors.Add($"无历史数据: {name}");
                continue;
            }

            // 抽稀到 maxPoints 以内，但末点一定保留，避免曲线末端缺一截
            int stride = Math.Max(1, (int)Math.Ceiling(history.Count / (double)maxPoints));
            var points = new List<object>();
            for (int i = 0; i < history.Count; i += stride)
                points.Add(new { t = history[i].Timestamp, v = history[i].Value });

            var last = history[^1];
            if ((history.Count - 1) % stride != 0)
                points.Add(new { t = last.Timestamp, v = last.Value });

            series[name] = new
            {
                storedPoints = history.Count,
                returnedPoints = points.Count,
                firstTimestampMs = history[0].Timestamp,
                lastTimestampMs = last.Timestamp,
                points,
            };
        }

        return Ok(new
        {
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            maxPoints,
            count = series.Count,
            errors,
            series,
        });
    }

    /// <summary>清空波形历史（界面波形与外部读取共用同一份缓冲）。</summary>
    [HttpPost("history/clear")]
    public IActionResult ClearHistory()
    {
        _context.Variables.ClearHistory();
        _context.Audit.Add("skill", "HISTORY-CLEAR", "/api/v1/variables/history/clear", "清空波形历史");
        return Ok(new { success = true, message = "已清空波形历史" });
    }

    private static bool TryParseColor(string text, out int argb)
    {
        argb = 0;
        text = text.Trim();
        if (text.StartsWith('#')) return int.TryParse(text[1..], System.Globalization.NumberStyles.HexNumber, null, out argb);
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out argb);
        return int.TryParse(text, out argb);
    }

    /// <summary>搜索符号（AI 在不知道确切变量名时可先检索）。</summary>
    [HttpGet("search")]
    public IActionResult Search([FromQuery] string keyword, [FromQuery] int max = 50)
    {
        var items = _context.Device.Symbols.Search(keyword, Math.Clamp(max, 1, 500))
            .Select(s => new
            {
                name = s.FullPath,
                address = $"0x{s.Address:X8}",
                type = s.Type.ToCName(),
                size = s.ByteSize,
            });
        return Ok(new { keyword, results = items });
    }

    /// <summary>分页列出全部符号（不做关键字过滤，供外部程序完整枚举）。</summary>
    [HttpGet("all")]
    public IActionResult All([FromQuery] int offset = 0, [FromQuery] int limit = 200,
        [FromQuery] string? keyword = null, [FromQuery] bool leavesOnly = false)
    {
        limit = Math.Clamp(limit, 1, 1000);
        offset = Math.Max(0, offset);

        IEnumerable<WatchSymbol> source = leavesOnly
            ? _context.Device.Symbols.AllLeaves()
            : _context.Device.Symbols.Roots;

        var all = source
            .Where(s => string.IsNullOrWhiteSpace(keyword) ||
                        s.FullPath.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var page = all.Skip(offset).Take(limit).Select(s => new
        {
            name = s.FullPath,
            address = $"0x{s.Address:X8}",
            type = s.Type.ToCName(),
            size = s.ByteSize,
            container = s.IsContainer,
            children = s.Children.Count,
        });

        return Ok(new
        {
            total = all.Count,
            offset,
            limit,
            hasMore = offset + limit < all.Count,
            results = page,
        });
    }

    /// <summary>把变量移出监控列表（界面表格同步移除）。</summary>
    [HttpPost("unwatch")]
    public IActionResult Unwatch([FromBody] VariableReadRequest request)
    {
        var removed = new List<string>();
        var errors = new List<string>();

        foreach (string name in request.Names)
        {
            var symbol = _context.Variables.WatchedRoots
                .FirstOrDefault(s => string.Equals(s.FullPath, name, StringComparison.OrdinalIgnoreCase));

            if (symbol == null)
            {
                errors.Add($"不在监控列表: {name}");
                continue;
            }

            _context.Variables.RemoveWatch(symbol);
            removed.Add(symbol.FullPath);
        }

        _context.Audit.Add("skill", "UNWATCH", "/api/v1/variables/unwatch",
            $"移除 {removed.Count} 项", 0, errors.Count == 0);

        return Ok(new { removed, errors, remaining = _context.Variables.WatchedRoots.Count });
    }
}
