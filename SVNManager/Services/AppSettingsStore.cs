using System.Text.Json;

namespace SVNManager;

internal static class AppSettingsStore
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SVNManager",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
            Normalize(settings);
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        Normalize(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void Normalize(AppSettings settings)
    {
        settings.Repositories ??= [];
        settings.IgnoredWorkingCopyPaths ??= [];
        settings.FavoriteFileTreePaths ??= [];
        settings.ExpandedFileTreePaths ??= [];
        settings.UiLayout ??= new UiLayoutSettings();
    }
}
