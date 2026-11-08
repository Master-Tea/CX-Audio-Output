using System.Text.Json;

namespace CXAudioOutput;

public sealed class AppSettings
{
    public List<string> SelectedAdapterIds { get; set; } = new();
    public bool MultiSelect { get; set; } = true;
    public bool HasNetworkSetup { get; set; }
    public string AdbPath { get; set; } = string.Empty;

    public AppSettings Clone() => new()
    {
        SelectedAdapterIds = new List<string>(SelectedAdapterIds),
        MultiSelect = MultiSelect,
        HasNetworkSetup = HasNetworkSetup,
        AdbPath = AdbPath
    };
}

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _sync = new();

    public AppSettingsStore()
    {
        try
        {
            AppPaths.EnsureDirectories();
        }
        catch
        {
            // Settings are optional; the app can still stream without persistence.
        }
    }

    public string FilePath => AppPaths.SettingsFile;

    public AppSettings Load()
    {
        lock (_sync) return LoadNoLock();
    }

    public bool TrySave(AppSettings settings)
    {
        lock (_sync) return SaveNoLock(settings);
    }

    /// <summary>
    /// Read-modify-write helper. Network selection and the adb path are stored
    /// in the same file, so saving one must not discard the other.
    /// </summary>
    public bool Update(Action<AppSettings> mutate)
    {
        lock (_sync)
        {
            var settings = LoadNoLock();
            mutate(settings);
            return SaveNoLock(settings);
        }
    }

    private AppSettings LoadNoLock()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile)) return new AppSettings();
            var json = File.ReadAllText(AppPaths.SettingsFile);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (settings is null) return new AppSettings();
            settings.SelectedAdapterIds = settings.SelectedAdapterIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            settings.AdbPath ??= string.Empty;
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    private bool SaveNoLock(AppSettings settings)
    {
        try
        {
            AppPaths.EnsureDirectories();
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            var temporary = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, AppPaths.SettingsFile, true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
