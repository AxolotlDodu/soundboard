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

    public static void EnsureRootFolderExists()
    {
        Directory.CreateDirectory(RootFolder);
    }
}
