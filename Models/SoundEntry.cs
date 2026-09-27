namespace Soundboard.Models;

/// <summary>
/// Représente un bruitage configuré : un identifiant stable (utilisé par le
/// macro pad pour cibler l'API), un nom affiché, le chemin du fichier audio,
/// le volume de lecture (0.0 à 1.0), et un découpage optionnel (trim) du
/// fichier source. TrimStartSeconds est le point de départ de la lecture ;
/// TrimEndSeconds est le point d'arrêt (null = jusqu'à la fin réelle du
/// fichier). Les valeurs par défaut garantissent que les entrées existantes
/// de sounds.json (sans ces champs) sont désérialisées sans casser la
/// compatibilité.
/// </summary>
public record SoundEntry(
    string Id,
    string DisplayName,
    string FilePath,
    double Volume = 1.0,
    double TrimStartSeconds = 0.0,
    double? TrimEndSeconds = null);