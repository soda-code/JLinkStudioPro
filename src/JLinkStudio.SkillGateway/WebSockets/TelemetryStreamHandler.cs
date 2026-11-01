using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using JLinkStudio.Core.Models;
using JLinkStudio.Core.Services;
using Microsoft.AspNetCore.Http;

namespace JLinkStudio.SkillGateway.WebSockets;

/// <summary>
/// WebSocket 实时遥测流：把 RTT 日志与变量采样推送给外部 Skill。
/// 客户端可下发命令：{"type":"command","text":"reboot"} / {"type":"read","names":["g_motorPid.kp"]}
/// </summary>
public static class TelemetryStreamHandler
{
    private sealed record OutboundMessage(string Type, object Payload);

    public static async Task HandleAsync(HttpContext httpContext, StudioContext studio)
    {
        if (!httpContext.WebSockets.IsWebSocketRequest)
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsync("WebSocket endpoint. Connect with ws:// protocol.");
            return;
        }

        using var socket = await httpContext.WebSockets.AcceptWebSocketAsync();
        var outbox = Channel.CreateUnbounded<OutboundMessage>();

        void OnRtt(object? sender, List<RttLine> lines)
        {
            var payload = lines.Select(l => new
            {
                timestamp = l.TimestampMs,
                channel = l.Channel,
                level = l.Level.ToString(),
                text = l.Text,
                fields = l.Fields,
            }).ToList();
            outbox.Writer.TryWrite(new OutboundMessage("rtt", payload));
        }

        void OnVariables(object? sender, List<VariableSample> samples)
        {
            var payload = samples.Select(s => new
            {
                name = s.Name,
                address = $"0x{s.Address:X8}",
                type = s.Type.ToCName(),
                value = s.Value,
                text = s.ValueText,
                timestamp = s.TimestampMs,
            }).ToList();
            outbox.Writer.TryWrite(new OutboundMessage("variables", payload));
        }

        void OnAudit(object? sender, SkillAuditEntry entry)
        {
            outbox.Writer.TryWrite(new OutboundMessage("audit", new
            {
                entry.TimestampMs,
                entry.Source,
                entry.Method,
                entry.Path,
                entry.Detail,
                entry.Success,
            }));
        }

        studio.Rtt.LinesReceived += OnRtt;
        studio.Variables.ValuesUpdated += OnVariables;
        studio.Audit.EntryAdded += OnAudit;

        outbox.Writer.TryWrite(new OutboundMessage("hello", new
        {
            server = "JLink-Studio-Pro",
            status = studio.BuildStatus(),
            subscriptions = new[] { "rtt", "variables", "audit" },
        }));

        try
        {
            var token = httpContext.RequestAborted;
            var sendTask = SendLoopAsync(socket, outbox.Reader, token);
            var receiveTask = ReceiveLoopAsync(socket, studio, outbox.Writer, token);
            await Task.WhenAny(sendTask, receiveTask);
        }
        catch
        {
            // 客户端断开属于正常流程
        }
        finally
        {
            studio.Rtt.LinesReceived -= OnRtt;
            studio.Variables.ValuesUpdated -= OnVariables;
            studio.Audit.EntryAdded -= OnAudit;
            outbox.Writer.TryComplete();

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                }
                catch
                {
                    // ignore
                }
            }
        }
    }

    private static async Task SendLoopAsync(WebSocket socket, ChannelReader<OutboundMessage> reader, CancellationToken token)
    {
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            OutboundMessage message;
            try
            {
                message = await reader.ReadAsync(token);
            }
            catch (ChannelClosedException)
            {
                break;
            }

            var json = JsonSerializer.Serialize(new { type = message.Type, data = message.Payload }, options);
            var bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, token);
        }
    }

    private static async Task ReceiveLoopAsync(WebSocket socket, StudioContext studio,
        ChannelWriter<OutboundMessage> outbox, CancellationToken token)
    {
        var buffer = new byte[8192];
        var builder = new StringBuilder();

        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close) break;

            builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (!result.EndOfMessage) continue;

            var text = builder.ToString();
            builder.Clear();
            if (text.Length == 0) continue;

            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                string type = root.TryGetProperty("type", out var t) ? t.GetString() ?? string.Empty : string.Empty;

                switch (type)
                {
                    case "command":
                    {
                        string command = root.TryGetProperty("text", out var c) ? c.GetString() ?? string.Empty : string.Empty;
                        int channel = root.TryGetProperty("channel", out var ch) && ch.TryGetInt32(out var chv) ? chv : 0;
                        bool ok = studio.Rtt.Send(command, channel);
                        outbox.TryWrite(new OutboundMessage("ack", new { action = "command", success = ok, text = command }));
                        break;
                    }
                    case "read":
                    {
                        var samples = new List<object>();
                        if (root.TryGetProperty("names", out var names) && names.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var nameElement in names.EnumerateArray())
                            {
                                var name = nameElement.GetString();
                                if (string.IsNullOrWhiteSpace(name)) continue;
                                var sample = studio.Variables.ReadByName(name, out var error);
                                samples.Add(new
                                {
                                    name,
                                    success = sample != null,
                                    value = sample?.Value,
                                    text = sample?.ValueText,
                                    address = sample != null ? $"0x{sample.Address:X8}" : null,
                                    error,
                                });
                            }
                        }
                        outbox.TryWrite(new OutboundMessage("read-result", samples));
                        break;
                    }
                    case "ping":
                        outbox.TryWrite(new OutboundMessage("pong", new { timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }));
                        break;
                    default:
                        outbox.TryWrite(new OutboundMessage("error", new { message = $"未知消息类型: {type}" }));
                        break;
                }
            }
            catch (JsonException ex)
            {
                outbox.TryWrite(new OutboundMessage("error", new { message = "JSON 解析失败: " + ex.Message }));
            }
        }
    }
}
