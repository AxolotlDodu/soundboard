using System.Text.Json;

namespace Soundboard.Config;

/// <summary>
/// Charge et sauvegarde les préférences persistées dans settings.json
/// (voir AppPaths.SettingsFile).
/// </summary>
public static class SettingsRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static AppSettings Load()
    {
        if (!File.Exists(AppPaths.SettingsFile))
        {
            return new AppSettings();
        }

        var json = File.ReadAllText(AppPaths.SettingsFile);
        return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        AppPaths.EnsureRootFolderExists();
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(AppPaths.SettingsFile, json);
    }
}