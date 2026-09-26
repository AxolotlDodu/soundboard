namespace Soundboard.Config;

/// <summary>
/// Gère le fichier de découverte de port : la soundboard y écrit le port
/// qu'elle a obtenu de l'OS au démarrage (écoute sur le port 0 = port libre
/// attribué dynamiquement), et n'importe quel client (macro pad) le relit
/// avant chaque appel pour connaître l'URL courante de l'API.
/// </summary>
public static class PortRegistry
{
    public static void WritePort(int port)
    {
        AppPaths.EnsureRootFolderExists();
        File.WriteAllText(AppPaths.PortFile, port.ToString());
    }

    /// <summary>
    /// Côté client (macro pad) : lit le port courant. Retourne null si le
    /// fichier n'existe pas (soundboard jamais lancée) ou est invalide.
    /// </summary>
    public static int? ReadPort()
    {
        if (!File.Exists(AppPaths.PortFile))
        {
            return null;
        }

        var content = File.ReadAllText(AppPaths.PortFile).Trim();
        return int.TryParse(content, out var port) ? port : null;
    }
}
