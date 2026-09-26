using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Soundboard.Api;
using Soundboard.Audio;
using Soundboard.Models;

namespace Soundboard.Views;

public partial class MainWindow : Window
{
    private readonly AudioEngine? _audioEngine;
    private readonly SoundLibrary? _soundLibrary;

    // Ids des sons cochés via la case de sélection multiple. Conservé côté
    // code-behind (et non sur SoundEntry, qui est immuable) puisque la liste
    // est reconstruite à chaque Changed de la bibliothèque.
    private readonly HashSet<string> _selectedSoundIds = new(StringComparer.OrdinalIgnoreCase);

    // Ids des sons actuellement en cours de lecture, tenu à jour via les
    // évènements PlaybackStarted/PlaybackEnded de l'AudioEngine (source de
    // vérité unique : couvre aussi bien un clic manuel qu'une fin naturelle
    // ou un arrêt déclenché depuis l'API du macro pad).
    private readonly HashSet<string> _playingSoundIds = new(StringComparer.OrdinalIgnoreCase);

    // SoundsListBox, RemoveSoundButton, AddSoundButton, DeviceComboBox,
    // SecondaryOutputToggle, SecondaryDeviceComboBox et SelectAllCheckBox
    // sont générés automatiquement comme champs par Avalonia.Generators à
    // partir de leur x:Name dans le XAML ; pas besoin de FindControl ici.

    // Constructeur sans paramètre requis par le designer XAML d'Avalonia.
    public MainWindow() : this(null, null)
    {
    }

    public MainWindow(AudioEngine? audioEngine, SoundLibrary? soundLibrary)
    {
        _audioEngine = audioEngine;
        _soundLibrary = soundLibrary;
        InitializeComponent();

        SetupDeviceSelector();
        SetupSecondaryOutputSelector();
        SetupSoundsList();
        SetupPlaybackTracking();
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
    /// arrêt via l'API du macro pad) ne passent pas par un clic sur ce
    /// bouton précis.
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
        DeviceComboBox.SelectedItem = devices.FirstOrDefault(d => d.IsDefault);

        DeviceComboBox.SelectionChanged += (_, _) =>
        {
            if (DeviceComboBox.SelectedItem is AudioDeviceInfo selected)
            {
                _audioEngine.SelectDevice(selected.Index);
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
        SecondaryDeviceComboBox.ItemsSource = _audioEngine.GetAvailableDevices();

        SecondaryOutputToggle.IsCheckedChanged += (_, _) =>
        {
            var enabled = SecondaryOutputToggle.IsChecked == true;
            SecondaryOutputToggle.Content = enabled ? "On" : "Off";
            SecondaryDeviceComboBox.IsEnabled = enabled;
            _audioEngine.SetSecondaryOutputEnabled(enabled);

            if (enabled && SecondaryDeviceComboBox.SelectedItem is AudioDeviceInfo selected)
            {
                _audioEngine.SetSecondaryDevice(selected.Index);
            }
        };

        SecondaryDeviceComboBox.SelectionChanged += (_, _) =>
        {
            if (SecondaryDeviceComboBox.SelectedItem is AudioDeviceInfo selected)
            {
                _audioEngine.SetSecondaryDevice(selected.Index);
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

            if (localPath is not null)
            {
                _soundLibrary.AddFromFile(localPath);
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
        // au moment du binding et ne reflète pas un changement de volume fait
        // depuis via le slider. On relit donc le volume à jour dans la
        // bibliothèque avant de jouer le son.
        var current = _soundLibrary?.FindById(sound.Id) ?? sound;

        try
        {
            _audioEngine.PlaySound(current.Id, current.FilePath, current.Volume);
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
}