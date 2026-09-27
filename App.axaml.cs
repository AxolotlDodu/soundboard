using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Soundboard.Api;
using Soundboard.Audio;
using Soundboard.Config;
using Soundboard.Views;

namespace Soundboard;

public class App : Application
{
    // Renseigné par Program.cs avant le démarrage du lifetime desktop.
    public static AudioEngine? AudioEngine { get; set; }
    public static SoundLibrary? SoundLibrary { get; set; }
    public static AppSettings? Settings { get; set; }

    private MainWindow? _mainWindow;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // La fermeture de la fenêtre (croix) ne doit pas quitter l'appli :
            // elle continue de tourner en arrière-plan (icône tray, API HTTP
            // toujours active pour le macro pad). Seul "Quitter" dans le menu
            // tray termine réellement le process.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _mainWindow = new MainWindow(AudioEngine, SoundLibrary, Settings);
            _mainWindow.Closing += OnMainWindowClosing;

            desktop.MainWindow = _mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (e.CloseReason != WindowCloseReason.WindowClosing)
        {
            // Fermeture réelle (ex. déclenchée par "Quitter" via desktop.Shutdown()) :
            // on laisse faire, sinon l'appli ne pourrait plus jamais se fermer.
            return;
        }

        e.Cancel = true;
        _mainWindow?.Hide();
    }

    private void OnTrayIconClicked(object? sender, System.EventArgs e) => ShowMainWindow();

    private void OnTrayConfigurationClicked(object? sender, System.EventArgs e) => ShowMainWindow();

    private void OnTrayQuitClicked(object? sender, System.EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }
}