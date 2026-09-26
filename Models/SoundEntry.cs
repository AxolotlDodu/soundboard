namespace Soundboard.Models;

/// <summary>
/// Représente un bruitage configuré : un identifiant stable (utilisé par le
/// macro pad pour cibler l'API), un nom affiché, le chemin du fichier audio,
/// et le volume de lecture (0.0 à 1.0). Le paramètre par défaut de Volume
/// garantit que les entrées existantes de sounds.json (sans ce champ) sont
/// désérialisées à 1.0 sans casser la compatibilité.
/// </summary>
public record SoundEntry(string Id, string DisplayName, string FilePath, double Volume = 1.0);