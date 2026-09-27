using Avalonia;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Soundboard.Api;
using Soundboard.Audio;
using Soundboard.Config;

namespace Soundboard;

internal static class Program
{
    // Point d'entrée natif requis par Avalonia (ne pas renommer / supprimer).
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Console.WriteLine($"[FATAL] Exception non gérée : {e.ExceptionObject}");

        try
        {
            Console.WriteLine("[1/4] Initialisation du moteur audio...");
            var settings = SettingsRepository.Load();
            var audioEngine = new AudioEngine();

            // Résout les noms de devices persistés en index Bass actuels. S'ils
            // ne sont plus branchés, on retombe sur le device par défaut pour la
            // sortie principale, et sur la sortie de test désactivée (l'UI vide
            // alors la sélection au lieu de deviner un remplaçant).
            var availableDevices = audioEngine.GetAvailableDevices();
            int? preferredPrimaryIndex = settings.PrimaryDeviceName is null
                ? null
                : availableDevices.FirstOrDefault(d => d.Name == settings.PrimaryDeviceName)?.Index;

            audioEngine.Initialize(preferredPrimaryIndex);
            audioEngine.SetMasterVolume(settings.MasterVolume);

            if (settings.SecondaryDeviceName is not null)
            {
                var secondaryDevice = availableDevices.FirstOrDefault(d => d.Name == settings.SecondaryDeviceName);

                if (secondaryDevice is not null)
                {
                    audioEngine.SetSecondaryDevice(secondaryDevice.Index);

                    if (settings.SecondaryOutputEnabled)
                    {
                        audioEngine.SetSecondaryOutputEnabled(true);
                    }
                }
            }

            Console.WriteLine("[1/4] OK");

            // 2. Bibliothèque de sons, chargée depuis sounds.json (liste vide au
            //    premier lancement ; l'utilisateur ajoute des sons via l'UI).
            var library = SoundLibrary.LoadFromDisk();

            // 3. API HTTP locale, sur un port libre attribué par l'OS, en écoute
            //    uniquement sur 127.0.0.1 (décision : point 1 -> machine locale uniquement).
            Console.WriteLine("[2/4] Démarrage de l'API locale...");
            var apiBuilder = WebApplication.CreateBuilder(args);
            apiBuilder.Logging.ClearProviders();

            var apiApp = apiBuilder.Build();
            apiApp.Urls.Add("http://127.0.0.1:0"); // port 0 = laisse l'OS choisir un port libre
            apiApp.MapSoundboardEndpoints(audioEngine, library);

            apiApp.Start();

            // Le port réel n'est connu qu'après Start() ; on le lit via IServerAddressesFeature
            // plutôt que via apiApp.Urls, qui garde la valeur "0" qu'on lui a passée.
            var addressesFeature = apiApp.Services
                .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                .Features
                .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();

            var actualUrl = addressesFeature!.Addresses.First();
            var actualPort = int.Parse(actualUrl.Split(':').Last());
            PortRegistry.WritePort(actualPort);
            Console.WriteLine($"[2/4] OK - API sur le port {actualPort}");

            // 4. Lance l'UI Avalonia. Bloquant jusqu'à fermeture de la fenêtre.
            App.AudioEngine = audioEngine;
            App.SoundLibrary = library;
            App.Settings = settings;

            Console.WriteLine("[3/4] Lancement de l'UI Avalonia...");
            try
            {
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
                Console.WriteLine("[4/4] Fenêtre fermée normalement.");
            }
            finally
            {
                apiApp.StopAsync().GetAwaiter().GetResult();
                audioEngine.Dispose();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FATAL] {ex}");
            Console.WriteLine("Appuie sur une touche pour fermer...");
            Console.ReadKey();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}