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
    /// Ajoute un son à partir d'un chemin de fichier. L'id est dérivé du nom
    /// de fichier (sans extension) ; en cas de collision, un suffixe
    /// numérique est ajouté pour garder des ids uniques.
    /// </summary>
    public SoundEntry AddFromFile(string filePath)
    {
        var baseName = Path.GetFileNameWithoutExtension(filePath);
        var displayName = baseName;

        lock (_lock)
        {
            var id = ToUniqueId(baseName);
            var entry = new SoundEntry(id, displayName, filePath);
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