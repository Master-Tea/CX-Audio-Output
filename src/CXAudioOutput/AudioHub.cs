using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace CXAudioOutput;

public sealed record ReceiverSnapshot(
    string Id,
    string Name,
    string Platform,
    string Address,
    int Volume,
    bool Muted,
    int RttMs,
    int BufferMs,
    string Playback,
    long Underruns,
    long Dropped,
    DateTimeOffset ConnectedAt);

public sealed class ReceiverSession : IAsyncDisposable
{
    private readonly Channel<OutboundMessage> _outbox;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _sync = new();
    private readonly ILogger _logger;
    private Task? _sendTask;
    private int _closed;

    private string _name;
    private string _playback = "buffering";
    private int _volume = 100;
    private bool _muted;
    private int _rttMs;
    private int _bufferMs;
    private long _underruns;
    private long _dropped;
    private long _lastSeenTicks = DateTime.UtcNow.Ticks;

    public ReceiverSession(WebSocket socket, string name, string platform, string address,
        ILogger logger)
    {
        Socket = socket;
        _name = name;
        Platform = platform;
        Address = address;
        _logger = logger;
        Id = Guid.NewGuid().ToString("N")[..10];
        ConnectedAt = DateTimeOffset.UtcNow;
        _outbox = Channel.CreateBounded<OutboundMessage>(new BoundedChannelOptions(96)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    }

    public string Id { get; }
    public WebSocket Socket { get; }
    public string Platform { get; }
    public string Address { get; }
    public DateTimeOffset ConnectedAt { get; }

    public string Name
    {
        get { lock (_sync) return _name; }
    }

    public DateTime LastSeen
    {
        get { lock (_sync) return new DateTime(Interlocked.Read(ref _lastSeenTicks), DateTimeKind.Utc); }
    }

    public void Start() => _sendTask = Task.Run(SendLoopAsync);

    public void Touch()
    {
        Interlocked.Exchange(ref _lastSeenTicks, DateTime.UtcNow.Ticks);
    }

    public void EnqueueJson(string json)
    {
        _outbox.Writer.TryWrite(new OutboundMessage(null, json));
    }

    public void EnqueueAudio(byte[] frame)
    {
        if (_outbox.Reader.Count > 70)
        {
            Interlocked.Increment(ref _dropped);
        }

        _outbox.Writer.TryWrite(new OutboundMessage(frame, null));
    }

    public void UpdateStats(int bufferMs, string? playback, long underruns, long overruns)
    {
        lock (_sync)
        {
            _bufferMs = Math.Clamp(bufferMs, 0, 5000);
            if (!string.IsNullOrWhiteSpace(playback)) _playback = playback;
            _underruns = Math.Max(_underruns, underruns);
            _lastSeenTicks = DateTime.UtcNow.Ticks;
        }
    }

    public void SetPlayback(string playback)
    {
        lock (_sync)
        {
            _playback = playback;
            _lastSeenTicks = DateTime.UtcNow.Ticks;
        }
    }

    public void SetVolume(int volume)
    {
        lock (_sync)
        {
            _volume = Math.Clamp(volume, 0, 100);
            _lastSeenTicks = DateTime.UtcNow.Ticks;
        }
    }

    public void SetMuted(bool muted)
    {
        lock (_sync)
        {
            _muted = muted;
            _lastSeenTicks = DateTime.UtcNow.Ticks;
        }
    }

    public void SetName(string name)
    {
        lock (_sync)
        {
            _name = name;
            _lastSeenTicks = DateTime.UtcNow.Ticks;
        }
    }

    public void SetRtt(int rtt)
    {
        lock (_sync)
        {
            _rttMs = Math.Clamp(rtt, 0, 10000);
            _lastSeenTicks = DateTime.UtcNow.Ticks;
        }
    }

    public ReceiverSnapshot Snapshot()
    {
        lock (_sync)
        {
            return new ReceiverSnapshot(Id, _name, Platform, Address, _volume, _muted, _rttMs,
                _bufferMs, _playback, _underruns, Interlocked.Read(ref _dropped), ConnectedAt);
        }
    }

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var message in _outbox.Reader.ReadAllAsync(_cts.Token))
            {
                if (Socket.State != WebSocketState.Open) break;

                if (message.Binary is not null)
                {
                    await Socket.SendAsync(message.Binary, WebSocketMessageType.Binary, true, _cts.Token);
                }
                else if (message.Text is not null)
                {
                    var bytes = Encoding.UTF8.GetBytes(message.Text);
                    await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Receiver send loop ended for {Name}", Name);
            try
            {
                Socket.Abort();
            }
            catch
            {
                // ignored
            }
        }
    }

    public async Task CloseAsync(int code, string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _outbox.Writer.TryComplete();
        try
        {
            if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
                await Socket.CloseOutputAsync((WebSocketCloseStatus)code, reason, timeout.Token);
            }
        }
        catch
        {
            // ignored
        }

        try
        {
            _cts.Cancel();
        }
        catch
        {
            // ignored
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync(1001, "disconnect");
        try
        {
            Socket.Dispose();
        }
        catch
        {
            // ignored
        }

        if (_sendTask is not null)
        {
            try
            {
                await _sendTask.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch
            {
                // ignored
            }
        }

        _cts.Dispose();
    }
}

public sealed class HostSession : IAsyncDisposable
{
    private readonly Channel<string> _outbox;
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger _logger;
    private Task? _sendTask;
    private int _closed;

    public HostSession(WebSocket socket, ILogger logger)
    {
        Socket = socket;
        _logger = logger;
        _outbox = Channel.CreateBounded<string>(new BoundedChannelOptions(24)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    }

    public WebSocket Socket { get; }
    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];

    public void Start() => _sendTask = Task.Run(SendLoopAsync);

    public void Enqueue(string json) => _outbox.Writer.TryWrite(json);

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var json in _outbox.Reader.ReadAllAsync(_cts.Token))
            {
                if (Socket.State != WebSocketState.Open) break;
                var bytes = Encoding.UTF8.GetBytes(json);
                await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Host send loop ended");
            try
            {
                Socket.Abort();
            }
            catch
            {
                // ignored
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            _outbox.Writer.TryComplete();
            try
            {
                if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                    await Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token);
                }
            }
            catch
            {
                // ignored
            }

            _cts.Cancel();
        }

        try
        {
            Socket.Dispose();
        }
        catch
        {
            // ignored
        }

        if (_sendTask is not null)
        {
            try
            {
                await _sendTask.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch
            {
                // ignored
            }
        }

        _cts.Dispose();
    }
}

public sealed class AudioHub
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AppState _state;
    private readonly NetworkSelectionService _network;
    private readonly LaunchOptions _options;
    private readonly ILogger<AudioHub> _logger;
    private readonly ConcurrentDictionary<string, ReceiverSession> _receivers = new();
    private readonly ConcurrentDictionary<string, HostSession> _hosts = new();
    private long _pingCounter;

    public AudioHub(AppState state, NetworkSelectionService network, LaunchOptions options,
        ILogger<AudioHub> logger)
    {
        _state = state;
        _network = network;
        _options = options;
        _logger = logger;
        _network.Changed += (_, _) => _ = BroadcastStateAsync();
    }

    public int ReceiverCount => _receivers.Count;
    public ICollection<ReceiverSession> Receivers => _receivers.Values;

    public ReceiverSession AddReceiver(WebSocket socket, string name, string platform, string address,
        ILogger logger)
    {
        var session = new ReceiverSession(socket, name, platform, address, logger);
        _receivers[session.Id] = session;
        session.Start();
        session.EnqueueJson(BuildHello(session));
        _logger.LogInformation("Receiver connected: {Name} ({Platform}) from {Address}", name, platform, address);
        _ = BroadcastStateAsync();
        return session;
    }

    public async Task RemoveReceiverAsync(ReceiverSession session)
    {
        if (!_receivers.TryRemove(session.Id, out _))
        {
            return;
        }

        _logger.LogInformation("Receiver disconnected: {Name}", session.Name);
        await session.DisposeAsync();
        _ = BroadcastStateAsync();
    }

    public HostSession AddHost(WebSocket socket, ILogger logger)
    {
        var host = new HostSession(socket, logger);
        _hosts[host.Id] = host;
        host.Start();
        host.Enqueue(BuildStateJson());
        return host;
    }

    public async Task RemoveHostAsync(HostSession host)
    {
        _hosts.TryRemove(host.Id, out _);
        await host.DisposeAsync();
    }

    public void OnAudioFrame(byte[] frame, float peak)
    {
        _state.PushPeak(peak);
        foreach (var receiver in _receivers.Values)
        {
            receiver.EnqueueAudio(frame);
        }
    }

    public void NotifyFormat(int sampleRate, int channels, string? deviceName)
    {
        var json = JsonSerializer.Serialize(new
        {
            type = "format",
            sampleRate,
            channels,
            deviceName
        }, JsonOptions);

        foreach (var receiver in _receivers.Values)
        {
            receiver.EnqueueJson(json);
        }
    }

    public void NotifyStream(bool playing, string? reason)
    {
        var json = JsonSerializer.Serialize(new
        {
            type = "stream",
            playing,
            reason,
            profile = _state.Profile,
            targetMs = _state.TargetMs
        }, JsonOptions);

        foreach (var receiver in _receivers.Values)
        {
            receiver.EnqueueJson(json);
        }
    }

    public void SetMasterVolume(int volume)
    {
        _state.SetMasterVolume(volume);
        BroadcastMaster();
        _ = BroadcastStateAsync();
    }

    public void SetMuted(bool muted)
    {
        _state.SetMuted(muted);
        BroadcastMaster();
        _ = BroadcastStateAsync();
    }

    public void SetProfile(string profile)
    {
        _state.SetProfile(profile);
        var json = JsonSerializer.Serialize(new
        {
            type = "profile",
            profile = _state.Profile,
            targetMs = _state.TargetMs
        }, JsonOptions);

        foreach (var receiver in _receivers.Values)
        {
            receiver.EnqueueJson(json);
        }

        _ = BroadcastStateAsync();
    }

    public bool SetDeviceVolume(string id, int volume)
    {
        if (!_receivers.TryGetValue(id, out var receiver)) return false;
        receiver.SetVolume(volume);
        receiver.EnqueueJson(JsonSerializer.Serialize(new { type = "volume", value = volume }, JsonOptions));
        _ = BroadcastStateAsync();
        return true;
    }

    public bool SetDeviceMuted(string id, bool muted)
    {
        if (!_receivers.TryGetValue(id, out var receiver)) return false;
        receiver.SetMuted(muted);
        receiver.EnqueueJson(JsonSerializer.Serialize(new { type = "muted", value = muted }, JsonOptions));
        _ = BroadcastStateAsync();
        return true;
    }

    public bool Identify(string id)
    {
        if (!_receivers.TryGetValue(id, out var receiver)) return false;
        receiver.EnqueueJson(JsonSerializer.Serialize(new { type = "identify" }, JsonOptions));
        return true;
    }

    public async Task KickAsync(string id, string reason = "removed")
    {
        if (!_receivers.TryGetValue(id, out var receiver)) return;
        receiver.EnqueueJson(JsonSerializer.Serialize(new { type = "kicked", reason }, JsonOptions));
        await Task.Delay(60);
        await receiver.CloseAsync(4001, reason);
        await RemoveReceiverAsync(receiver);
    }

    public string RotateCode()
    {
        var code = _state.RotateAccessCode();
        _ = BroadcastStateAsync();
        return code;
    }

    public void UpdateReceiverStats(string id, int bufferMs, string? playback, long underruns,
        long overruns)
    {
        if (!_receivers.TryGetValue(id, out var receiver)) return;
        receiver.UpdateStats(bufferMs, playback, underruns, overruns);
    }

    public void UpdateReceiverVolume(string id, int volume)
    {
        if (!_receivers.TryGetValue(id, out var receiver)) return;
        receiver.SetVolume(volume);
        _ = BroadcastStateAsync();
    }

    public void UpdateReceiverMuted(string id, bool muted)
    {
        if (!_receivers.TryGetValue(id, out var receiver)) return;
        receiver.SetMuted(muted);
        _ = BroadcastStateAsync();
    }

    public void UpdateReceiverName(string id, string name)
    {
        if (!_receivers.TryGetValue(id, out var receiver)) return;
        receiver.SetName(name);
        _ = BroadcastStateAsync();
    }

    public void HandlePong(ReceiverSession receiver, long timestamp)
    {
        var rtt = (int)Math.Clamp(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - timestamp, 0, 10000);
        receiver.SetRtt(rtt);
        receiver.Touch();
        receiver.EnqueueJson(JsonSerializer.Serialize(new { type = "rtt", value = rtt }, JsonOptions));
    }

    public async Task PingReceiversAsync()
    {
        if (_receivers.IsEmpty) return;
        Interlocked.Increment(ref _pingCounter);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var json = JsonSerializer.Serialize(new { type = "ping", t = timestamp, n = _pingCounter }, JsonOptions);
        foreach (var receiver in _receivers.Values)
        {
            receiver.EnqueueJson(json);
        }

        var stale = _receivers.Values
            .Where(r => DateTime.UtcNow - r.LastSeen > TimeSpan.FromSeconds(18))
            .ToList();

        foreach (var receiver in stale)
        {
            _logger.LogInformation("Closing stale receiver {Name}", receiver.Name);
            await receiver.CloseAsync(4000, "timeout");
            await RemoveReceiverAsync(receiver);
        }
    }

    public Task BroadcastStateAsync()
    {
        if (_hosts.IsEmpty) return Task.CompletedTask;
        var json = BuildStateJson();
        foreach (var host in _hosts.Values)
        {
            host.Enqueue(json);
        }

        return Task.CompletedTask;
    }

    public string BuildStateJson()
    {
        var snap = _state.Snapshot();
        var addresses = _network.GetAdvertisedAddresses()
            .Select(a => new
            {
                ip = a.Ip,
                network = a.InterfaceName,
                url = a.BaseUrl(_options.Port)
            })
            .ToArray();

        var preferred = addresses.FirstOrDefault(a => !a.ip.StartsWith("127.", StringComparison.Ordinal))
                        ?? addresses.FirstOrDefault();

        var receivers = _receivers.Values
            .Select(r => r.Snapshot())
            .OrderBy(r => r.ConnectedAt)
            .Select(r => new
            {
                id = r.Id,
                name = r.Name,
                platform = r.Platform,
                address = r.Address,
                volume = r.Volume,
                muted = r.Muted,
                rttMs = r.RttMs,
                bufferMs = r.BufferMs,
                playback = r.Playback,
                underruns = r.Underruns,
                dropped = r.Dropped,
                connectedAt = r.ConnectedAt
            })
            .ToArray();

        var payload = new
        {
            type = "state",
            app = "CX Audio Output",
            version = _state.VersionString,
            serverName = _state.ServerName,
            startedAt = _state.StartedAt,
            accessCode = snap.AccessCode,
            demo = _state.Demo,
            port = _options.Port,
            capture = new
            {
                status = snap.Capture.Status,
                deviceName = snap.Capture.DeviceName,
                sampleRate = snap.Capture.SampleRate,
                channels = snap.Capture.Channels,
                bitDepth = snap.Capture.BitDepth,
                error = snap.Capture.Error,
                paused = snap.Capture.Paused
            },
            masterVolume = snap.MasterVolume,
            muted = snap.Muted,
            profile = snap.Profile,
            targetMs = snap.TargetMs,
            peak = snap.Peak,
            levels = snap.Levels,
            addresses,
            preferredUrl = preferred?.url,
            receivers
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private string BuildHello(ReceiverSession receiver)
    {
        var capture = _state.Capture;
        var playing = capture.Status == "live";
        return JsonSerializer.Serialize(new
        {
            type = "hello",
            receiverId = receiver.Id,
            sampleRate = capture.SampleRate > 0 ? capture.SampleRate : 48000,
            channels = 2,
            bitDepth = 16,
            frameMs = 20,
            serverName = _state.ServerName,
            profile = _state.Profile,
            targetMs = _state.TargetMs,
            masterVolume = _state.MasterVolume,
            muted = _state.Muted,
            playing,
            paused = capture.Paused,
            captureStatus = capture.Status,
            demo = _state.Demo
        }, JsonOptions);
    }

    private void BroadcastMaster()
    {
        var json = JsonSerializer.Serialize(new
        {
            type = "master",
            volume = _state.MasterVolume,
            muted = _state.Muted
        }, JsonOptions);

        foreach (var receiver in _receivers.Values)
        {
            receiver.EnqueueJson(json);
        }
    }

}

public readonly record struct OutboundMessage(byte[]? Binary, string? Text);
