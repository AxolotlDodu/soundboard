using Soundboard.Config;
using Soundboard.Models;

namespace Soundboard.Api;

/// <summary>
/// Détient la liste des sons en mémoire, notifie l'UI des changements et
/// persiste automatiquement dans sounds.json à chaque modification.
/// </summary>
public sealed class SoundLibrary
{
    private readonly List<SoundEntry> _sounds;
    private readonly object _lock = new();

    public event Action? Changed;

    public SoundLibrary(IEnumerable<SoundEntry> initialSounds)
    {
        _sounds = initialSounds.ToList();
    }

    /// <summary>
    /// Charge la bibliothèque depuis sounds.json (ou une liste vide si le
    /// fichier n'existe pas encore, ex. premier lancement de l'appli).
    /// </summary>
    public static SoundLibrary LoadFromDisk() => new(SoundsRepository.Load());

    public IReadOnlyList<SoundEntry> All
    {
        get
        {
            lock (_lock)
            {
                return _sounds.ToList();
            }
        }
    }

    public SoundEntry? FindById(string id)
    {
        lock (_lock)
        {
            return _sounds.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Ajoute un son à partir d'un chemin de fichier choisi par l'utilisateur
    /// (ex. explorateur de fichiers). Le fichier est copié dans
    /// AppPaths.SoundsFolder plutôt que référencé à son emplacement d'origine :
    /// la soundboard reste donc fonctionnelle même si ce fichier source est
    /// ensuite déplacé, renommé ou supprimé par l'utilisateur. L'id est dérivé
    /// du nom de fichier (sans extension) ; en cas de collision (id ou nom de
    /// fichier déjà utilisé), un suffixe numérique est ajouté pour rester unique.
    /// </summary>
    public SoundEntry AddFromFile(string filePath)
    {
        var baseName = Path.GetFileNameWithoutExtension(filePath);
        var displayName = baseName;

        AppPaths.EnsureSoundsFolderExists();
        var storedPath = BuildUniqueStoredPath(baseName, Path.GetExtension(filePath));
        File.Copy(filePath, storedPath);

        lock (_lock)
        {
            var id = ToUniqueId(baseName);
            var entry = new SoundEntry(id, displayName, storedPath);
            _sounds.Add(entry);
            SoundsRepository.Save(_sounds);
            NotifyChanged();
            return entry;
        }
    }

    /// <summary>
    /// Construit un chemin unique dans AppPaths.SoundsFolder à partir d'un nom
    /// de base (slugifié : lettres/chiffres/tirets uniquement), en ajoutant un
    /// suffixe numérique si un fichier du même nom existe déjà.
    /// </summary>
    private static string BuildUniqueStoredPath(string baseName, string extension)
    {
        var slug = string.Concat(baseName.Trim()
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));

        if (string.IsNullOrEmpty(slug))
        {
            slug = "son";
        }

        var candidate = Path.Combine(AppPaths.SoundsFolder, slug + extension);
        var suffix = 1;

        while (File.Exists(candidate))
        {
            candidate = Path.Combine(AppPaths.SoundsFolder, $"{slug}-{suffix++}{extension}");
        }

        return candidate;
    }

    /// <summary>
    /// Ajoute un son pointant vers un fichier déjà présent sur disque, avec
    /// un nom affiché explicite (contrairement à AddFromFile, qui le dérive
    /// du nom de fichier). Utilisé notamment pour l'extrait exporté depuis la
    /// fenêtre de découpage (voir AudioEngine.ExportTrimmedFile) : le fichier
    /// étant déjà le résultat du découpage, TrimStartSeconds/TrimEndSeconds
    /// restent à leurs valeurs par défaut (0 / null).
    /// </summary>
    public SoundEntry AddSound(string filePath, string displayName, double volume = 1.0)
    {
        lock (_lock)
        {
            var id = ToUniqueId(displayName);
            var entry = new SoundEntry(id, displayName, filePath, volume);
            _sounds.Add(entry);
            SoundsRepository.Save(_sounds);
            NotifyChanged();
            return entry;
        }
    }

    public bool Remove(string id)
    {
        lock (_lock)
        {
            var removed = _sounds.RemoveAll(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) > 0;

            if (removed)
            {
                SoundsRepository.Save(_sounds);
                NotifyChanged();
            }

            return removed;
        }
    }

    /// <summary>
    /// Renomme un son : met à jour son nom affiché et régénère son id à
    /// partir de ce nouveau nom (décision : l'id suit toujours le nom
    /// affiché). Attention : si le macro pad référence ce son par son ancien
    /// id, le mapping devra être mis à jour côté macro pad après renommage.
    /// </summary>
    public SoundEntry? Rename(string id, string newDisplayName)
    {
        lock (_lock)
        {
            var index = _sounds.FindIndex(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
            {
                return null;
            }

            var current = _sounds[index];
            var newId = ToUniqueId(newDisplayName, excludeIndex: index);
            var renamed = current with { Id = newId, DisplayName = newDisplayName };

            _sounds[index] = renamed;
            SoundsRepository.Save(_sounds);
            NotifyChanged();
            return renamed;
        }
    }

    /// <summary>
    /// Met à jour le volume d'un son et persiste le changement. Ne déclenche
    /// volontairement pas Changed : cet évènement reconstruit toute la liste
    /// côté UI (RefreshSoundsList), ce qui interromprait le drag du slider en
    /// cours. Le volume affiché reste donc géré directement par le slider
    /// côté UI, indépendamment de ce changement en arrière-plan.
    /// </summary>
    public void UpdateVolume(string id, double volume)
    {
        lock (_lock)
        {
            var index = _sounds.FindIndex(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
            {
                return;
            }

            _sounds[index] = _sounds[index] with { Volume = volume };
            SoundsRepository.Save(_sounds);
        }
    }

    /// <summary>
    /// Met à jour le découpage (trim) d'un son suite à la fenêtre d'édition
    /// dédiée. Contrairement à UpdateVolume, déclenche Changed : il n'y a pas
    /// de drag continu à préserver ici, juste une validation ponctuelle dans
    /// la fenêtre de trim.
    /// </summary>
    public void UpdateTrim(string id, double trimStartSeconds, double? trimEndSeconds)
    {
        lock (_lock)
        {
            var index = _sounds.FindIndex(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
            {
                return;
            }

            _sounds[index] = _sounds[index] with
            {
                TrimStartSeconds = trimStartSeconds,
                TrimEndSeconds = trimEndSeconds,
            };

            SoundsRepository.Save(_sounds);
            NotifyChanged();
        }
    }

    /// <summary>
    /// À appeler après AudioEngine.RewriteTrimmedFile : le fichier sur disque
    /// ne contient désormais plus que l'extrait choisi (le découpage est
    /// "baked in"), donc TrimStartSeconds/TrimEndSeconds reviennent à leurs
    /// valeurs par défaut (0 / null), et FilePath est mis à jour puisque la
    /// réécriture peut avoir changé l'extension (voir RewriteTrimmedFile).
    /// </summary>
    public SoundEntry? ApplyBakedTrim(string id, string newFilePath)
    {
        lock (_lock)
        {
            var index = _sounds.FindIndex(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
            {
                return null;
            }

            var updated = _sounds[index] with
            {
                FilePath = newFilePath,
                TrimStartSeconds = 0.0,
                TrimEndSeconds = null,
            };

            _sounds[index] = updated;
            SoundsRepository.Save(_sounds);
            NotifyChanged();
            return updated;
        }
    }

    private string ToUniqueId(string baseName, int excludeIndex = -1)
    {
        var slug = baseName.Trim().Replace(' ', '-').ToLowerInvariant();
        var candidate = slug;
        var suffix = 1;

        while (_sounds.Where((_, i) => i != excludeIndex)
                      .Any(s => s.Id.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{slug}-{suffix++}";
        }

        return candidate;
    }

    private void NotifyChanged() => Changed?.Invoke();
}