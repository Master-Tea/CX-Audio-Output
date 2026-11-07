using System.Text.Json;

namespace CXAudioOutput;

public sealed class AppSettings
{
    public List<string> SelectedAdapterIds { get; set; } = new();
    public bool MultiSelect { get; set; } = true;
    public bool HasNetworkSetup { get; set; }

    public AppSettings Clone() => new()
    {
        SelectedAdapterIds = new List<string>(SelectedAdapterIds),
        MultiSelect = MultiSelect,
        HasNetworkSetup = HasNetworkSetup
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
        lock (_sync)
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
                return settings;
            }
            catch
            {
                return new AppSettings();
            }
        }
    }

    public bool TrySave(AppSettings settings)
    {
        lock (_sync)
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
}
