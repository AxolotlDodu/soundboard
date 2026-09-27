namespace Soundboard.Config;

/// <summary>
/// Centralise les chemins de fichiers utilisés par l'application, en restant
/// cohérent entre Windows et Linux (Environment.SpecialFolder.ApplicationData
/// pointe vers %APPDATA% sous Windows et ~/.config sous Linux).
/// </summary>
public static class AppPaths
{
    private static readonly string RootFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Soundboard");

    /// <summary>
    /// Fichier dans lequel la soundboard écrit son port HTTP actif au démarrage.
    /// Le macro pad (ou tout autre client) lit ce fichier pour savoir où envoyer
    /// ses requêtes, ce qui évite tout conflit de port fixe.
    /// </summary>
    public static string PortFile => Path.Combine(RootFolder, "port.txt");

    /// <summary>
    /// Fichier JSON listant les sons configurés (nom, chemin, raccourci logique).
    /// </summary>
    public static string SoundsConfigFile => Path.Combine(RootFolder, "sounds.json");

    /// <summary>
    /// Fichier JSON des préférences persistées : volume général et sorties
    /// audio sélectionnées (voir AppSettings).
    /// </summary>
    public static string SettingsFile => Path.Combine(RootFolder, "settings.json");

    /// <summary>
    /// Dossier dans lequel sont stockées les copies des fichiers audio
    /// importés (voir SoundLibrary.AddFromFile) ainsi que les extraits (trim)
    /// exportés comme nouveau son, au format .wav (voir
    /// AudioEngine.ExportTrimmedFile). Situé au même endroit que sounds.json
    /// et settings.json plutôt que de dépendre d'emplacements choisis par
    /// l'utilisateur dans l'explorateur, qui peuvent être déplacés/supprimés.
    /// </summary>
    public static string SoundsFolder => Path.Combine(RootFolder, "Sounds");

    public static void EnsureRootFolderExists()
    {
        Directory.CreateDirectory(RootFolder);
    }

    public static void EnsureSoundsFolderExists()
    {
        Directory.CreateDirectory(SoundsFolder);
    }
}