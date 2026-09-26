using System.Text.Json;
using Soundboard.Models;

namespace Soundboard.Config;

/// <summary>
/// Charge et sauvegarde la liste des sons configurés dans sounds.json
/// (voir AppPaths.SoundsConfigFile).
/// </summary>
public static class SoundsRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static List<SoundEntry> Load()
    {
        if (!File.Exists(AppPaths.SoundsConfigFile))
        {
            return new List<SoundEntry>();
        }

        var json = File.ReadAllText(AppPaths.SoundsConfigFile);
        return JsonSerializer.Deserialize<List<SoundEntry>>(json) ?? new List<SoundEntry>();
    }

    public static void Save(IEnumerable<SoundEntry> sounds)
    {
        AppPaths.EnsureRootFolderExists();
        var json = JsonSerializer.Serialize(sounds.ToList(), JsonOptions);
        File.WriteAllText(AppPaths.SoundsConfigFile, json);
    }
}
