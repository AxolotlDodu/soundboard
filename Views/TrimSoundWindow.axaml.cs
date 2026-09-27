using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Soundboard.Audio;
using Soundboard.Models;

namespace Soundboard.Views;

public partial class TrimSoundWindow : Window
{
    // Id de son dédié à la prévisualisation dans cette fenêtre : distinct des
    // ids réels de la bibliothèque pour ne jamais interférer avec un son
    // lancé par ailleurs (UI principale ou API du macro pad) pendant que
    // cette fenêtre est ouverte.
    private const string PreviewSoundId = "__trim_preview__";

    private readonly AudioEngine? _audioEngine;
    private readonly SoundEntry _sound;
    private double _durationSeconds;
    private bool _isPreviewing;

    // Pics d'amplitude (0.0-1.0) pour le dessin de la waveform.
    private double[] _peaks = Array.Empty<double>();

    // Positions courantes des deux poignées de découpage, en secondes.
    private double _trimStart;
    private double _trimEnd;

    // Poignée en cours de drag ("start", "end" ou null).
    private string? _draggingHandle;

    private const double HandleHitToleranceX = 8.0;

    /// <summary>Vrai si l'utilisateur a choisi "Enregistrer" (écrase le trim du son d'origine).</summary>
    public bool Saved { get; private set; }

    /// <summary>Vrai si l'utilisateur a choisi "Enregistrer comme nouveau son" (crée une copie).</summary>
    public bool SavedAsNew { get; private set; }

    /// <summary>Nom affiché à donner à la copie, renseigné uniquement quand SavedAsNew est vrai.</summary>
    public string NewSoundName { get; private set; } = string.Empty;

    public double TrimStartSeconds { get; private set; }
    public double? TrimEndSeconds { get; private set; }

    // Constructeur sans paramètre requis par le designer XAML d'Avalonia.
    public TrimSoundWindow() : this(null, new SoundEntry("preview", "Aperçu", string.Empty))
    {
    }

    public TrimSoundWindow(AudioEngine? audioEngine, SoundEntry sound)
    {
        _audioEngine = audioEngine;
        _sound = sound;
        InitializeComponent();

        Title = $"Ajuster « {sound.DisplayName} »";
        SoundNameText.Text = sound.DisplayName;
        NewSoundNameTextBox.Text = $"{sound.DisplayName} (extrait)";

        try
        {
            _durationSeconds = _audioEngine?.GetDurationSeconds(sound.FilePath) ?? 0;
        }
        catch
        {
            // Fichier illisible (déplacé, format non supporté...) : on garde
            // une durée nulle plutôt que de planter la fenêtre ; les sliders
            // resteront à 0, l'utilisateur peut annuler.
            _durationSeconds = 0;
        }

        var effectiveEnd = Math.Min(sound.TrimEndSeconds ?? _durationSeconds, _durationSeconds);
        var effectiveStart = Math.Min(sound.TrimStartSeconds, _durationSeconds);

        _trimStart = effectiveStart;
        _trimEnd = effectiveEnd;

        UpdateLabels();

        // La largeur réelle du canvas n'est connue qu'après layout : on
        // redessine à chaque changement de taille et au premier Loaded.
        WaveformCanvas.SizeChanged += (_, _) => DrawWaveform();
        Loaded += (_, _) => DrawWaveform();

        try
        {
            var rawPeaks = _audioEngine?.GetWaveformPeaks(sound.FilePath, 200) ?? Array.Empty<float>();
            _peaks = rawPeaks.Select(f => (double)f).ToArray();
        }
        catch
        {
            // Fichier illisible : pas de waveform, l'utilisateur peut quand
            // même régler début/fin en glissant sur la piste vide.
            _peaks = Array.Empty<double>();
        }

        PreviewButton.Click += OnPreviewClicked;
        SaveButton.Click += OnSaveClicked;
        SaveAsNewButton.Click += OnSaveAsNewClicked;
        CancelButton.Click += (_, _) => Close();

        Closing += (_, _) => StopPreview();
    }

    private void UpdateLabels()
    {
        StartLabel.Text = FormatTime(_trimStart);
        EndLabel.Text = FormatTime(_trimEnd);
    }

    private double TimeToX(double seconds)
    {
        var width = WaveformCanvas.Bounds.Width;
        return _durationSeconds <= 0 ? 0 : seconds / _durationSeconds * width;
    }

    private double XToTime(double x)
    {
        var width = WaveformCanvas.Bounds.Width;

        if (_durationSeconds <= 0 || width <= 0)
        {
            return 0;
        }

        var ratio = Math.Clamp(x / width, 0, 1);
        return ratio * _durationSeconds;
    }

    /// <summary>
    /// Redessine entièrement la waveform : barres d'amplitude, zones grisées
    /// en dehors de [_trimStart, _trimEnd], et les deux poignées.
    /// </summary>
    private void DrawWaveform()
    {
        var width = WaveformCanvas.Bounds.Width;
        var height = WaveformCanvas.Bounds.Height;
        WaveformCanvas.Children.Clear();

        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (_peaks.Length > 0)
        {
            var barWidth = width / _peaks.Length;
            var mid = height / 2;

            for (var i = 0; i < _peaks.Length; i++)
            {
                var barHeight = Math.Max(2, _peaks[i] * height);
                var bar = new Rectangle
                {
                    Width = Math.Max(1, barWidth - 1),
                    Height = barHeight,
                    Fill = new SolidColorBrush(Color.Parse("#3A6FE0")),
                };
                Canvas.SetLeft(bar, i * barWidth);
                Canvas.SetTop(bar, mid - barHeight / 2);
                WaveformCanvas.Children.Add(bar);
            }
        }

        var startX = TimeToX(_trimStart);
        var endX = TimeToX(_trimEnd);

        if (startX > 0)
        {
            var leftMask = new Rectangle
            {
                Width = startX,
                Height = height,
                Fill = new SolidColorBrush(Color.Parse("#0F1117"), 0.65),
            };
            Canvas.SetLeft(leftMask, 0);
            Canvas.SetTop(leftMask, 0);
            WaveformCanvas.Children.Add(leftMask);
        }

        if (endX < width)
        {
            var rightMask = new Rectangle
            {
                Width = width - endX,
                Height = height,
                Fill = new SolidColorBrush(Color.Parse("#0F1117"), 0.65),
            };
            Canvas.SetLeft(rightMask, endX);
            Canvas.SetTop(rightMask, 0);
            WaveformCanvas.Children.Add(rightMask);
        }

        WaveformCanvas.Children.Add(CreateHandle(startX, height));
        WaveformCanvas.Children.Add(CreateHandle(endX, height));
    }

    private static Rectangle CreateHandle(double x, double height)
    {
        var handle = new Rectangle
        {
            Width = 3,
            Height = height,
            Fill = new SolidColorBrush(Color.Parse("#5B8DEF")),
        };
        Canvas.SetLeft(handle, x - 1.5);
        Canvas.SetTop(handle, 0);
        return handle;
    }

    private void OnWaveformPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pos = e.GetPosition(WaveformCanvas);
        var startX = TimeToX(_trimStart);
        var endX = TimeToX(_trimEnd);

        // La poignée la plus proche du clic est saisie ; si aucune n'est assez
        // proche, on ignore (pas de "saut" de poignée sur simple clic ailleurs
        // sur la piste, pour éviter les modifications accidentelles).
        if (Math.Abs(pos.X - startX) <= HandleHitToleranceX)
        {
            _draggingHandle = "start";
        }
        else if (Math.Abs(pos.X - endX) <= HandleHitToleranceX)
        {
            _draggingHandle = "end";
        }
        else
        {
            return;
        }

        e.Pointer.Capture(WaveformCanvas);
    }

    private void OnWaveformPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggingHandle is null || _durationSeconds <= 0)
        {
            return;
        }

        var time = XToTime(e.GetPosition(WaveformCanvas).X);

        if (_draggingHandle == "start")
        {
            _trimStart = Math.Clamp(time, 0, _trimEnd);
        }
        else
        {
            _trimEnd = Math.Clamp(time, _trimStart, _durationSeconds);
        }

        UpdateLabels();
        DrawWaveform();
    }

    private void OnWaveformPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _draggingHandle = null;
        e.Pointer.Capture(null);
    }

    private static string FormatTime(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.ToString(@"mm\:ss");
    }

    private void OnPreviewClicked(object? sender, RoutedEventArgs e)
    {
        if (_audioEngine is null || string.IsNullOrEmpty(_sound.FilePath))
        {
            return;
        }

        if (_isPreviewing)
        {
            StopPreview();
            return;
        }

        var trimEnd = _trimEnd >= _durationSeconds ? (double?)null : _trimEnd;
        _audioEngine.PlaySound(PreviewSoundId, _sound.FilePath, _sound.Volume, _trimStart, trimEnd);
        _isPreviewing = true;
        PreviewButton.Content = "⏹ Arrêter l'extrait";
    }

    private void StopPreview()
    {
        if (!_isPreviewing)
        {
            return;
        }

        _audioEngine?.StopSound(PreviewSoundId);
        _isPreviewing = false;
        PreviewButton.Content = "▶ Écouter l'extrait";
    }

    private (double Start, double? End) ReadTrimValues()
    {
        var end = _trimEnd >= _durationSeconds ? (double?)null : _trimEnd;
        return (_trimStart, end);
    }

    private void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        StopPreview();
        (TrimStartSeconds, TrimEndSeconds) = ReadTrimValues();
        Saved = true;
        Close();
    }

    private void OnSaveAsNewClicked(object? sender, RoutedEventArgs e)
    {
        var name = NewSoundNameTextBox.Text?.Trim();

        if (string.IsNullOrEmpty(name))
        {
            // Pas de nom saisi : on ne ferme pas la fenêtre, l'utilisateur
            // doit en renseigner un pour créer la copie.
            return;
        }

        StopPreview();
        (TrimStartSeconds, TrimEndSeconds) = ReadTrimValues();
        NewSoundName = name;
        SavedAsNew = true;
        Close();
    }
}