using ManagedBass;
using Soundboard.Config;

namespace Soundboard.Audio;

public record AudioDeviceInfo(int Index, string Name, bool IsDefault);

/// <summary>
/// Encapsule ManagedBass : sélection du device de sortie, lecture de sons
/// (avec volume par son, volume général, et découpage/trim optionnel),
/// sortie secondaire de test, arrêt manuel ou global des sons en cours, et
/// surveillance périodique du device pour détecter une déconnexion et
/// retomber automatiquement sur le device par défaut (décision : point 5 -> C).
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

    // Volume général appliqué en plus du volume propre à chaque son :
    // volume effectif d'un stream = volume du son * volume général.
    // Persisté par l'appelant (voir AppSettings/SettingsRepository) et
    // exposé via l'API HTTP pour être piloté depuis un encodeur rotatif
    // du macro pad.
    private double _masterVolume = 1.0;

    // Bass.CurrentDevice est un état GLOBAL de la librairie native, partagé
    // entre le thread UI, le timer de surveillance (CheckDeviceHealth) et les
    // callbacks BASS (fin de lecture, fin de trim) qui s'exécutent sur des
    // threads internes à la lib. Toute séquence qui bascule Bass.CurrentDevice
    // le temps d'un appel (création de stream, lecture, arrêt, (ré)init d'un
    // device...) est protégée par ce verrou pour éviter qu'un autre thread ne
    // change le device courant au mauvais moment.
    private readonly object _bassLock = new();

    // Suivi des streams en cours par son (id -> (device, stream)), pour
    // pouvoir arrêter un son précis ou tous les sons, et savoir quand un son
    // n'est vraiment plus en train de jouer nulle part (fin naturelle, fin de
    // trim, ou stop).
    private readonly Dictionary<string, List<(int DeviceIndex, int Stream)>> _activeStreams = new();
    private readonly Dictionary<int, (string SoundId, int DeviceIndex)> _streamOwners = new();

    // Fichier/volume/trim du son en cours, pour pouvoir le redémarrer sur un
    // device qui vient d'être activé (ex. sortie de test) sans perdre l'info
    // source. Nettoyé quand le son n'a plus aucun stream actif.
    private readonly Dictionary<string, (string FilePath, double Volume, double TrimStartSeconds, double? TrimEndSeconds)> _activeSoundSources = new();

    private readonly object _streamsLock = new();
    private readonly SyncProcedure _endSyncProc;
    private readonly SyncProcedure _trimEndSyncProc;
    private bool _primaryOutputMuted;

    public event Action<string>? DeviceReconnected;
    public event Action<string>? DeviceLost;

    /// <summary>Levé quand un son démarre (utile pour synchroniser un bouton lecture/arrêt côté UI).</summary>
    public event Action<string>? PlaybackStarted;

    /// <summary>Levé quand un son n'a plus aucun stream en cours (arrêt manuel, StopAll, fin naturelle ou fin de trim).</summary>
    public event Action<string>? PlaybackEnded;

    public AudioEngine()
    {
        _endSyncProc = OnStreamEnded;
        _trimEndSyncProc = OnTrimEndReached;
    }

    /// <summary>Volume général courant (0.0 à 1.0), tel qu'appliqué à tous les streams actifs.</summary>
    public double MasterVolume => _masterVolume;

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

    /// <summary>
    /// Durée totale d'un fichier audio, en secondes. Utilisé par la fenêtre
    /// de découpage (trim) pour borner les curseurs de début/fin. Ouvre le
    /// fichier en mode "décodage" (pas de device de sortie impliqué, donc pas
    /// besoin de _bassLock ici) puis le libère immédiatement.
    /// </summary>
    public double GetDurationSeconds(string filePath)
    {
        var stream = Bass.CreateStream(filePath, 0, 0, BassFlags.Decode);

        if (stream == 0)
        {
            throw new InvalidOperationException(
                $"Impossible de lire la durée de '{filePath}' : {Bass.LastError}");
        }

        try
        {
            var lengthBytes = Bass.ChannelGetLength(stream);
            return Bass.ChannelBytes2Seconds(stream, lengthBytes);
        }
        finally
        {
            Bass.StreamFree(stream);
        }
    }

    /// <summary>
    /// Calcule une série de pics d'amplitude (0.0 à 1.0), un par "bucket",
    /// pour affichage waveform dans la fenêtre de découpage (trim). Décode le
    /// fichier par blocs successifs (BassFlags.Decode : pas de device de
    /// sortie impliqué, donc pas besoin de _bassLock) puis libère le stream.
    /// </summary>
    public float[] GetWaveformPeaks(string filePath, int bucketCount)
    {
        if (bucketCount <= 0)
        {
            return Array.Empty<float>();
        }

        var stream = Bass.CreateStream(filePath, 0, 0, BassFlags.Decode | BassFlags.Float);

        if (stream == 0)
        {
            throw new InvalidOperationException(
                $"Impossible de générer la waveform de '{filePath}' : {Bass.LastError}");
        }

        try
        {
            Bass.ChannelGetInfo(stream, out var info);
            var channels = Math.Max(1, info.Channels);
            var lengthBytes = Bass.ChannelGetLength(stream);
            var totalSamples = lengthBytes / sizeof(float) / channels;

            if (totalSamples <= 0)
            {
                return new float[bucketCount];
            }

            var samplesPerBucket = Math.Max(1, (int)(totalSamples / bucketCount));
            var bufferLen = samplesPerBucket * channels;
            var buffer = new float[bufferLen];
            var peaks = new List<float>(bucketCount);

            for (var i = 0; i < bucketCount; i++)
            {
                var bytesWanted = bufferLen * sizeof(float);
                var bytesRead = Bass.ChannelGetData(stream, buffer, bytesWanted);

                if (bytesRead <= 0)
                {
                    peaks.Add(0f);
                    continue;
                }

                var floatsRead = bytesRead / sizeof(float);
                var peak = 0f;

                for (var j = 0; j < floatsRead; j++)
                {
                    var abs = Math.Abs(buffer[j]);

                    if (abs > peak)
                    {
                        peak = abs;
                    }
                }

                peaks.Add(Math.Min(peak, 1f));
            }

            return peaks.ToArray();
        }
        finally
        {
            Bass.StreamFree(stream);
        }
    }

    /// <summary>
    /// Décode l'extrait [trimStartSeconds, trimEndSeconds] de sourceFilePath
    /// et l'écrit sur disque comme un nouveau fichier .wav autonome, dans
    /// AppPaths.SoundsFolder (à côté de sounds.json/settings.json). Retourne
    /// le chemin du fichier créé.
    /// </summary>
    public string ExportTrimmedFile(string sourceFilePath, string desiredDisplayName, double trimStartSeconds, double? trimEndSeconds)
    {
        AppPaths.EnsureSoundsFolderExists();

        var outputPath = BuildUniqueSoundPath(desiredDisplayName);
        DecodeTrimToWavFile(sourceFilePath, outputPath, trimStartSeconds, trimEndSeconds);
        return outputPath;
    }

    /// <summary>
    /// Réécrit physiquement filePath pour ne conserver que l'extrait
    /// [trimStartSeconds, trimEndSeconds] : décode vers un fichier temporaire
    /// puis remplace l'original (au lieu de simplement mémoriser des bornes
    /// de trim appliquées à la volée pendant la lecture). Un fichier source
    /// pas déjà en .wav (mp3, ogg, flac...) devient un .wav après réécriture
    /// — ManagedBass sait décoder ces formats mais pas les ré-encoder sans
    /// plugin d'encodage additionnel — et l'ancien fichier est supprimé.
    /// Retourne le chemin final (peut différer de filePath si l'extension a
    /// changé).
    /// </summary>
    public string RewriteTrimmedFile(string filePath, double trimStartSeconds, double? trimEndSeconds)
    {
        var tempPath = filePath + ".trimtmp";
        DecodeTrimToWavFile(filePath, tempPath, trimStartSeconds, trimEndSeconds);

        var finalPath = Path.ChangeExtension(filePath, ".wav");

        if (File.Exists(finalPath))
        {
            File.Delete(finalPath);
        }

        File.Move(tempPath, finalPath);

        if (!string.Equals(filePath, finalPath, StringComparison.OrdinalIgnoreCase) && File.Exists(filePath))
        {
            try
            {
                File.Delete(filePath);
            }
            catch
            {
                // Pas bloquant : l'ancien fichier (mp3/ogg/flac) reste
                // orphelin sur disque, mais la bibliothèque pointera
                // désormais vers le nouveau .wav.
            }
        }

        return finalPath;
    }

    /// <summary>
    /// Décode l'extrait [trimStartSeconds, trimEndSeconds] de sourceFilePath
    /// et l'écrit intégralement dans outputPath au format .wav (PCM 16 bits).
    /// Logique commune à ExportTrimmedFile (nouveau fichier) et
    /// RewriteTrimmedFile (remplacement en place, via un fichier temporaire).
    /// </summary>
    private void DecodeTrimToWavFile(string sourceFilePath, string outputPath, double trimStartSeconds, double? trimEndSeconds)
    {
        var stream = Bass.CreateStream(sourceFilePath, 0, 0, BassFlags.Decode);

        if (stream == 0)
        {
            throw new InvalidOperationException(
                $"Impossible de décoder '{sourceFilePath}' : {Bass.LastError}");
        }

        try
        {
            Bass.ChannelGetInfo(stream, out var info);
            var channels = Math.Max(1, info.Channels);
            var frequency = info.Frequency;

            var totalLengthBytes = Bass.ChannelGetLength(stream);
            var startBytes = Bass.ChannelSeconds2Bytes(stream, trimStartSeconds);
            var endBytes = trimEndSeconds.HasValue
                ? Math.Min(Bass.ChannelSeconds2Bytes(stream, trimEndSeconds.Value), totalLengthBytes)
                : totalLengthBytes;

            Bass.ChannelSetPosition(stream, startBytes);
            var bytesToRead = Math.Max(0, endBytes - startBytes);

            using var outputStream = File.Create(outputPath);
            WriteWavHeaderPlaceholder(outputStream);

            var buffer = new byte[65536];
            long bytesWritten = 0;

            while (bytesWritten < bytesToRead)
            {
                var chunkSize = (int)Math.Min(buffer.Length, bytesToRead - bytesWritten);
                var bytesRead = Bass.ChannelGetData(stream, buffer, chunkSize);

                if (bytesRead <= 0)
                {
                    break;
                }

                outputStream.Write(buffer, 0, bytesRead);
                bytesWritten += bytesRead;
            }

            // 2 octets/échantillon : Bass.CreateStream sans BassFlags.Float
            // décode par défaut en PCM 16 bits, comme les autres flux créés
            // ailleurs dans cette classe (CreateStreamOnDevice, GetDurationSeconds).
            FinalizeWavHeader(outputStream, bytesWritten, channels, frequency, bytesPerSample: 2);
        }
        catch
        {
            // Écriture interrompue (fichier source corrompu en cours de
            // lecture, disque plein...) : on ne laisse pas un .wav à moitié
            // écrit derrière.
            try
            {
                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }
            }
            catch
            {
                // Rien de plus à faire si même la suppression échoue.
            }

            throw;
        }
        finally
        {
            Bass.StreamFree(stream);
        }
    }

    private static string BuildUniqueSoundPath(string desiredDisplayName)
    {
        var slug = string.Concat(desiredDisplayName.Trim()
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));

        if (string.IsNullOrEmpty(slug))
        {
            slug = "extrait";
        }

        var candidate = Path.Combine(AppPaths.SoundsFolder, slug + ".wav");
        var suffix = 1;

        while (File.Exists(candidate))
        {
            candidate = Path.Combine(AppPaths.SoundsFolder, $"{slug}-{suffix++}.wav");
        }

        return candidate;
    }

    private static void WriteWavHeaderPlaceholder(Stream stream)
    {
        // 44 octets réservés pour l'en-tête WAV standard (RIFF/fmt/data),
        // rempli avec les vraies valeurs une fois la taille des données connue
        // (voir FinalizeWavHeader).
        stream.Write(new byte[44], 0, 44);
    }

    private static void FinalizeWavHeader(FileStream stream, long dataBytes, int channels, int sampleRate, int bytesPerSample)
    {
        stream.Seek(0, SeekOrigin.Begin);

        var byteRate = sampleRate * channels * bytesPerSample;
        var blockAlign = (short)(channels * bytesPerSample);
        var bitsPerSample = (short)(bytesPerSample * 8);
        var riffChunkSize = (uint)(36 + dataBytes);

        using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(riffChunkSize);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write((uint)16);
        writer.Write((short)1); // PCM
        writer.Write((short)channels);
        writer.Write((uint)sampleRate);
        writer.Write((uint)byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write((uint)dataBytes);
    }

    public void SelectDevice(int deviceIndex)
    {
        if (!Bass.GetDeviceInfo(deviceIndex, out var info) || !info.IsEnabled)
        {
            throw new ArgumentException($"Device {deviceIndex} invalide ou désactivé.");
        }

        if (_secondaryDeviceIndex.HasValue && _secondaryDeviceIndex.Value == deviceIndex)
        {
            // Utiliser le même device physique pour la sortie principale et la
            // sortie de test fait planter/geler l'appli : les deux "rôles"
            // s'appuient tous les deux sur Bass.CurrentDevice (état global) et
            // sur Bass.Free()/Bass.Init() pour ce même device, ce qui les fait
            // se marcher dessus. On refuse explicitement plutôt que de laisser
            // l'appli se figer.
            throw new InvalidOperationException(
                "Ce device est déjà utilisé comme sortie de test. Choisis un device différent pour la sortie principale, ou désactive d'abord la sortie de test.");
        }

        // Bass.Free() démonte le device courant pendant qu'un stream y joue
        // activement (et que son sync BASS_SYNC_END peut encore se déclencher
        // en parallèle sur le thread BASS) : c'est ce qui provoquait le crash
        // natif. On arrête proprement tout ce qui joue avant de libérer/
        // réinitialiser.
        StopAll();

        lock (_bassLock)
        {
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

        var toStart = new List<(string SoundId, string FilePath, double Volume, double? TrimEndSeconds, long PositionBytes)>();

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

                long position;

                lock (_bassLock)
                {
                    var previousDevice = Bass.CurrentDevice;
                    Bass.CurrentDevice = reference.DeviceIndex;
                    position = Bass.ChannelGetPosition(reference.Stream);
                    Bass.CurrentDevice = previousDevice;
                }

                toStart.Add((soundId, source.FilePath, source.Volume, source.TrimEndSeconds, position));
            }
        }

        foreach (var (soundId, filePath, volume, trimEndSeconds, position) in toStart)
        {
            try
            {
                // trimStart n'est pas ré-appliqué ici : on reprend à la position
                // déjà atteinte sur la sortie principale (position), qui est
                // forcée juste après. trimEndSeconds reste en revanche
                // nécessaire pour que cette copie s'arrête au bon endroit.
                var stream = CreateStreamOnDevice(_secondaryDeviceIndex.Value, filePath, volume * _masterVolume, 0.0, trimEndSeconds);

                lock (_bassLock)
                {
                    var previousDevice = Bass.CurrentDevice;
                    Bass.CurrentDevice = _secondaryDeviceIndex.Value;
                    Bass.ChannelSetPosition(stream, position);
                    Bass.ChannelPlay(stream);
                    Bass.CurrentDevice = previousDevice;
                }

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

        if (ResolveDeviceIndex(_currentDeviceIndex) == deviceIndex)
        {
            // Même raison que dans SelectDevice : un seul et même device
            // physique ne peut pas tenir les deux rôles à la fois sans geler
            // l'appli.
            throw new InvalidOperationException(
                "Ce device est déjà utilisé comme sortie principale. Choisis un device différent pour la sortie de test.");
        }

        EnsureDeviceInitialized(deviceIndex);
        _secondaryDeviceIndex = deviceIndex;
    }

    public void PlaySound(string soundId, string filePath, double volume = 1.0, double trimStartSeconds = 0.0, double? trimEndSeconds = null)
    {
        var primaryDeviceIndex = ResolveDeviceIndex(_currentDeviceIndex);
        var playedSomewhere = false;

        lock (_streamsLock)
        {
            _activeSoundSources[soundId] = (filePath, volume, trimStartSeconds, trimEndSeconds);
        }

        if (!_primaryOutputMuted)
        {
            var stream = PlayOnDevice(primaryDeviceIndex, filePath, volume * _masterVolume, trimStartSeconds, trimEndSeconds);
            TrackStream(soundId, primaryDeviceIndex, stream);
            playedSomewhere = true;
        }

        if (_secondaryOutputEnabled && _secondaryDeviceIndex.HasValue)
        {
            try
            {
                var secondaryStream = PlayOnDevice(_secondaryDeviceIndex.Value, filePath, volume * _masterVolume, trimStartSeconds, trimEndSeconds);
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
    /// confondues), sans attendre la prochaine lecture. Le volume effectif
    /// appliqué tient compte du volume général (volume du son * volume
    /// général) ; le volume "brut" du son est mémorisé dans
    /// _activeSoundSources pour que SetMasterVolume puisse le retrouver.
    /// </summary>
    public void SetVolume(string soundId, double volume)
    {
        List<(int DeviceIndex, int Stream)> streams;

        lock (_streamsLock)
        {
            if (_activeSoundSources.TryGetValue(soundId, out var source))
            {
                _activeSoundSources[soundId] = source with { Volume = volume };
            }

            streams = _activeStreams.TryGetValue(soundId, out var list)
                ? new List<(int, int)>(list)
                : new List<(int, int)>();
        }

        foreach (var (deviceIndex, stream) in streams)
        {
            lock (_bassLock)
            {
                var previousDevice = Bass.CurrentDevice;
                Bass.CurrentDevice = deviceIndex;

                try
                {
                    Bass.ChannelSetAttribute(stream, ChannelAttribute.Volume, volume * _masterVolume);
                }
                finally
                {
                    Bass.CurrentDevice = previousDevice;
                }
            }
        }
    }

    /// <summary>
    /// Change le volume général de la soundboard (0.0 à 1.0, hors bornes
    /// ramené dans l'intervalle) et recalcule immédiatement le volume
    /// effectif de tous les streams actifs, sur toutes les sorties, à partir
    /// du volume propre de chaque son mémorisé dans _activeSoundSources.
    /// Pensé pour être piloté depuis un encodeur rotatif via l'API HTTP.
    /// </summary>
    public void SetMasterVolume(double volume)
    {
        _masterVolume = Math.Clamp(volume, 0.0, 1.0);

        List<(string SoundId, int DeviceIndex, int Stream)> streams;

        lock (_streamsLock)
        {
            streams = _streamOwners
                .Select(kvp => (kvp.Value.SoundId, kvp.Value.DeviceIndex, Stream: kvp.Key))
                .ToList();
        }

        foreach (var (soundId, deviceIndex, stream) in streams)
        {
            double perSoundVolume;

            lock (_streamsLock)
            {
                perSoundVolume = _activeSoundSources.TryGetValue(soundId, out var source) ? source.Volume : 1.0;
            }

            lock (_bassLock)
            {
                var previousDevice = Bass.CurrentDevice;
                Bass.CurrentDevice = deviceIndex;

                try
                {
                    Bass.ChannelSetAttribute(stream, ChannelAttribute.Volume, perSoundVolume * _masterVolume);
                }
                finally
                {
                    Bass.CurrentDevice = previousDevice;
                }
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

        lock (_bassLock)
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
    }

    /// <summary>
    /// Crée un stream pour filePath sur deviceIndex, avec le volume donné, et
    /// applique le découpage (trim) demandé : seek au point de départ, et
    /// pose d'un sync de position au point de fin (voir OnTrimEndReached) qui
    /// coupera le son à cet endroit plutôt qu'à la fin réelle du fichier.
    /// </summary>
    private int CreateStreamOnDevice(int deviceIndex, string filePath, double volume, double trimStartSeconds = 0.0, double? trimEndSeconds = null)
    {
        lock (_bassLock)
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

            if (trimStartSeconds > 0)
            {
                var startBytes = Bass.ChannelSeconds2Bytes(stream, trimStartSeconds);
                Bass.ChannelSetPosition(stream, startBytes);
            }

            if (trimEndSeconds.HasValue)
            {
                var endBytes = Bass.ChannelSeconds2Bytes(stream, trimEndSeconds.Value);
                Bass.ChannelSetSync(stream, SyncFlags.Position, endBytes, _trimEndSyncProc);
            }

            Bass.ChannelSetAttribute(stream, ChannelAttribute.Volume, volume);
            Bass.CurrentDevice = previousDevice;
            return stream;
        }
    }

    private int PlayOnDevice(int deviceIndex, string filePath, double volume, double trimStartSeconds = 0.0, double? trimEndSeconds = null)
    {
        var stream = CreateStreamOnDevice(deviceIndex, filePath, volume, trimStartSeconds, trimEndSeconds);

        lock (_bassLock)
        {
            var previousDevice = Bass.CurrentDevice;
            Bass.CurrentDevice = deviceIndex;
            Bass.ChannelPlay(stream);
            Bass.CurrentDevice = previousDevice;
        }

        return stream;
    }

    private void StopStream(int deviceIndex, int stream)
    {
        lock (_bassLock)
        {
            var previousDevice = Bass.CurrentDevice;
            Bass.CurrentDevice = deviceIndex;

            try
            {
                Bass.ChannelStop(stream);
            }
            catch
            {
                // Le stream a pu être libéré entre-temps (fin naturelle ou fin
                // de trim concurrente) ; sans conséquence pour un arrêt manuel.
            }
            finally
            {
                Bass.CurrentDevice = previousDevice;
            }
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
        // Le sync de fin de trim (BASS_SYNC_POS), lui, est déjà posé dans
        // CreateStreamOnDevice au moment de la création du stream.
        lock (_bassLock)
        {
            Bass.ChannelSetSync(stream, SyncFlags.End, 0, _endSyncProc);
        }
    }

    private void OnStreamEnded(int syncHandle, int channel, int data, IntPtr user) =>
        RemoveStreamAndNotifyIfDone(channel);

    /// <summary>
    /// Callback du sync de fin de trim (BASS_SYNC_POS) : arrête explicitement
    /// le canal (BASS_SYNC_POS ne le fait pas tout seul, contrairement à
    /// BASS_SYNC_END) puis nettoie le suivi comme pour une fin naturelle.
    /// </summary>
    private void OnTrimEndReached(int syncHandle, int channel, int data, IntPtr user)
    {
        try
        {
            Bass.ChannelStop(channel);
        }
        catch
        {
            // Idem StopStream : le canal a pu se terminer naturellement entre
            // le déclenchement du sync et cet appel.
        }

        RemoveStreamAndNotifyIfDone(channel);
    }

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
            lock (_bassLock)
            {
                Bass.Free();

                if (Bass.Init(-1)) // -1 = device par défaut du système
                {
                    _currentDeviceIndex = -1;
                    Bass.GetDeviceInfo(-1, out var defaultInfo);
                    DeviceReconnected?.Invoke(defaultInfo.Name ?? "Device par défaut");
                }
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