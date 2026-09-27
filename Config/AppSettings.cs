namespace Soundboard.Config;

/// <summary>
/// Préférences persistées entre deux lancements : volume général et sorties
/// audio sélectionnées. Les sorties sont identifiées par leur nom (et non par
/// index Bass, qui peut changer d'un lancement à l'autre selon les
/// périphériques connectés/déconnectés).
/// </summary>
public record AppSettings(
    double MasterVolume = 1.0,
    string? PrimaryDeviceName = null,
    bool SecondaryOutputEnabled = false,
    string? SecondaryDeviceName = null);