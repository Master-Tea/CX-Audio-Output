using System.Security.Cryptography;
using System.Text;

namespace CXAudioOutput;

public sealed record CaptureInfo(
    string Status,
    string? DeviceName,
    int SampleRate,
    int Channels,
    int BitDepth,
    string? Error,
    bool Paused)
{
    public static CaptureInfo Initial { get; } = new("starting", null, 0, 0, 0, null, false);
}

public sealed class AppState
{
    public const int LevelCount = 56;
    public const int Version = 1;

    private readonly object _sync = new();
    private readonly double[] _levels = new double[LevelCount];
    private int _levelCursor;
    private string _accessCode;
    private int _masterVolume = 100;
    private bool _muted;
    private string _profile = "balanced";
    private CaptureInfo _capture = CaptureInfo.Initial;
    private double _peak;

    public AppState(LaunchOptions options)
    {
        Demo = options.Demo;
        StartedAt = DateTimeOffset.UtcNow;
        _accessCode = AccessCodes.Create();
        ServerName = FriendlyMachineName();
    }

    public bool Demo { get; }
    public DateTimeOffset StartedAt { get; }
    public string ServerName { get; }

    public string AccessCode
    {
        get { lock (_sync) return _accessCode; }
    }

    public int MasterVolume
    {
        get { lock (_sync) return _masterVolume; }
    }

    public bool Muted
    {
        get { lock (_sync) return _muted; }
    }

    public string Profile
    {
        get { lock (_sync) return _profile; }
    }

    public int TargetMs => TargetMilliseconds(Profile);

    public CaptureInfo Capture
    {
        get { lock (_sync) return _capture; }
    }

    public double Peak
    {
        get { lock (_sync) return _peak; }
    }

    public string VersionString => "1.3.0";

    public static int TargetMilliseconds(string profile) => profile switch
    {
        "low" => 120,
        "stable" => 450,
        _ => 240
    };

    public void SetMasterVolume(int volume)
    {
        lock (_sync) _masterVolume = Math.Clamp(volume, 0, 100);
    }

    public void SetMuted(bool muted)
    {
        lock (_sync) _muted = muted;
    }

    public void SetProfile(string profile)
    {
        if (profile is not ("low" or "balanced" or "stable")) return;
        lock (_sync) _profile = profile;
    }

    public string RotateAccessCode()
    {
        lock (_sync)
        {
            _accessCode = AccessCodes.Create();
            return _accessCode;
        }
    }

    public void SetCapture(string status, string? deviceName, int sampleRate, int channels, int bitDepth,
        string? error = null)
    {
        lock (_sync)
        {
            _capture = new CaptureInfo(status, deviceName, sampleRate, channels, bitDepth, error, status == "paused");
            if (status is "error" or "no-device" or "paused")
            {
                _peak = 0;
            }
        }
    }

    public void SetPaused(bool paused)
    {
        lock (_sync)
        {
            _capture = _capture with
            {
                Status = paused ? "paused" : "starting",
                Paused = paused,
                Error = null
            };
            _peak = 0;
        }
    }

    public void PushPeak(double peak)
    {
        lock (_sync)
        {
            _peak = Math.Clamp(peak, 0, 1);
            _levels[_levelCursor] = _peak;
            _levelCursor = (_levelCursor + 1) % LevelCount;
        }
    }

    public double[] SnapshotLevels()
    {
        lock (_sync)
        {
            var result = new double[LevelCount];
            for (var i = 0; i < LevelCount; i++)
            {
                result[i] = _levels[(_levelCursor + i) % LevelCount];
            }

            return result;
        }
    }

    public StateSnapshot Snapshot()
    {
        lock (_sync)
        {
            var levels = new double[LevelCount];
            for (var i = 0; i < LevelCount; i++)
            {
                levels[i] = _levels[(_levelCursor + i) % LevelCount];
            }

            return new StateSnapshot(
                _accessCode,
                _masterVolume,
                _muted,
                _profile,
                TargetMilliseconds(_profile),
                _capture,
                _peak,
                levels);
        }
    }

    private static string FriendlyMachineName()
    {
        var name = Environment.MachineName;
        if (string.IsNullOrWhiteSpace(name)) return "Windows PC";
        if (name.Length <= 32) return name;
        return name[..32];
    }
}

public sealed record StateSnapshot(
    string AccessCode,
    int MasterVolume,
    bool Muted,
    string Profile,
    int TargetMs,
    CaptureInfo Capture,
    double Peak,
    double[] Levels);

internal static class AccessCodes
{
    public static string Create() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public static bool FixedTimeEquals(string? left, string? right)
    {
        if (left is null || right is null) return false;
        var a = Encoding.ASCII.GetBytes(left);
        var b = Encoding.ASCII.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
