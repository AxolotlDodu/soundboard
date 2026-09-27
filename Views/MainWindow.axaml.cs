using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Soundboard.Api;
using Soundboard.Audio;
using Soundboard.Config;
using Soundboard.Models;

namespace Soundboard.Views;

public partial class MainWindow : Window
{
    private readonly AudioEngine? _audioEngine;
    private readonly SoundLibrary? _soundLibrary;
    private AppSettings _settings;

    // Dernière sélection valide de chaque sortie, pour pouvoir revenir en
    // arrière si l'utilisateur choisit un device déjà utilisé par l'autre
    // sortie (refusé par AudioEngine, voir ShowOutputWarning).
    private AudioDeviceInfo? _lastValidPrimaryDevice;
    private AudioDeviceInfo? _lastValidSecondaryDevice;

    // Ids des sons cochés via la case de sélection multiple. Conservé côté
    // code-behind (et non sur SoundEntry, qui est immuable) puisque la liste
    // est reconstruite à chaque Changed de la bibliothèque.
    private readonly HashSet<string> _selectedSoundIds = new(StringComparer.OrdinalIgnoreCase);

    // Ids des sons actuellement en cours de lecture, tenu à jour via les
    // évènements PlaybackStarted/PlaybackEnded de l'AudioEngine (source de
    // vérité unique : couvre aussi bien un clic manuel qu'une fin naturelle,
    // une fin de trim, ou un arrêt déclenché depuis l'API du macro pad).
    private readonly HashSet<string> _playingSoundIds = new(StringComparer.OrdinalIgnoreCase);

    // SoundsListBox, RemoveSoundButton, AddSoundButton, DeviceComboBox,
    // SecondaryOutputToggle, SecondaryDeviceComboBox, SelectAllCheckBox,
    // MasterVolumeSlider, MasterVolumeLabel et OutputWarningText sont générés
    // automatiquement comme champs par Avalonia.Generators à partir de leur
    // x:Name dans le XAML ; pas besoin de FindControl ici.

    // Constructeur sans paramètre requis par le designer XAML d'Avalonia.
    public MainWindow() : this(null, null, null)
    {
    }

    public MainWindow(AudioEngine? audioEngine, SoundLibrary? soundLibrary, AppSettings? settings)
    {
        _audioEngine = audioEngine;
        _soundLibrary = soundLibrary;
        _settings = settings ?? new AppSettings();
        InitializeComponent();

        SetupDeviceSelector();
        SetupSecondaryOutputSelector();
        SetupSoundsList();
        SetupPlaybackTracking();
        SetupMasterVolume();
    }

    /// <summary>
    /// Fusionne les champs fournis dans les préférences en mémoire et les
    /// persiste immédiatement dans settings.json.
    /// </summary>
    private void SaveSettings(
        double? masterVolume = null,
        string? primaryDeviceName = null,
        bool? secondaryEnabled = null,
        string? secondaryDeviceName = null)
    {
        _settings = _settings with
        {
            MasterVolume = masterVolume ?? _settings.MasterVolume,
            PrimaryDeviceName = primaryDeviceName ?? _settings.PrimaryDeviceName,
            SecondaryOutputEnabled = secondaryEnabled ?? _settings.SecondaryOutputEnabled,
            SecondaryDeviceName = secondaryDeviceName ?? _settings.SecondaryDeviceName,
        };

        SettingsRepository.Save(_settings);
    }

    private void ShowOutputWarning(string message)
    {
        OutputWarningText.Text = message;
        OutputWarningText.IsVisible = true;
    }

    private void HideOutputWarning()
    {
        OutputWarningText.IsVisible = false;
    }

    /// <summary>
    /// Initialise le slider de volume général à la valeur persistée (déjà
    /// appliquée au moteur audio par Program.cs au démarrage) et branche sa
    /// sauvegarde à chaque changement.
    /// </summary>
    private void SetupMasterVolume()
    {
        if (_audioEngine is null)
        {
            return;
        }

        MasterVolumeSlider.Value = _settings.MasterVolume;
        MasterVolumeLabel.Text = $"{_settings.MasterVolume:P0}";

        MasterVolumeSlider.ValueChanged += (_, e) =>
        {
            _audioEngine.SetMasterVolume(e.NewValue);
            MasterVolumeLabel.Text = $"{e.NewValue:P0}";
            SaveSettings(masterVolume: e.NewValue);
        };
    }

    private void SetupPlaybackTracking()
    {
        if (_audioEngine is null)
        {
            return;
        }

        _audioEngine.PlaybackStarted += soundId => Dispatcher.UIThread.Post(() =>
        {
            _playingSoundIds.Add(soundId);
            UpdatePlayButtonVisual(soundId, isPlaying: true);
        });

        _audioEngine.PlaybackEnded += soundId => Dispatcher.UIThread.Post(() =>
        {
            _playingSoundIds.Remove(soundId);
            UpdatePlayButtonVisual(soundId, isPlaying: false);
        });

        StopAllButton.Click += (_, _) => _audioEngine.StopAll();
    }

    /// <summary>
    /// Retrouve le bouton ▶/⏹ correspondant à un son parmi les lignes
    /// actuellement réalisées de la liste, et met à jour son apparence.
    /// Nécessaire car les évènements de lecture (démarrage, fin naturelle,
    /// fin de trim, arrêt via l'API du macro pad) ne passent pas par un clic
    /// sur ce bouton précis.
    /// </summary>
    private void UpdatePlayButtonVisual(string soundId, bool isPlaying)
    {
        var button = SoundsListBox.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(b => b.Classes.Contains("play") &&
                                  b.Tag is SoundEntry tagged &&
                                  tagged.Id.Equals(soundId, StringComparison.OrdinalIgnoreCase));

        if (button is null)
        {
            return;
        }

        button.Content = isPlaying ? "⏹" : "▶";

        if (isPlaying)
        {
            button.Classes.Add("playing");
        }
        else
        {
            button.Classes.Remove("playing");
        }
    }

    private void SetupDeviceSelector()
    {
        if (_audioEngine is null)
        {
            return;
        }

        var devices = _audioEngine.GetAvailableDevices();

        DeviceComboBox.ItemsSource = devices;

        // Si un nom de device a été persisté, on essaie de le retrouver ; s'il
        // n'est plus branché, on vide volontairement la sélection (au lieu de
        // retomber sur le device par défaut) pour signaler visuellement que
        // la sortie configurée n'est plus disponible.
        DeviceComboBox.SelectedItem = _settings.PrimaryDeviceName is null
            ? devices.FirstOrDefault(d => d.IsDefault)
            : devices.FirstOrDefault(d => d.Name == _settings.PrimaryDeviceName);

        _lastValidPrimaryDevice = DeviceComboBox.SelectedItem as AudioDeviceInfo;

        DeviceComboBox.SelectionChanged += (_, _) =>
        {
            if (DeviceComboBox.SelectedItem is not AudioDeviceInfo selected)
            {
                return;
            }

            try
            {
                _audioEngine.SelectDevice(selected.Index);
                _lastValidPrimaryDevice = selected;
                SaveSettings(primaryDeviceName: selected.Name);
                HideOutputWarning();
            }
            catch (InvalidOperationException ex)
            {
                // Ex. même device déjà choisi pour la sortie de test : on
                // revient à la sélection précédente plutôt que de laisser
                // l'appli dans un état qui la fait geler.
                ShowOutputWarning(ex.Message);
                DeviceComboBox.SelectedItem = _lastValidPrimaryDevice;
            }
        };

        PrimaryOutputToggle.IsCheckedChanged += (_, _) =>
        {
            var isOn = PrimaryOutputToggle.IsChecked == true;
            PrimaryOutputToggle.Content = isOn ? "On" : "Off";
            _audioEngine.SetPrimaryOutputMuted(!isOn);
        };
    }

    private void SetupSecondaryOutputSelector()
    {
        if (_audioEngine is null)
        {
            return;
        }

        // Même liste de devices que la sortie principale : l'idée est de
        // pouvoir brancher un 2e device (casque, sortie physique...) juste
        // pour comparer à l'oreille le rendu des volumes réglés par son.
        var devices = _audioEngine.GetAvailableDevices();
        SecondaryDeviceComboBox.ItemsSource = devices;

        // Comme pour la sortie principale : si le device persisté n'est plus
        // branché, on laisse la sélection vide plutôt que de deviner un
        // remplaçant, et la sortie de test reste désactivée dans ce cas.
        var persistedDevice = _settings.SecondaryDeviceName is null
            ? null
            : devices.FirstOrDefault(d => d.Name == _settings.SecondaryDeviceName);

        SecondaryDeviceComboBox.SelectedItem = persistedDevice;
        _lastValidSecondaryDevice = persistedDevice;

        if (persistedDevice is not null && _settings.SecondaryOutputEnabled)
        {
            SecondaryOutputToggle.IsChecked = true;
            SecondaryOutputToggle.Content = "On";
            SecondaryDeviceComboBox.IsEnabled = true;
        }

        SecondaryOutputToggle.IsCheckedChanged += (_, _) =>
        {
            var enabled = SecondaryOutputToggle.IsChecked == true;

            if (enabled && SecondaryDeviceComboBox.SelectedItem is AudioDeviceInfo selectedForEnable)
            {
                try
                {
                    _audioEngine.SetSecondaryDevice(selectedForEnable.Index);
                    _lastValidSecondaryDevice = selectedForEnable;
                    HideOutputWarning();
                }
                catch (InvalidOperationException ex)
                {
                    // Refuse l'activation : on repasse le toggle à Off, ce qui
                    // redéclenche ce même gestionnaire avec enabled=false et
                    // termine proprement la mise à jour visuelle ci-dessous.
                    ShowOutputWarning(ex.Message);
                    SecondaryOutputToggle.IsChecked = false;
                    return;
                }
            }

            SecondaryOutputToggle.Content = enabled ? "On" : "Off";
            SecondaryDeviceComboBox.IsEnabled = enabled;
            _audioEngine.SetSecondaryOutputEnabled(enabled);
            SaveSettings(secondaryEnabled: enabled);
        };

        SecondaryDeviceComboBox.SelectionChanged += (_, _) =>
        {
            if (SecondaryDeviceComboBox.SelectedItem is not AudioDeviceInfo selected)
            {
                return;
            }

            try
            {
                _audioEngine.SetSecondaryDevice(selected.Index);
                _lastValidSecondaryDevice = selected;
                SaveSettings(secondaryDeviceName: selected.Name);
                HideOutputWarning();
            }
            catch (InvalidOperationException ex)
            {
                ShowOutputWarning(ex.Message);
                SecondaryDeviceComboBox.SelectedItem = _lastValidSecondaryDevice;
            }
        };
    }

    private void SetupSoundsList()
    {
        if (_soundLibrary is null)
        {
            return;
        }

        RefreshSoundsList();
        _soundLibrary.Changed += RefreshSoundsList;

        SoundsListBox.SelectionChanged += (_, _) => UpdateRemoveButtonState();

        SelectAllCheckBox.IsCheckedChanged += (_, _) =>
        {
            if (SelectAllCheckBox.IsChecked == true)
            {
                foreach (var sound in _soundLibrary.All)
                {
                    _selectedSoundIds.Add(sound.Id);
                }
            }
            else
            {
                _selectedSoundIds.Clear();
            }

            RefreshSoundsList();
            UpdateRemoveButtonState();
        };

        var addButton = AddSoundButton;
        addButton.Click += OnAddSoundClicked;

        RemoveSoundButton.Click += OnRemoveSoundClicked;
    }

    private void RefreshSoundsList()
    {
        // Changed peut être levé depuis n'importe quel contexte ; on revient
        // sur le thread UI pour manipuler les contrôles Avalonia.
        Dispatcher.UIThread.Post(() =>
        {
            SoundsListBox.ItemsSource = _soundLibrary!.All;
        });
    }

    private void UpdateRemoveButtonState()
    {
        RemoveSoundButton.IsEnabled = _selectedSoundIds.Count > 0 || SoundsListBox.SelectedItem is not null;
    }

    private async void OnAddSoundClicked(object? sender, RoutedEventArgs e)
    {
        if (_soundLibrary is null)
        {
            return;
        }

        var topLevel = GetTopLevel(this);

        if (topLevel is null)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choisir un fichier audio",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Fichiers audio")
                {
                    Patterns = new[] { "*.mp3", "*.wav", "*.ogg", "*.flac" },
                },
            },
        });

        foreach (var file in files)
        {
            var localPath = file.TryGetLocalPath();

            if (localPath is null)
            {
                continue;
            }

            try
            {
                _soundLibrary.AddFromFile(localPath);
            }
            catch
            {
                // La copie vers AppPaths.SoundsFolder peut échouer (disque
                // plein, permissions...) : on continue avec les fichiers
                // suivants plutôt que d'interrompre tout l'import.
            }
        }
    }

    private void OnRemoveSoundClicked(object? sender, RoutedEventArgs e)
    {
        if (_soundLibrary is null)
        {
            return;
        }

        // Priorité à la sélection multiple (cases cochées) si elle est utilisée ;
        // sinon on retombe sur la sélection simple de la ListBox.
        if (_selectedSoundIds.Count > 0)
        {
            foreach (var id in _selectedSoundIds.ToList())
            {
                _soundLibrary.Remove(id);
            }

            _selectedSoundIds.Clear();
            SelectAllCheckBox.IsChecked = false;
            UpdateRemoveButtonState();
            return;
        }

        if (SoundsListBox.SelectedItem is SoundEntry selected)
        {
            _soundLibrary.Remove(selected.Id);
        }
    }

    private void OnPlaySoundClicked(object? sender, RoutedEventArgs e)
    {
        if (_audioEngine is null || sender is not Button { Tag: SoundEntry sound })
        {
            return;
        }

        // Le bouton fait office de bascule : s'il est déjà en cours de
        // lecture, un nouveau clic l'arrête au lieu de le relancer.
        if (_playingSoundIds.Contains(sound.Id))
        {
            _audioEngine.StopSound(sound.Id);
            return;
        }

        // SoundEntry est un record immuable : le Tag du bouton a été capturé
        // au moment du binding et ne reflète pas un changement de volume ou
        // de trim fait depuis (slider, fenêtre de trim). On relit donc
        // l'entrée à jour dans la bibliothèque avant de jouer le son.
        var current = _soundLibrary?.FindById(sound.Id) ?? sound;

        try
        {
            _audioEngine.PlaySound(current.Id, current.FilePath, current.Volume, current.TrimStartSeconds, current.TrimEndSeconds);
        }
        catch
        {
            // La lecture manuelle depuis l'UI échoue silencieusement ici ;
            // un retour visuel (ex. bordure rouge temporaire) pourra être
            // ajouté plus tard si besoin.
        }
    }

    private void OnVolumeChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_soundLibrary is null || sender is not Slider { Tag: SoundEntry sound } slider)
        {
            return;
        }

        _soundLibrary.UpdateVolume(sound.Id, e.NewValue);
        _audioEngine?.SetVolume(sound.Id, e.NewValue);

        // Met à jour le libellé de pourcentage juste à côté du slider, sans
        // reconstruire toute la liste (voir le commentaire sur UpdateVolume).
        if (slider.Parent is Panel row)
        {
            var label = row.Children.OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("volume-label"));

            if (label is not null)
            {
                label.Text = $"{e.NewValue:P0}";
            }
        }
    }

    private void OnSoundCheckBoxLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: SoundEntry sound } checkBox)
        {
            checkBox.IsChecked = _selectedSoundIds.Contains(sound.Id);
        }
    }

    private void OnSoundCheckBoxCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: SoundEntry sound } checkBox)
        {
            return;
        }

        if (checkBox.IsChecked == true)
        {
            _selectedSoundIds.Add(sound.Id);
        }
        else
        {
            _selectedSoundIds.Remove(sound.Id);
        }

        UpdateRemoveButtonState();
    }

    private void OnRenameClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SoundEntry } button || button.Parent is not Grid row)
        {
            return;
        }

        if (row.Children.OfType<Grid>().FirstOrDefault() is not Grid nameHost)
        {
            return;
        }

        var display = nameHost.Children.OfType<TextBlock>().FirstOrDefault();
        var editor = nameHost.Children.OfType<TextBox>().FirstOrDefault();

        if (display is null || editor is null || button.Tag is not SoundEntry sound)
        {
            return;
        }

        editor.Text = sound.DisplayName;
        display.IsVisible = false;
        editor.IsVisible = true;
        editor.Focus();
        editor.SelectAll();
    }

    private void OnRenameTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { Tag: SoundEntry sound } editor)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            CommitRename(editor, sound);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelRename(editor);
            e.Handled = true;
        }
    }

    private void OnRenameTextBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        // La garde IsVisible évite un double traitement : Escape masque déjà
        // l'éditeur avant que le LostFocus qui suit ne se déclenche.
        if (sender is TextBox { Tag: SoundEntry sound, IsVisible: true } editor)
        {
            CommitRename(editor, sound);
        }
    }

    private void CommitRename(TextBox editor, SoundEntry sound)
    {
        var newName = editor.Text?.Trim();

        if (!string.IsNullOrEmpty(newName) && newName != sound.DisplayName)
        {
            // Déclenche Changed -> RefreshSoundsList, qui reconstruit la ligne ;
            // pas besoin de manipuler display/editor nous-mêmes après ça.
            _soundLibrary?.Rename(sound.Id, newName);
            return;
        }

        CancelRename(editor);
    }

    private void CancelRename(TextBox editor)
    {
        editor.IsVisible = false;

        if (editor.Parent is Grid nameHost &&
            nameHost.Children.OfType<TextBlock>().FirstOrDefault() is TextBlock display)
        {
            display.IsVisible = true;
        }
    }

    /// <summary>
    /// Ouvre la fenêtre de découpage (trim) pour le son ciblé et persiste le
    /// résultat si l'utilisateur a validé (Saved).
    /// </summary>
    private async void OnTrimClicked(object? sender, RoutedEventArgs e)
    {
        if (_soundLibrary is null || sender is not Button { Tag: SoundEntry sound })
        {
            return;
        }

        var current = _soundLibrary.FindById(sound.Id) ?? sound;
        var dialog = new TrimSoundWindow(_audioEngine, current);

        await dialog.ShowDialog(this);

        if (dialog.Saved)
        {
            if (_audioEngine is not null)
            {
                try
                {
                    // Réécrit physiquement le fichier pour ne garder que
                    // l'extrait choisi, plutôt que de simplement mémoriser
                    // des bornes de trim appliquées à la volée pendant la
                    // lecture (cohérent avec "Enregistrer comme nouveau son").
                    var rewrittenPath = _audioEngine.RewriteTrimmedFile(
                        current.FilePath, dialog.TrimStartSeconds, dialog.TrimEndSeconds);

                    _soundLibrary.ApplyBakedTrim(current.Id, rewrittenPath);
                }
                catch
                {
                    // Échec de la réécriture (fichier verrouillé, disque
                    // plein...) : on retombe sur l'ancien comportement
                    // (mémoriser juste les bornes de trim) plutôt que de
                    // perdre le changement demandé par l'utilisateur.
                    _soundLibrary.UpdateTrim(current.Id, dialog.TrimStartSeconds, dialog.TrimEndSeconds);
                }
            }
            else
            {
                _soundLibrary.UpdateTrim(current.Id, dialog.TrimStartSeconds, dialog.TrimEndSeconds);
            }
        }
        else if (dialog.SavedAsNew && _audioEngine is not null)
        {
            try
            {
                // Écrit un vrai fichier .wav de l'extrait sur disque (dans
                // AppPaths.SoundsFolder) plutôt que de simplement mémoriser des
                // bornes de trim sur le fichier source : la copie reste donc
                // indépendante si le son d'origine est renommé, retrimé ou
                // supprimé.
                var exportedPath = _audioEngine.ExportTrimmedFile(
                    current.FilePath, dialog.NewSoundName, dialog.TrimStartSeconds, dialog.TrimEndSeconds);

                _soundLibrary.AddSound(exportedPath, dialog.NewSoundName, current.Volume);
            }
            catch
            {
                // Échec de l'export (fichier source illisible, disque plein...) :
                // la copie n'est simplement pas ajoutée. Un retour visuel
                // pourra être ajouté plus tard si besoin (voir OnPlaySoundClicked).
            }
        }
    }
}