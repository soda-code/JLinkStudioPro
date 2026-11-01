using System.Diagnostics;
using JLinkStudio.Core.Models;
using JLinkStudio.Core.Services;
using JLinkStudio.SkillGateway.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;

namespace JLinkStudio.SkillGateway;

/// <summary>
/// WinForms 进程内自宿主的轻量级 Skill 服务：REST API + WebSocket 实时流 + Swagger。
/// 所有控制器直接复用 <see cref="StudioContext"/> 中的单例引擎，实现零拷贝跨层访问。
/// </summary>
public sealed class EmbeddedHostService
{
    private WebApplication? _app;
    private StudioContext? _context;

    public bool IsRunning => _app != null;
    public int Port { get; private set; } = 5080;
    public string BaseUrl => $"http://127.0.0.1:{Port}";
    public string SwaggerUrl => $"{BaseUrl}/swagger";
    public string OpenApiUrl => $"{BaseUrl}/swagger/v1/swagger.json";
    public string WebSocketUrl => $"ws://127.0.0.1:{Port}/ws/stream";
    public string? LastError { get; private set; }

    public event EventHandler<string>? StateChanged;

    public async Task<bool> StartAsync(int port, StudioContext context)
    {
        if (_app != null)
        {
            StateChanged?.Invoke(this, $"Skill 服务已在运行（:{Port}）");
            return true;
        }

        Port = port;
        _context = context;

        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = "JLinkStudio.SkillGateway",
                EnvironmentName = Environments.Production,
            });

            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel(options =>
            {
                options.ListenLocalhost(port);
                options.AddServerHeader = false;
            });

            builder.Services.AddSingleton(context);
            builder.Services.AddSingleton(this);
            builder.Services.AddControllers()
                .AddApplicationPart(typeof(EmbeddedHostService).Assembly)
                .AddJsonOptions(o => o.JsonSerializerOptions.DefaultIgnoreCondition =
                    System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull);
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo
                {
                    Title = "JLink-Studio-Pro Skill Gateway",
                    Version = "v1",
                    Description = "J-Link 智能调试上位机自动化网关：变量读写、时序采集、寄存器快照、RTT 日志。",
                });
            });
            builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
                p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

            var app = builder.Build();

            // 审计中间件：记录每一次外部 Skill 调用
            app.Use(async (httpContext, next) =>
            {
                var stopwatch = Stopwatch.StartNew();
                string source = httpContext.Request.Headers["X-Skill-Name"].FirstOrDefault()
                                ?? httpContext.Connection.RemoteIpAddress?.ToString()
                                ?? "local";
                try
                {
                    await next(httpContext);
                    stopwatch.Stop();
                    if (!httpContext.Request.Path.StartsWithSegments("/swagger"))
                        context.Audit.Add(source, httpContext.Request.Method, httpContext.Request.Path,
                            $"{(int)httpContext.Response.StatusCode} · {stopwatch.Elapsed.TotalMilliseconds:F1}ms",
                            stopwatch.Elapsed.TotalMilliseconds,
                            httpContext.Response.StatusCode < 400);
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    context.Audit.Add(source, httpContext.Request.Method, httpContext.Request.Path,
                        "异常: " + ex.Message, stopwatch.Elapsed.TotalMilliseconds, false);
                    if (!httpContext.Response.HasStarted)
                    {
                        httpContext.Response.StatusCode = 500;
                        await httpContext.Response.WriteAsJsonAsync(new { error = ex.Message });
                    }
                }
            });

            app.UseCors();
            app.UseSwagger();
            app.UseSwaggerUI(c =>
            {
                c.RoutePrefix = "swagger";
                c.DocumentTitle = "JLink-Studio-Pro Skill API";
            });
            app.UseWebSockets();
            app.MapControllers();

            app.Map("/ws/stream", async socketContext =>
                await TelemetryStreamHandler.HandleAsync(socketContext, context));

            app.MapGet("/", async httpContext =>
            {
                const string html = """
                <!doctype html><html lang="zh-CN"><head><meta charset="utf-8">
                <title>JLink-Studio-Pro Skill Gateway</title>
                <style>
                  body{background:#18181C;color:#E5E7EB;font-family:'Microsoft YaHei UI',sans-serif;padding:40px}
                  h1{color:#3B82F6} code{color:#10B981}
                  li{margin:8px 0} a{color:#60A5FA;text-decoration:none}
                </style></head><body>
                <h1>⚡ JLink-Studio-Pro Skill Gateway</h1>
                <p>嵌入式调试上位机自动化网关正在运行。</p>
                <ul>
                  <li><a href="/swagger">Swagger UI</a></li>
                  <li><code>POST /api/v1/session/config</code> 配置并连接目标</li>
                  <li><code>POST /api/v1/variables/read</code> 按符号名读取变量</li>
                  <li><code>POST /api/v1/variables/capture-timeseries</code> 同步块采集</li>
                  <li><code>GET /api/v1/registers/peripheral/{name}</code> 读取外设寄存器</li>
                  <li><code>GET /api/v1/registers/fault-snapshot</code> 异常现场快照</li>
                  <li><code>ws://127.0.0.1:PORT/ws/stream</code> 实时遥测流</li>
                </ul></body></html>
                """;
                httpContext.Response.ContentType = "text/html; charset=utf-8";
                await httpContext.Response.WriteAsync(html);
            });

            await app.StartAsync();
            _app = app;
            LastError = null;
            context.Audit.Add("system", "START", $"/skill-gateway:{port}", "Skill 网关已启动");
            StateChanged?.Invoke(this, $"Skill 服务已启动：{BaseUrl}");
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _app = null;
            StateChanged?.Invoke(this, $"Skill 服务启动失败：{ex.Message}");
            return false;
        }
    }

    public async Task StopAsync()
    {
        if (_app == null) return;
        try
        {
            // ConfigureAwait(false)：关闭流程不依赖任何 UI 上下文，
            // 防止调用方在 UI 线程同步等待时被 SynchronizationContext 死锁。
            await _app.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }
        _app = null;
        _context?.Audit.Add("system", "STOP", "/skill-gateway", "Skill 网关已停止");
        StateChanged?.Invoke(this, "Skill 服务已停止");
    }

    /// <summary>生成供 AI Agent 直接使用的 MCP / Tool 配置片段。</summary>
    public string BuildMcpConfigJson()
    {
        var payload = new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["jlink-studio-pro"] = new
                {
                    type = "http",
                    url = $"{BaseUrl}/api/v1",
                    openapi = OpenApiUrl,
                    websocket = WebSocketUrl,
                    description = "J-Link 智能调试上位机：变量读写 / 时序采集 / 寄存器诊断 / RTT 日志",
                },
            },
        };
        return System.Text.Json.JsonSerializer.Serialize(payload,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    public void NotifyHardFault(RttLine line)
    {
        _context?.Audit.Add("rtt-monitor", "ALERT", "/rtt/alarm",
            $"[Ch{line.Channel}] {line.Text}", 0, false);
    }
}
