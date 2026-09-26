using ManagedBass;

namespace Soundboard.Audio;

public record AudioDeviceInfo(int Index, string Name, bool IsDefault);

/// <summary>
/// Encapsule ManagedBass : sélection du device de sortie, lecture de sons
/// (avec volume par son), sortie secondaire de test, arrêt manuel ou global
/// des sons en cours, et surveillance périodique du device pour détecter une
/// déconnexion et retomber automatiquement sur le device par défaut
/// (décision : point 5 -> C).
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private const int PollIntervalMs = 2500;

    private int _currentDeviceIndex = -1; // -1 = device par défaut système (convention Bass)
    private int? _secondaryDeviceIndex;
    private bool _secondaryOutputEnabled;
    private readonly HashSet<int> _initializedDevices = new();
    private Timer? _watchTimer;
    private bool _disposed;

    // Suivi des streams en cours par son (id -> (device, stream)), pour
    // pouvoir arrêter un son précis ou tous les sons, et savoir quand un son
    // n'est vraiment plus en train de jouer nulle part (fin naturelle ou stop).
    private readonly Dictionary<string, List<(int DeviceIndex, int Stream)>> _activeStreams = new();
    private readonly Dictionary<int, (string SoundId, int DeviceIndex)> _streamOwners = new();

    // Fichier/volume du son en cours, pour pouvoir le redémarrer sur un
    // device qui vient d'être activé (ex. sortie de test) sans perdre
    // l'info source. Nettoyé quand le son n'a plus aucun stream actif.
    private readonly Dictionary<string, (string FilePath, double Volume)> _activeSoundSources = new();

    private readonly object _streamsLock = new();
    private readonly SyncProcedure _endSyncProc;
    private bool _primaryOutputMuted;

    public event Action<string>? DeviceReconnected;
    public event Action<string>? DeviceLost;

    /// <summary>Levé quand un son démarre (utile pour synchroniser un bouton lecture/arrêt côté UI).</summary>
    public event Action<string>? PlaybackStarted;

    /// <summary>Levé quand un son n'a plus aucun stream en cours (arrêt manuel, StopAll, ou fin naturelle).</summary>
    public event Action<string>? PlaybackEnded;

    public AudioEngine()
    {
        _endSyncProc = OnStreamEnded;
    }

    public void Initialize(int? preferredDeviceIndex = null)
    {
        _currentDeviceIndex = preferredDeviceIndex ?? -1;

        if (!Bass.Init(_currentDeviceIndex))
        {
            throw new InvalidOperationException(
                $"Échec de l'initialisation de Bass sur le device {_currentDeviceIndex} : {Bass.LastError}");
        }

        _initializedDevices.Add(ResolveDeviceIndex(_currentDeviceIndex));
        _watchTimer = new Timer(_ => CheckDeviceHealth(), null, PollIntervalMs, PollIntervalMs);
    }

    public IReadOnlyList<AudioDeviceInfo> GetAvailableDevices()
    {
        var devices = new List<AudioDeviceInfo>();

        for (var i = 0; Bass.GetDeviceInfo(i, out var info); i++)
        {
            if (!info.IsEnabled)
            {
                continue;
            }

            devices.Add(new AudioDeviceInfo(i, info.Name, info.IsDefault));
        }

        return devices;
    }

    public void SelectDevice(int deviceIndex)
    {
        if (!Bass.GetDeviceInfo(deviceIndex, out var info) || !info.IsEnabled)
        {
            throw new ArgumentException($"Device {deviceIndex} invalide ou désactivé.");
        }

        // Bass.Free() démonte le device courant pendant qu'un stream y joue
        // activement (et que son sync BASS_SYNC_END peut encore se déclencher
        // en parallèle sur le thread BASS) : c'est ce qui provoquait le crash
        // natif. On arrête proprement tout ce qui joue avant de libérer/
        // réinitialiser.
        StopAll();

        var freedDeviceIndex = ResolveDeviceIndex(_currentDeviceIndex);

        // On réinitialise Bass sur le nouveau device plutôt que d'appeler
        // uniquement Bass.CurrentDevice, pour forcer une ré-ouverture propre
        // du flux audio (évite des états de device "zombie").
        Bass.Free();
        _initializedDevices.Remove(freedDeviceIndex);

        if (!Bass.Init(deviceIndex))
        {
            throw new InvalidOperationException(
                $"Échec du changement vers le device {deviceIndex} : {Bass.LastError}");
        }

        _currentDeviceIndex = deviceIndex;
        _initializedDevices.Add(deviceIndex);
    }

    /// <summary>
    /// Coupe ou rétablit l'envoi audio de la soundboard vers la sortie
    /// principale. Agit uniquement sur nos propres streams (arrêt des sons en
    /// cours + blocage des futurs dans PlaySound), sans toucher au volume du
    /// device lui-même : ça n'impacte donc pas les autres applications qui
    /// utilisent ce même périphérique.
    /// </summary>
    public void SetPrimaryOutputMuted(bool muted)
    {
        _primaryOutputMuted = muted;

        if (muted)
        {
            StopStreamsOnDevice(ResolveDeviceIndex(_currentDeviceIndex));
        }
    }

    /// <summary>
    /// Active ou désactive la sortie de test, ajoutée pour comparer le rendu
    /// des volumes entre deux sorties audio différentes. À l'activation, tous
    /// les sons actuellement en cours ailleurs sont démarrés sur cette sortie
    /// à la même position, pour qu'ils y soient immédiatement audibles et
    /// synchronisés (plutôt que d'attendre la prochaine lecture).
    /// </summary>
    public void SetSecondaryOutputEnabled(bool enabled)
    {
        _secondaryOutputEnabled = enabled;

        if (!enabled)
        {
            if (_secondaryDeviceIndex.HasValue)
            {
                StopStreamsOnDevice(_secondaryDeviceIndex.Value);
            }

            return;
        }

        if (!_secondaryDeviceIndex.HasValue)
        {
            return;
        }

        var toStart = new List<(string SoundId, string FilePath, double Volume, long PositionBytes)>();

        lock (_streamsLock)
        {
            foreach (var (soundId, streams) in _activeStreams)
            {
                if (!_activeSoundSources.TryGetValue(soundId, out var source))
                {
                    continue;
                }

                var found = false;
                (int DeviceIndex, int Stream) reference = default;

                foreach (var s in streams)
                {
                    if (s.DeviceIndex != _secondaryDeviceIndex.Value)
                    {
                        reference = s;
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    continue;
                }

                var previousDevice = Bass.CurrentDevice;
                Bass.CurrentDevice = reference.DeviceIndex;
                var position = Bass.ChannelGetPosition(reference.Stream);
                Bass.CurrentDevice = previousDevice;

                toStart.Add((soundId, source.FilePath, source.Volume, position));
            }
        }

        foreach (var (soundId, filePath, volume, position) in toStart)
        {
            try
            {
                var stream = CreateStreamOnDevice(_secondaryDeviceIndex.Value, filePath, volume);

                var previousDevice = Bass.CurrentDevice;
                Bass.CurrentDevice = _secondaryDeviceIndex.Value;
                Bass.ChannelSetPosition(stream, position);
                Bass.ChannelPlay(stream);
                Bass.CurrentDevice = previousDevice;

                TrackStream(soundId, _secondaryDeviceIndex.Value, stream);
            }
            catch
            {
                // La sortie de test est un outil de confort pour comparer les
                // volumes : un échec ici ne doit pas remettre en cause la
                // lecture déjà en cours sur la sortie principale.
            }
        }
    }

    /// <summary>
    /// Définit le device utilisé pour la sortie de test et l'initialise
    /// auprès de Bass s'il ne l'est pas déjà. Contrairement au device
    /// principal (SelectDevice, qui libère puis réinitialise), ManagedBass
    /// permet d'avoir plusieurs devices initialisés simultanément : il suffit
    /// de changer Bass.CurrentDevice avant chaque appel qui dépend du device
    /// (CreateStream, ChannelPlay...), voir PlayOnDevice.
    /// </summary>
    public void SetSecondaryDevice(int deviceIndex)
    {
        if (!Bass.GetDeviceInfo(deviceIndex, out var info) || !info.IsEnabled)
        {
            throw new ArgumentException($"Device {deviceIndex} invalide ou désactivé.");
        }

        EnsureDeviceInitialized(deviceIndex);
        _secondaryDeviceIndex = deviceIndex;
    }

    public void PlaySound(string soundId, string filePath, double volume = 1.0)
    {
        var primaryDeviceIndex = ResolveDeviceIndex(_currentDeviceIndex);
        var playedSomewhere = false;

        lock (_streamsLock)
        {
            _activeSoundSources[soundId] = (filePath, volume);
        }

        if (!_primaryOutputMuted)
        {
            var stream = PlayOnDevice(primaryDeviceIndex, filePath, volume);
            TrackStream(soundId, primaryDeviceIndex, stream);
            playedSomewhere = true;
        }

        if (_secondaryOutputEnabled && _secondaryDeviceIndex.HasValue)
        {
            try
            {
                var secondaryStream = PlayOnDevice(_secondaryDeviceIndex.Value, filePath, volume);
                TrackStream(soundId, _secondaryDeviceIndex.Value, secondaryStream);
                playedSomewhere = true;
            }
            catch
            {
                // La sortie de test est un outil de confort pour comparer les
                // volumes : un échec ici ne doit pas empêcher la lecture
                // normale sur la sortie principale.
            }
        }

        if (playedSomewhere)
        {
            PlaybackStarted?.Invoke(soundId);
        }
        else
        {
            lock (_streamsLock)
            {
                _activeSoundSources.Remove(soundId);
            }
        }
    }

    /// <summary>
    /// Change le volume d'un son déjà en cours de lecture, sur tous les
    /// streams actifs de ce son (sortie principale et sortie de test
    /// confondues), sans attendre la prochaine lecture.
    /// </summary>
    public void SetVolume(string soundId, double volume)
    {
        List<(int DeviceIndex, int Stream)> streams;

        lock (_streamsLock)
        {
            streams = _activeStreams.TryGetValue(soundId, out var list)
                ? new List<(int, int)>(list)
                : new List<(int, int)>();
        }

        foreach (var (deviceIndex, stream) in streams)
        {
            var previousDevice = Bass.CurrentDevice;
            Bass.CurrentDevice = deviceIndex;

            try
            {
                Bass.ChannelSetAttribute(stream, ChannelAttribute.Volume, volume);
            }
            finally
            {
                Bass.CurrentDevice = previousDevice;
            }
        }
    }

    /// <summary>
    /// Arrête toutes les instances en cours d'un son précis (principal et
    /// sortie de test confondus).
    /// </summary>
    public void StopSound(string soundId)
    {
        List<(int DeviceIndex, int Stream)> streams;

        lock (_streamsLock)
        {
            streams = _activeStreams.TryGetValue(soundId, out var list)
                ? new List<(int, int)>(list)
                : new List<(int, int)>();
        }

        foreach (var (deviceIndex, stream) in streams)
        {
            StopStream(deviceIndex, stream);
            RemoveStreamAndNotifyIfDone(stream);
        }
    }

    /// <summary>Arrête tous les sons en cours, toutes sorties confondues.</summary>
    public void StopAll()
    {
        List<(int DeviceIndex, int Stream)> streams;

        lock (_streamsLock)
        {
            streams = _streamOwners.Select(kvp => (kvp.Value.DeviceIndex, Stream: kvp.Key)).ToList();
        }

        foreach (var (deviceIndex, stream) in streams)
        {
            StopStream(deviceIndex, stream);
            RemoveStreamAndNotifyIfDone(stream);
        }
    }

    /// <summary>
    /// Arrête toutes les instances en cours sur un device précis (utilisé
    /// pour couper net la sortie principale quand on la désactive, sans
    /// toucher aux streams qui jouent sur la sortie de test).
    /// </summary>
    private void StopStreamsOnDevice(int deviceIndex)
    {
        List<(int DeviceIndex, int Stream)> streams;

        lock (_streamsLock)
        {
            streams = _streamOwners
                .Where(kvp => kvp.Value.DeviceIndex == deviceIndex)
                .Select(kvp => (kvp.Value.DeviceIndex, Stream: kvp.Key))
                .ToList();
        }

        foreach (var (devIdx, stream) in streams)
        {
            StopStream(devIdx, stream);
            RemoveStreamAndNotifyIfDone(stream);
        }
    }

    private int ResolveDeviceIndex(int deviceIndex) =>
        deviceIndex == -1 ? Bass.CurrentDevice : deviceIndex;

    private void EnsureDeviceInitialized(int deviceIndex)
    {
        if (_initializedDevices.Contains(deviceIndex))
        {
            return;
        }

        var previousDevice = Bass.CurrentDevice;

        if (!Bass.Init(deviceIndex))
        {
            throw new InvalidOperationException(
                $"Échec de l'initialisation du device {deviceIndex} : {Bass.LastError}");
        }

        _initializedDevices.Add(deviceIndex);
        Bass.CurrentDevice = previousDevice;
    }

    private int CreateStreamOnDevice(int deviceIndex, string filePath, double volume)
    {
        var previousDevice = Bass.CurrentDevice;
        Bass.CurrentDevice = deviceIndex;

        var stream = Bass.CreateStream(filePath);

        if (stream == 0)
        {
            Bass.CurrentDevice = previousDevice;
            throw new InvalidOperationException(
                $"Impossible de charger le fichier '{filePath}' sur le device {deviceIndex} : {Bass.LastError}");
        }

        Bass.ChannelSetAttribute(stream, ChannelAttribute.Volume, volume);
        Bass.CurrentDevice = previousDevice;
        return stream;
    }

    private int PlayOnDevice(int deviceIndex, string filePath, double volume)
    {
        var stream = CreateStreamOnDevice(deviceIndex, filePath, volume);

        var previousDevice = Bass.CurrentDevice;
        Bass.CurrentDevice = deviceIndex;
        Bass.ChannelPlay(stream);
        Bass.CurrentDevice = previousDevice;
        return stream;
    }

    private void StopStream(int deviceIndex, int stream)
    {
        var previousDevice = Bass.CurrentDevice;
        Bass.CurrentDevice = deviceIndex;

        try
        {
            Bass.ChannelStop(stream);
        }
        catch
        {
            // Le stream a pu être libéré entre-temps (fin naturelle
            // concurrente) ; sans conséquence pour un arrêt manuel.
        }
        finally
        {
            Bass.CurrentDevice = previousDevice;
        }
    }

    private void TrackStream(string soundId, int deviceIndex, int stream)
    {
        lock (_streamsLock)
        {
            if (!_activeStreams.TryGetValue(soundId, out var list))
            {
                list = new List<(int, int)>();
                _activeStreams[soundId] = list;
            }

            list.Add((deviceIndex, stream));
            _streamOwners[stream] = (soundId, deviceIndex);
        }

        // BASS_SYNC_END : détecte la fin naturelle de la lecture (sans
        // polling) pour prévenir l'UI, ex. remettre le bouton ▶ à sa place.
        Bass.ChannelSetSync(stream, SyncFlags.End, 0, _endSyncProc);
    }

    private void OnStreamEnded(int syncHandle, int channel, int data, IntPtr user) =>
        RemoveStreamAndNotifyIfDone(channel);

    private void RemoveStreamAndNotifyIfDone(int stream)
    {
        string? soundId = null;
        var isDone = false;

        lock (_streamsLock)
        {
            if (_streamOwners.TryGetValue(stream, out var owner))
            {
                soundId = owner.SoundId;
                _streamOwners.Remove(stream);

                if (_activeStreams.TryGetValue(soundId, out var list))
                {
                    list.RemoveAll(s => s.Stream == stream);

                    if (list.Count == 0)
                    {
                        _activeStreams.Remove(soundId);
                        _activeSoundSources.Remove(soundId);
                        isDone = true;
                    }
                }
            }
        }

        if (isDone && soundId is not null)
        {
            PlaybackEnded?.Invoke(soundId);
        }
    }

    /// <summary>
    /// Vérifie périodiquement que le device sélectionné est toujours valide.
    /// S'il ne l'est plus (déconnexion physique, changement de périphérique
    /// par défaut Windows/Linux...), on tente de reconnecter automatiquement
    /// sur le device par défaut du système.
    /// </summary>
    private void CheckDeviceHealth()
    {
        if (_disposed)
        {
            return;
        }

        var deviceIndexToCheck = ResolveDeviceIndex(_currentDeviceIndex);
        var isValid = Bass.GetDeviceInfo(deviceIndexToCheck, out var info) && info.IsEnabled;

        if (isValid)
        {
            return;
        }

        DeviceLost?.Invoke($"Device {_currentDeviceIndex} déconnecté, tentative de reconnexion...");

        try
        {
            Bass.Free();

            if (Bass.Init(-1)) // -1 = device par défaut du système
            {
                _currentDeviceIndex = -1;
                Bass.GetDeviceInfo(-1, out var defaultInfo);
                DeviceReconnected?.Invoke(defaultInfo.Name ?? "Device par défaut");
            }
        }
        catch (Exception ex)
        {
            DeviceLost?.Invoke($"Échec de la reconnexion automatique : {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watchTimer?.Dispose();
        Bass.Free(); // Ne libère que le device courant ; un éventuel device de
                     // test resté initialisé n'est pas explicitement libéré ici.
    }
}