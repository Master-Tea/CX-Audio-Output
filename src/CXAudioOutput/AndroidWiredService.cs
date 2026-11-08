using System.Diagnostics;
using System.Text;

namespace CXAudioOutput;

public sealed record AndroidDeviceInfo(string Serial, string State, string Model)
{
    public bool IsReady => string.Equals(State, "device", StringComparison.OrdinalIgnoreCase);

    public string StateLabel => State.ToLowerInvariant() switch
    {
        "device" => "已授权",
        "unauthorized" => "未授权",
        "offline" => "离线",
        "no permissions" => "无权限",
        "recovery" => "恢复模式",
        "sideload" => "Sideload",
        "bootloader" => "Fastboot",
        _ => State
    };

    public string Display
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Model) ? Serial : Model;
            return $"{name} · {StateLabel} · {Serial}";
        }
    }
}

public sealed record AndroidWiredSnapshot(
    bool AdbAvailable,
    string? AdbPath,
    IReadOnlyList<AndroidDeviceInfo> Devices,
    bool ReverseActive,
    string? ReverseSerial,
    NetworkAdapterSnapshot? UsbTetherAdapter,
    string? Error,
    DateTimeOffset UpdatedAt)
{
    public AndroidDeviceInfo? ReadyDevice => Devices.FirstOrDefault(device => device.IsReady);

    public bool TetherReady => UsbTetherAdapter is { IsUp: true } tether
                              && tether.Ipv4Addresses.Count > 0;
}

/// <summary>
/// Bridges an Android phone to the server over a USB cable. Two independent
/// transports are supported:
///   1. Android USB tethering (RNDIS), which appears as a normal network adapter.
///   2. adb reverse, which forwards the phone's 127.0.0.1:port to this PC.
/// The second option gives the phone a secure localhost origin, so AudioWorklet
/// is available even though the app itself only serves HTTP.
/// </summary>
public sealed class AndroidWiredService : IAsyncDisposable
{
    private static readonly TimeSpan AdbTimeout = TimeSpan.FromSeconds(8);

    private readonly object _sync = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly NetworkSelectionService _network;
    private readonly AppSettingsStore _settingsStore;
    private readonly LaunchOptions _options;
    private readonly ILogger<AndroidWiredService> _logger;

    private string? _adbPath;
    private bool _adbAvailable;
    private List<AndroidDeviceInfo> _devices = new();
    private bool _reverseActive;
    private string? _reverseSerial;
    private string? _error;
    private DateTimeOffset _updatedAt = DateTimeOffset.Now;
    private string _lastSignature = string.Empty;
    private bool _disposed;

    public AndroidWiredService(NetworkSelectionService network, AppSettingsStore settingsStore,
        LaunchOptions options, ILogger<AndroidWiredService> logger)
    {
        _network = network;
        _settingsStore = settingsStore;
        _options = options;
        _logger = logger;

        var settings = settingsStore.Load();
        _adbPath = FindAdb(settings.AdbPath);
        _adbAvailable = _adbPath is not null;
    }

    public event EventHandler? Changed;

    public AndroidWiredSnapshot GetSnapshot()
    {
        var tether = FindUsbTetherAdapter();
        lock (_sync)
        {
            return new AndroidWiredSnapshot(
                _adbAvailable,
                _adbPath,
                _devices.ToArray(),
                _reverseActive,
                _reverseSerial,
                tether,
                _error,
                _updatedAt);
        }
    }

    public string BuildPhoneUrl(string accessCode) =>
        $"http://127.0.0.1:{_options.Port}/listen?code={Uri.EscapeDataString(accessCode)}&wired=1";

    public string? BuildTetherUrl(string accessCode)
    {
        var tether = FindUsbTetherAdapter();
        if (tether is null || !tether.IsUp) return null;
        var ip = tether.Ipv4Addresses.FirstOrDefault()?.ToString();
        if (string.IsNullOrWhiteSpace(ip)) return null;
        return $"http://{ip}:{_options.Port}/listen?code={Uri.EscapeDataString(accessCode)}&wired=1";
    }

    public bool SetAdbPath(string? path)
    {
        var resolved = string.IsNullOrWhiteSpace(path) ? null : path.Trim().Trim('"');
        if (resolved is not null && !File.Exists(resolved)) return false;

        lock (_sync)
        {
            _adbPath = resolved;
            _adbAvailable = resolved is not null;
            _error = null;
            _updatedAt = DateTimeOffset.Now;
        }

        _settingsStore.Update(settings => settings.AdbPath = resolved ?? string.Empty);
        _lastSignature = string.Empty;
        NotifyChanged();
        _ = RefreshAsync();
        return true;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return;

        bool entered;
        try
        {
            entered = await _gate.WaitAsync(0, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!entered) return;

        try
        {
            var path = ResolveAdbPath();
            var available = path is not null;
            var devices = new List<AndroidDeviceInfo>();
            string? error = null;

            if (available)
            {
                var result = await RunAdbAsync(path!, new[] { "devices", "-l" }, cancellationToken);
                if (!result.Ok)
                {
                    error = "adb 无法运行：" + Summarize(result);
                }
                else
                {
                    devices = ParseDevices(result.StdOut);
                    if (devices.Count == 0)
                    {
                        error = "未发现 Android 设备。请插好数据线并开启 USB 调试。";
                    }
                }
            }
            else
            {
                error = "未检测到 adb。可点“选择 adb”，或改用 USB 网络共享。";
            }

            string? previousSerial;
            lock (_sync) previousSerial = _reverseSerial;

            var reverseActive = false;
            string? reverseSerial = null;
            if (available
                && !string.IsNullOrWhiteSpace(previousSerial)
                && devices.Any(device => string.Equals(device.Serial, previousSerial,
                    StringComparison.Ordinal)))
            {
                var check = await RunAdbAsync(path!,
                    new[] { "-s", previousSerial, "reverse", "--list" }, cancellationToken);
                if (check.Ok
                    && check.StdOut.Contains($"tcp:{_options.Port}", StringComparison.Ordinal))
                {
                    reverseActive = true;
                    reverseSerial = previousSerial;
                }
            }

            var tether = FindUsbTetherAdapter();
            var signature = BuildSignature(available, path, devices, reverseActive, reverseSerial,
                tether, error);

            lock (_sync)
            {
                _adbAvailable = available;
                _adbPath = path;
                _devices = devices;
                _reverseActive = reverseActive;
                _reverseSerial = reverseSerial;
                _error = error;
                _updatedAt = DateTimeOffset.Now;
            }

            if (!string.Equals(signature, _lastSignature, StringComparison.Ordinal))
            {
                _lastSignature = signature;
                NotifyChanged();
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown or a newer request.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Android wired refresh failed");
            lock (_sync)
            {
                _error = "检测 Android 设备失败：" + ex.Message;
                _updatedAt = DateTimeOffset.Now;
            }

            NotifyChanged();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(bool Ok, string Message)> EnableReverseAsync(string? serial = null,
        CancellationToken cancellationToken = default)
    {
        if (_disposed) return (false, "服务正在关闭。");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var path = ResolveAdbPath();
            if (path is null)
            {
                return (false, "未检测到 adb。请选择 adb.exe，或改用 USB 网络共享。");
            }

            var (devices, error) = await ReadDevicesAsync(path, cancellationToken);
            if (error is not null) return (false, error);

            AndroidDeviceInfo? target;
            if (!string.IsNullOrWhiteSpace(serial))
            {
                target = devices.FirstOrDefault(device =>
                    string.Equals(device.Serial, serial, StringComparison.Ordinal));
                if (target is null) return (false, "所选 Android 设备已断开，请刷新后重试。");
            }
            else
            {
                var ready = devices.Where(device => device.IsReady).ToList();
                if (ready.Count > 1)
                {
                    return (false, "检测到多台 Android 设备，请先在下拉框中选择一台。");
                }

                target = ready.FirstOrDefault();
            }

            if (target is null)
            {
                return (false, "未发现可用设备。请连接数据线并在手机上允许 USB 调试。");
            }

            if (!target.IsReady)
            {
                return (false, $"{target.Display}：请先在手机上允许 USB 调试。");
            }

            var result = await RunAdbAsync(path,
                new[] { "-s", target.Serial, "reverse", $"tcp:{_options.Port}", $"tcp:{_options.Port}" },
                cancellationToken);
            if (!result.Ok)
            {
                return (false, "建立 USB 通道失败：" + Summarize(result));
            }

            var check = await RunAdbAsync(path,
                new[] { "-s", target.Serial, "reverse", "--list" }, cancellationToken);
            if (!check.Ok
                || !check.StdOut.Contains($"tcp:{_options.Port}", StringComparison.Ordinal))
            {
                return (false, "USB 通道未生效：" + Summarize(check));
            }

            lock (_sync)
            {
                _adbAvailable = true;
                _adbPath = path;
                _devices = devices;
                _reverseActive = true;
                _reverseSerial = target.Serial;
                _error = null;
                _updatedAt = DateTimeOffset.Now;
            }

            NotifyChanged();
            return (true, $"已建立 USB 有线通道：{target.Display}");
        }
        catch (OperationCanceledException)
        {
            return (false, "操作已取消。");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not establish adb reverse");
            return (false, "建立 USB 通道失败：" + ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(bool Ok, string Message)> DisableReverseAsync(
        CancellationToken cancellationToken = default)
    {
        string? serial;
        lock (_sync) serial = _reverseSerial;
        if (serial is null)
        {
            ClearReverseState();
            return (true, "没有活动的 USB 有线通道。");
        }

        var path = ResolveAdbPath();
        if (path is null)
        {
            ClearReverseState();
            return (false, "未检测到 adb，本地通道状态已清除。");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var result = await RunAdbAsync(path,
                new[] { "-s", serial, "reverse", "--remove", $"tcp:{_options.Port}" },
                cancellationToken);
            ClearReverseState();
            return result.Ok
                ? (true, "USB 有线通道已关闭。")
                : (false, "关闭通道时 adb 返回错误：" + Summarize(result));
        }
        catch (OperationCanceledException)
        {
            ClearReverseState();
            return (false, "关闭通道已取消。");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove adb reverse");
            ClearReverseState();
            return (false, "关闭通道失败：" + ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(bool Ok, string Message)> OpenOnPhoneAsync(string url, string? serial = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return (false, "没有可打开的有线地址。");

        var path = ResolveAdbPath();
        if (path is null) return (false, "未检测到 adb，无法远程打开手机浏览器。");

        string? targetSerial = serial;
        lock (_sync) targetSerial ??= _reverseSerial;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (string.IsNullOrWhiteSpace(targetSerial))
            {
                var (devices, _) = await ReadDevicesAsync(path, cancellationToken);
                targetSerial = devices.FirstOrDefault(device => device.IsReady)?.Serial;
            }

            if (string.IsNullOrWhiteSpace(targetSerial))
            {
                return (false, "未发现可用 Android 设备。");
            }

            var escapedUrl = "'" + url.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
            var result = await RunAdbAsync(path,
                new[]
                {
                    "-s", targetSerial, "shell", "am", "start",
                    "-a", "android.intent.action.VIEW", "-d", escapedUrl
                }, cancellationToken);

            var output = (result.StdOut + "\n" + result.StdErr).Trim();
            if (!result.Ok
                || output.Contains("Error:", StringComparison.OrdinalIgnoreCase)
                || output.Contains("Exception", StringComparison.OrdinalIgnoreCase))
            {
                return (false, "无法在手机上打开浏览器：" + Summarize(result));
            }

            return (true, "已在手机上打开有线收听页面。");
        }
        catch (OperationCanceledException)
        {
            return (false, "操作已取消。");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open receiver on phone");
            return (false, "无法在手机上打开浏览器：" + ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await DisableReverseAsync(timeout.Token);
        }
        catch
        {
            // Best effort: the phone will drop the reverse tunnel when unplugged
            // or when the adb server restarts.
        }

        _gate.Dispose();
    }

    private void ClearReverseState()
    {
        var changed = false;
        lock (_sync)
        {
            if (_reverseActive || _reverseSerial is not null)
            {
                _reverseActive = false;
                _reverseSerial = null;
                _updatedAt = DateTimeOffset.Now;
                changed = true;
            }
        }

        if (changed) NotifyChanged();
    }

    private NetworkAdapterSnapshot? FindUsbTetherAdapter()
    {
        return _network.GetSnapshot().Adapters
            .Where(adapter => adapter.IsUsbTether)
            .OrderByDescending(adapter => adapter.HasUsableIpv4)
            .ThenByDescending(adapter => adapter.IsUp)
            .FirstOrDefault();
    }

    private string? ResolveAdbPath()
    {
        lock (_sync)
        {
            if (!string.IsNullOrWhiteSpace(_adbPath) && File.Exists(_adbPath)) return _adbPath;
        }

        string? preferred;
        lock (_sync) preferred = _adbPath;

        var found = FindAdb(preferred)
                    ?? FindAdb(_settingsStore.Load().AdbPath)
                    ?? FindAdb(null);

        lock (_sync)
        {
            if (found is not null) _adbPath = found;
            _adbAvailable = found is not null;
        }

        return found;
    }

    private static string? FindAdb(string? preferred)
    {
        foreach (var candidate in CandidateAdbPaths(preferred))
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateAdbPaths(string? preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred)) yield return preferred.Trim().Trim('"');

        var baseDirectory = AppContext.BaseDirectory;
        yield return Path.Combine(baseDirectory, "platform-tools", "adb.exe");
        yield return Path.Combine(baseDirectory, "adb.exe");

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
        {
            yield return Path.Combine(local, "Android", "Sdk", "platform-tools", "adb.exe");
            yield return Path.Combine(local, "Android", "platform-tools", "adb.exe");
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return Path.Combine(programFiles, "Android", "platform-tools", "adb.exe");
        }

        yield return @"C:\platform-tools\adb.exe";
        yield return @"C:\adb\adb.exe";

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) yield break;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory.Trim().Trim('"'), "adb.exe");
            }
            catch
            {
                continue;
            }

            yield return candidate;
        }
    }

    private static async Task<(List<AndroidDeviceInfo> Devices, string? Error)> ReadDevicesAsync(
        string path, CancellationToken cancellationToken)
    {
        var result = await RunAdbAsync(path, new[] { "devices", "-l" }, cancellationToken);
        if (!result.Ok) return (new List<AndroidDeviceInfo>(), "adb 无法运行：" + Summarize(result));

        var devices = ParseDevices(result.StdOut);
        return devices.Count == 0
            ? (devices, "未发现 Android 设备。请插好数据线并开启 USB 调试。")
            : (devices, null);
    }

    private static List<AndroidDeviceInfo> ParseDevices(string output)
    {
        var devices = new List<AndroidDeviceInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("*", StringComparison.Ordinal)) continue;

            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var serial = parts[0];
            var state = parts[1];
            if (state == "no" && line.Contains("no permissions", StringComparison.OrdinalIgnoreCase))
            {
                state = "no permissions";
            }

            var model = string.Empty;
            foreach (var part in parts)
            {
                if (!part.StartsWith("model:", StringComparison.OrdinalIgnoreCase)) continue;
                model = part[6..].Replace('_', ' ').Trim();
                break;
            }

            if (seen.Add(serial)) devices.Add(new AndroidDeviceInfo(serial, state, model));
        }

        return devices;
    }

    private static async Task<AdbResult> RunAdbAsync(string executable,
        IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory
        };

        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new AdbResult(false, string.Empty, "无法启动 adb。", -1);
            }
        }
        catch (Exception ex)
        {
            return new AdbResult(false, string.Empty, ex.Message, -1);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AdbTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The process may already have exited.
            }

            return new AdbResult(false, await SafeReadAsync(stdoutTask),
                string.IsNullOrWhiteSpace(await SafeReadAsync(stderrTask))
                    ? "adb 超时"
                    : await SafeReadAsync(stderrTask), -1);
        }

        return new AdbResult(process.ExitCode == 0, await SafeReadAsync(stdoutTask),
            await SafeReadAsync(stderrTask), process.ExitCode);
    }

    private static async Task<string> SafeReadAsync(Task<string> task)
    {
        try
        {
            return await task;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Summarize(AdbResult result)
    {
        var text = string.Join(" ",
            new[] { result.StdErr, result.StdOut }
                .Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (text.Length == 0) return $"退出码 {result.ExitCode}";
        if (text.Length > 400) text = text[..400] + "…";
        return text;
    }

    private static string BuildSignature(bool available, string? path,
        IEnumerable<AndroidDeviceInfo> devices, bool reverseActive, string? reverseSerial,
        NetworkAdapterSnapshot? tether, string? error)
    {
        var tetherKey = tether is null
            ? string.Empty
            : tether.Id + ":" + tether.IsUp + ":" +
              string.Join(",", tether.Ipv4Addresses.Select(address => address.ToString()));

        return string.Join("|",
            available,
            path ?? string.Empty,
            reverseActive,
            reverseSerial ?? string.Empty,
            tetherKey,
            error ?? string.Empty,
            string.Join(";", devices.Select(device =>
                device.Serial + ":" + device.State + ":" + device.Model)));
    }

    private void NotifyChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Android wired change handler failed");
        }
    }

    private readonly record struct AdbResult(bool Ok, string StdOut, string StdErr, int ExitCode);
}
