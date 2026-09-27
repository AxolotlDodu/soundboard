using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Soundboard.Audio;
using Soundboard.Models;

namespace Soundboard.Api;

/// <summary>
/// Déclare les routes de l'API locale utilisée par le macro pad.
/// </summary>
public static class ApiEndpoints
{
    public static void MapSoundboardEndpoints(this WebApplication app, AudioEngine engine, SoundLibrary library)
    {
        // POST /play/{soundId} - déclenche la lecture d'un son par son id stable
        app.MapPost("/play/{soundId}", (string soundId) =>
        {
            var sound = library.FindById(soundId);

            if (sound is null)
            {
                return Results.NotFound(new { error = $"Son '{soundId}' introuvable." });
            }

            try
            {
                engine.PlaySound(sound.Id, sound.FilePath, sound.Volume, sound.TrimStartSeconds, sound.TrimEndSeconds);
                return Results.Ok(new { played = sound.Id });
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 500);
            }
        });

        // POST /stop/{soundId} - arrête toutes les instances en cours d'un son précis
        app.MapPost("/stop/{soundId}", (string soundId) =>
        {
            engine.StopSound(soundId);
            return Results.Ok(new { stopped = soundId });
        });

        // POST /stop-all - arrête tous les sons en cours, toutes sorties confondues
        app.MapPost("/stop-all", () =>
        {
            engine.StopAll();
            return Results.Ok(new { stopped = "all" });
        });

        // GET /volume - volume général actuel (0.0 à 1.0)
        app.MapGet("/volume", () => Results.Ok(new { volume = engine.MasterVolume }));

        // POST /volume/{percent} - fixe le volume général en absolu (0 à 100)
        app.MapPost("/volume/{percent:int}", (int percent) =>
        {
            engine.SetMasterVolume(Math.Clamp(percent, 0, 100) / 100.0);
            return Results.Ok(new { volume = engine.MasterVolume });
        });

        // POST /volume/adjust/{deltaPercent} - ajuste le volume général de
        // deltaPercent points (négatif pour baisser) ; endpoint pensé pour un
        // encodeur rotatif, qui envoie un delta par cran plutôt qu'une valeur
        // absolue.
        app.MapPost("/volume/adjust/{deltaPercent:int}", (int deltaPercent) =>
        {
            engine.SetMasterVolume(engine.MasterVolume + deltaPercent / 100.0);
            return Results.Ok(new { volume = engine.MasterVolume });
        });

        // GET /sounds - liste des sons disponibles (utile pour un futur mapping type Stream Deck)
        app.MapGet("/sounds", () => Results.Ok(library.All));

        // GET /devices - liste des sorties audio disponibles
        app.MapGet("/devices", () => Results.Ok(engine.GetAvailableDevices()));

        // GET /health - ping simple pour vérifier que la soundboard tourne
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
    }
}