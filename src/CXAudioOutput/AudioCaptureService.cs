using System.Buffers.Binary;
using System.Diagnostics;

namespace CXAudioOutput;

public sealed class AudioCaptureService : BackgroundService
{
    private readonly AppState _state;
    private readonly AudioHub _hub;
    private readonly LaunchOptions _options;
    private readonly ILogger<AudioCaptureService> _logger;
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _paused;
    private volatile bool _acceptFrames;

    public AudioCaptureService(AppState state, AudioHub hub, LaunchOptions options,
        ILogger<AudioCaptureService> logger)
    {
        _state = state;
        _hub = hub;
        _options = options;
        _logger = logger;
    }

    public bool IsPaused => _paused;

    public void SetPaused(bool paused)
    {
        if (_paused == paused) return;
        _paused = paused;
        _acceptFrames = false;
        _state.SetPaused(paused);
        _hub.NotifyStream(false, paused ? "paused" : "starting");
        _wake.Set();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        stoppingToken.Register(() =>
        {
            try
            {
                _wake.Set();
            }
            catch
            {
                // ignored
            }
        });

        await Task.Run(() => RunLoop(stoppingToken), stoppingToken);
    }

    private void RunLoop(CancellationToken cancellationToken)
    {
        try
        {
            // Capture is tiny compared with UI/Web work; keep its packet pump
            // ahead of ordinary thread-pool work to avoid timing jitter.
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        }
        catch
        {
            // Priority adjustment is best effort.
        }

        using var mmcss = MmcssRegistration.TryRegister("Pro Audio");
        RunLoopCore(cancellationToken);
    }

    private void RunLoopCore(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_paused)
            {
                _acceptFrames = false;
                Wait(120, cancellationToken);
                continue;
            }

            if (_options.Demo)
            {
                RunDemo(cancellationToken);
                continue;
            }

            try
            {
                _acceptFrames = false;
                _state.SetCapture("starting", null, 0, 0, 0);
                _hub.NotifyStream(false, "starting");

                using var session = WasapiLoopbackSession.Create();
                var format = session.Format;
                session.Start();
                _acceptFrames = true;
                _state.SetCapture("live", session.DeviceName, format.SampleRate, 2, 16);
                _hub.NotifyFormat(format.SampleRate, 2, session.DeviceName);
                _hub.NotifyStream(true, null);
                _logger.LogInformation("Streaming system audio: {Device} ({Rate} Hz, {Channels} ch, {Bits}-bit)",
                    session.DeviceName, format.SampleRate, format.Channels, format.BitsPerSample);

                session.Run(cancellationToken, _wake, () => _paused, OnFrame);
                _acceptFrames = false;

                if (!cancellationToken.IsCancellationRequested && !_paused)
                {
                    _logger.LogInformation("Output device changed. Restarting capture.");
                    _state.SetCapture("starting", session.DeviceName, format.SampleRate, 2, 16);
                    _hub.NotifyStream(false, "starting");
                    Wait(400, cancellationToken);
                }
            }
            catch (WasapiException ex)
            {
                _acceptFrames = false;
                var status = ex.IsNoDevice ? "no-device" : "error";
                _state.SetCapture(status, null, 0, 0, 0, ex.Message);
                _hub.NotifyStream(false, status);
                _logger.LogWarning("{Status}: {Message}", status, ex.Message);
                Wait(2500, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _acceptFrames = false;
                _state.SetCapture("error", null, 0, 0, 0, ex.Message);
                _hub.NotifyStream(false, "error");
                _logger.LogError(ex, "Audio capture failed");
                Wait(3000, cancellationToken);
            }
        }
    }

    private void OnFrame(byte[] frame, float peak)
    {
        if (!_acceptFrames || _paused) return;
        _hub.OnAudioFrame(frame, peak);
    }

    private void RunDemo(CancellationToken cancellationToken)
    {
        const int sampleRate = 48000;
        const int framesPerChunk = sampleRate / 50;
        const double twoPi = Math.PI * 2;

        _acceptFrames = true;
        _state.SetCapture("live", "演示信号 · Demo oscillator", sampleRate, 2, 16);
        _hub.NotifyFormat(sampleRate, 2, "演示信号");
        _hub.NotifyStream(true, null);
        _logger.LogInformation("Demo oscillator streaming at 48 kHz");

        var chunk = new byte[framesPerChunk * 4];
        long frameIndex = 0;
        var periodTicks = Math.Max(1, Stopwatch.Frequency / 50);
        var nextTick = Stopwatch.GetTimestamp();

        while (!cancellationToken.IsCancellationRequested && !_paused)
        {
            var t0 = frameIndex / (double)sampleRate;
            var peak = 0f;
            for (var i = 0; i < framesPerChunk; i++)
            {
                var t = t0 + i / (double)sampleRate;
                var tremolo = 0.58 + 0.42 * Math.Pow(0.5 + 0.5 * Math.Sin(twoPi * 0.72 * t), 2);
                var left = (0.10 * Math.Sin(twoPi * 220.0 * t)
                            + 0.055 * Math.Sin(twoPi * 329.63 * t)
                            + 0.03 * Math.Sin(twoPi * 440.0 * t)) * tremolo;
                var right = (0.10 * Math.Sin(twoPi * 277.18 * t)
                             + 0.055 * Math.Sin(twoPi * 392.0 * t)
                             + 0.03 * Math.Sin(twoPi * 554.37 * t)) * tremolo;
                var l = (short)Math.Clamp((int)Math.Round(left * 32767), -32768, 32767);
                var r = (short)Math.Clamp((int)Math.Round(right * 32767), -32768, 32767);
                peak = MathF.Max(peak, MathF.Max(MathF.Abs(l / 32768f), MathF.Abs(r / 32768f)));
                BinaryPrimitives.WriteInt16LittleEndian(chunk.AsSpan(i * 4, 2), l);
                BinaryPrimitives.WriteInt16LittleEndian(chunk.AsSpan(i * 4 + 2, 2), r);
            }

            frameIndex += framesPerChunk;
            _hub.OnAudioFrame(chunk, peak);
            chunk = new byte[framesPerChunk * 4];

            nextTick += periodTicks;
            var now = Stopwatch.GetTimestamp();
            if (now - nextTick > periodTicks * 5)
            {
                nextTick = now;
            }

            if (nextTick > now)
            {
                var remainingMs = (int)((nextTick - now) * 1000 / Stopwatch.Frequency);
                if (remainingMs > 2)
                {
                    Wait(Math.Min(remainingMs - 1, 19), cancellationToken);
                }

                while (!cancellationToken.IsCancellationRequested && !_paused
                       && Stopwatch.GetTimestamp() < nextTick)
                {
                    Thread.SpinWait(120);
                }
            }
        }

        _acceptFrames = false;
        if (!cancellationToken.IsCancellationRequested)
        {
            _hub.NotifyStream(false, "paused");
        }
    }

    private void Wait(int milliseconds, CancellationToken cancellationToken)
    {
        if (milliseconds <= 0) return;
        try
        {
            _wake.WaitOne(milliseconds);
        }
        catch
        {
            // ignored
        }
    }

    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }
}
