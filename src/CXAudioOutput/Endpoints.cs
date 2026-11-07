using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace CXAudioOutput;

public enum JoinVerdict
{
    Ok,
    Invalid,
    RateLimited
}

public sealed class AccessGuard
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Attempt> _attempts = new();

    public JoinVerdict Check(string ip, string? code, string expected)
    {
        var now = DateTime.UtcNow;
        lock (_sync)
        {
            if (!_attempts.TryGetValue(ip, out var attempt))
            {
                attempt = new Attempt { WindowStart = now };
                _attempts[ip] = attempt;
            }

            if (attempt.BlockedUntil > now)
            {
                return JoinVerdict.RateLimited;
            }

            if (now - attempt.WindowStart > TimeSpan.FromMinutes(1))
            {
                attempt.WindowStart = now;
                attempt.Failures = 0;
            }

            if (AccessCodes.FixedTimeEquals(code?.Trim(), expected))
            {
                _attempts.Remove(ip);
                return JoinVerdict.Ok;
            }

            attempt.Failures++;
            if (attempt.Failures >= 12)
            {
                attempt.BlockedUntil = now.AddMinutes(1);
                attempt.Failures = 0;
                attempt.WindowStart = now;
                return JoinVerdict.RateLimited;
            }

            return JoinVerdict.Invalid;
        }
    }

    private sealed class Attempt
    {
        public DateTime WindowStart;
        public DateTime BlockedUntil;
        public int Failures;
    }
}

public static class Endpoints
{
    private const string IndexFile = "index.html";
    private const string StudioFile = "studio.html";

    public static void MapCxEndpoints(this WebApplication app, LaunchOptions options)
    {
        app.MapGet("/api/health", (AppState state) => Results.Json(new
        {
            ok = true,
            app = "CX Audio Output",
            version = state.VersionString,
            demo = options.Demo
        }));

        app.MapGet("/api/join", (HttpContext context, AppState state, AccessGuard guard) =>
        {
            var ip = NetTools.AddressOf(context);
            var verdict = guard.Check(ip, context.Request.Query["code"].ToString(), state.AccessCode);
            return verdict switch
            {
                JoinVerdict.Ok => Results.Json(new { ok = true, serverName = state.ServerName }),
                JoinVerdict.RateLimited => Results.Json(new { ok = false, error = "rate-limited" },
                    statusCode: StatusCodes.Status429TooManyRequests),
                _ => Results.Json(new { ok = false, error = "invalid-code" },
                    statusCode: StatusCodes.Status403Forbidden)
            };
        });

        app.MapPost("/api/shutdown", (HttpContext context, IHostApplicationLifetime lifetime) =>
        {
            if (!NetTools.IsLocal(context) || !NetTools.IsSameOrigin(context)) return Results.NotFound();
            _ = Task.Run(async () =>
            {
                await Task.Delay(250);
                lifetime.StopApplication();
            });
            return Results.Json(new { ok = true });
        });

        app.MapGet("/", (HttpContext context) =>
        {
            if (NetTools.IsLocal(context))
            {
                return Results.Redirect("/studio");
            }

            return ServeHtml(context, IndexFile);
        });

        app.MapGet("/listen", (HttpContext context) => ServeHtml(context, IndexFile));

        app.MapGet("/studio", (HttpContext context) =>
        {
            if (!NetTools.IsLocal(context)) return Results.NotFound();
            return ServeHtml(context, StudioFile);
        });

        app.Map("/ws/host", HandleHostSocketAsync);
        app.Map("/ws/audio", HandleAudioSocketAsync);
    }

    private static IResult ServeHtml(HttpContext context, string fileName)
    {
        var webRoot = context.RequestServices.GetRequiredService<IWebHostEnvironment>().WebRootPath;
        var path = Path.Combine(webRoot ?? Path.Combine(AppContext.BaseDirectory, "wwwroot"), fileName);
        if (!File.Exists(path)) return Results.NotFound();
        context.Response.Headers.CacheControl = "no-store, max-age=0";
        return Results.File(path, "text/html; charset=utf-8");
    }

    private static async Task HandleHostSocketAsync(HttpContext context, AudioHub hub,
        AudioCaptureService capture, LaunchOptions options, ILoggerFactory loggerFactory)
    {
        if (!NetTools.IsLocal(context) || !NetTools.IsSameOrigin(context))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var logger = loggerFactory.CreateLogger("HostSocket");
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var host = hub.AddHost(socket, logger);
        var buffer = new byte[16 * 1024];

        try
        {
            while (!context.RequestAborted.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye",
                        CancellationToken.None);
                    break;
                }

                if (result.MessageType != WebSocketMessageType.Text) continue;

                var json = await ReadTextMessageAsync(socket, result, buffer, context.RequestAborted);
                if (json is null) continue;
                await HandleHostActionAsync(json, hub, capture, options, logger);
            }
        }
        catch (OperationCanceledException)
        {
            // Browser closed.
        }
        catch (WebSocketException)
        {
            // Browser closed.
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Host socket ended");
        }
        finally
        {
            await hub.RemoveHostAsync(host);
        }
    }

    private static async Task HandleHostActionAsync(string json, AudioHub hub,
        AudioCaptureService capture, LaunchOptions options, ILogger logger)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (!root.TryGetProperty("action", out var actionElement)) return;
            var action = actionElement.GetString();

            switch (action)
            {
                case "pause":
                    capture.SetPaused(GetBool(root, "value"));
                    break;
                case "masterVolume":
                    hub.SetMasterVolume(GetInt(root, "value", 100));
                    break;
                case "muted":
                    hub.SetMuted(GetBool(root, "value"));
                    break;
                case "profile":
                    hub.SetProfile(GetString(root, "value") ?? "balanced");
                    break;
                case "deviceVolume":
                {
                    var id = GetString(root, "id");
                    if (id is not null) hub.SetDeviceVolume(id, GetInt(root, "value", 100));
                    break;
                }
                case "deviceMuted":
                {
                    var id = GetString(root, "id");
                    if (id is not null) hub.SetDeviceMuted(id, GetBool(root, "value"));
                    break;
                }
                case "identify":
                {
                    var id = GetString(root, "id");
                    if (id is not null) hub.Identify(id);
                    break;
                }
                case "kick":
                {
                    var id = GetString(root, "id");
                    if (id is not null) await hub.KickAsync(id);
                    break;
                }
                case "rotateCode":
                    hub.RotateCode();
                    break;
                case "sync":
                    break;
            }

            await hub.BroadcastStateAsync();
        }
        catch (JsonException)
        {
            // Ignore malformed UI messages.
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Host action failed: {Json}", json);
        }
    }

    private static async Task HandleAudioSocketAsync(HttpContext context, AudioHub hub, AppState state,
        AccessGuard guard, LaunchOptions options, ILoggerFactory loggerFactory)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!NetTools.IsSameOrigin(context))
        {
            using var rejected = await context.WebSockets.AcceptWebSocketAsync();
            try
            {
                await rejected.CloseOutputAsync((WebSocketCloseStatus)4403, "origin-not-allowed",
                    CancellationToken.None);
            }
            catch
            {
                // ignored
            }

            return;
        }

        var logger = loggerFactory.CreateLogger("AudioSocket");
        var ip = NetTools.AddressOf(context);
        var code = context.Request.Query["code"].ToString();
        var verdict = guard.Check(ip, code, state.AccessCode);

        if (verdict != JoinVerdict.Ok)
        {
            using var rejected = await context.WebSockets.AcceptWebSocketAsync();
            var status = verdict == JoinVerdict.RateLimited ? 4429 : 4401;
            var reason = verdict == JoinVerdict.RateLimited ? "rate-limited" : "invalid-code";
            try
            {
                await rejected.CloseOutputAsync((WebSocketCloseStatus)status, reason,
                    CancellationToken.None);
            }
            catch
            {
                // ignored
            }

            return;
        }

        var name = NetTools.SanitizeName(context.Request.Query["name"].ToString(), "未命名设备");
        var platform = NetTools.SanitizeName(context.Request.Query["platform"].ToString(), "浏览器", 24);
        var address = ip;

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var session = hub.AddReceiver(socket, name, platform, address, logger);
        var buffer = new byte[16 * 1024];

        try
        {
            while (!context.RequestAborted.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    try
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye",
                            CancellationToken.None);
                    }
                    catch
                    {
                        // ignored
                    }

                    break;
                }

                if (result.MessageType != WebSocketMessageType.Text) continue;
                var json = await ReadTextMessageAsync(socket, result, buffer, context.RequestAborted);
                if (json is null) continue;
                HandleReceiverMessage(json, session, hub, logger);
            }
        }
        catch (OperationCanceledException)
        {
            // Browser closed.
        }
        catch (WebSocketException)
        {
            // Browser closed.
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Audio socket ended for {Name}", session.Name);
        }
        finally
        {
            await hub.RemoveReceiverAsync(session);
        }
    }

    private static void HandleReceiverMessage(string json, ReceiverSession session, AudioHub hub,
        ILogger logger)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            session.Touch();

            if (!root.TryGetProperty("type", out var typeElement)) return;
            switch (typeElement.GetString())
            {
                case "pong":
                    if (root.TryGetProperty("t", out var t) && t.TryGetInt64(out var timestamp))
                    {
                        hub.HandlePong(session, timestamp);
                    }

                    break;
                case "stats":
                    hub.UpdateReceiverStats(
                        session.Id,
                        GetInt(root, "bufferMs", 0),
                        GetString(root, "playback"),
                        GetLong(root, "underruns"),
                        GetLong(root, "overruns"));
                    break;
                case "volume":
                    hub.UpdateReceiverVolume(session.Id, GetInt(root, "value", 100));
                    break;
                case "muted":
                    hub.UpdateReceiverMuted(session.Id, GetBool(root, "value"));
                    break;
                case "rename":
                {
                    var name = NetTools.SanitizeName(GetString(root, "name") ?? "", session.Name, 32);
                    hub.UpdateReceiverName(session.Id, name);
                    break;
                }
            }
        }
        catch (JsonException)
        {
            // Ignore malformed client messages.
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Receiver message failed");
        }
    }

    private static async Task<string?> ReadTextMessageAsync(WebSocket socket, WebSocketReceiveResult first,
        byte[] buffer, CancellationToken cancellationToken)
    {
        if (first.EndOfMessage)
        {
            return Encoding.UTF8.GetString(buffer, 0, first.Count);
        }

        using var stream = new MemoryStream();
        stream.Write(buffer, 0, first.Count);
        var current = first;
        while (!current.EndOfMessage)
        {
            current = await socket.ReceiveAsync(buffer, cancellationToken);
            if (current.MessageType == WebSocketMessageType.Close) return null;
            stream.Write(buffer, 0, current.Count);
            if (stream.Length > 256 * 1024) return null;
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? GetString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static int GetInt(JsonElement root, string name, int fallback)
    {
        if (!root.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
        {
            return number;
        }

        return fallback;
    }

    private static long GetLong(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return 0;
    }

    private static bool GetBool(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return false;
        return value.ValueKind == JsonValueKind.True
               || (value.ValueKind == JsonValueKind.String
                   && bool.TryParse(value.GetString(), out var parsed) && parsed);
    }
}

public static class NetTools
{
    public static bool IsLocal(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return IPAddress.IsLoopback(address);
    }

    public static string AddressOf(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return address.ToString();
    }

    public static bool IsSameOrigin(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(origin)) return true;

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)) return false;
        var requestHost = context.Request.Host.Host.Trim('[', ']');
        var originHost = originUri.Host.Trim('[', ']');
        if (!string.Equals(requestHost, originHost, StringComparison.OrdinalIgnoreCase)) return false;

        var requestPort = context.Request.Host.Port ?? (context.Request.IsHttps ? 443 : 80);
        return originUri.Port == requestPort;
    }

    public static string DisplayAddress(IPAddress? address)
    {
        if (address is null) return "未知地址";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return address.ToString();
    }

    public static string SanitizeName(string? value, string fallback, int maxLength = 32)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            if (char.IsControl(ch)) continue;
            builder.Append(ch);
            if (builder.Length >= maxLength) break;
        }

        var result = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(result) ? fallback : result;
    }
}
