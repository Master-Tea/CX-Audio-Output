using System.Runtime.InteropServices;
using System.Text;

namespace CXAudioOutput;

public static class AppPaths
{
    private static readonly Lazy<string> RootLazy = new(DetermineRoot);

    public static string Root => RootLazy.Value;

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string LogsDirectory => Path.Combine(Root, "logs");

    public static string LogFile => Path.Combine(LogsDirectory, "cxaudio.log");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
    }

    private static string DetermineRoot()
    {
        var candidates = new List<string>();

        // A packaged app must use its package-local data folder. The install
        // directory under WindowsApps is read-only, and the package identity
        // can redirect normal LocalAppData lookups.
        foreach (var packagedRoot in GetPackagedRoots())
        {
            if (!string.IsNullOrWhiteSpace(packagedRoot)) candidates.Add(packagedRoot);
        }

        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CXAudioOutput"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "data"));
        candidates.Add(Path.Combine(Path.GetTempPath(), "CXAudioOutput"));

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try
            {
                Directory.CreateDirectory(candidate);
                return candidate;
            }
            catch
            {
                // Try the next writable location.
            }
        }

        return Path.GetTempPath();
    }

    private static List<string> GetPackagedRoots()
    {
        var roots = new List<string>();
        try
        {
            // Full-trust MSIX apps are not UWP AppContainers, so
            // ApplicationData.Current / WinRT Package APIs are not reliable
            // here. Ask the app model directly for the package family name.
            var familyName = GetCurrentPackageFamilyName();
            var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }

            if (!string.IsNullOrWhiteSpace(familyName) && !string.IsNullOrWhiteSpace(localAppData))
            {
                var packageRoot = Path.Combine(localAppData, "Packages", familyName);
                roots.Add(Path.Combine(packageRoot, "LocalState", "CXAudioOutput"));
                roots.Add(Path.Combine(packageRoot, "LocalCache", "Local", "CXAudioOutput"));
                roots.Add(Path.Combine(packageRoot, "LocalCache", "Roaming", "CXAudioOutput"));
            }
        }
        catch
        {
            // Fall through to the normal desktop locations.
        }

        try
        {
            var appData = global::Windows.Storage.ApplicationData.Current.LocalFolder.Path;
            if (!string.IsNullOrWhiteSpace(appData))
            {
                roots.Add(Path.Combine(appData, "CXAudioOutput"));
            }
        }
        catch
        {
            // WinRT ApplicationData is not available in every packaged
            // desktop mode; the package paths above cover those cases.
        }

        return roots;
    }

    private static string? GetCurrentPackageFamilyName()
    {
        try
        {
            uint length = 0;
            var result = GetCurrentPackageFamilyName(ref length, null);
            if (result != ErrorInsufficientBuffer || length == 0) return null;

            var buffer = new StringBuilder((int)length);
            result = GetCurrentPackageFamilyName(ref length, buffer);
            return result == 0 ? buffer.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    private const int ErrorInsufficientBuffer = 122;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetCurrentPackageFamilyName(
        ref uint packageFamilyNameLength,
        StringBuilder? packageFamilyName);
}
