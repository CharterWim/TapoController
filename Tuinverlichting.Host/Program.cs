// Tuinverlichting API-host.
//   GET  /api/status                       actieve scène, avondcurve en per lamp de toestand
//   GET  /api/scenes                       beschikbare scènes
//   POST /api/scenes/{naam}/start?fade=20   scène starten (of ernaar wisselen) met crossfade in seconden
//   POST /api/stop?fade=10                 terugfaden naar de basisstand en de lampen exact terugzetten
//   GET  /health                           leeft de dienst?
// Wachtwoord: Tapo:Password (user secrets "tuinverlichting-tapo", of omgevingsvariabele Tapo__Password).
// Optioneel Api:Key → dan moet elke /api-aanroep header X-Api-Key meesturen.

using System.Security.Cryptography;
using System.Text;
using Tuinverlichting.Host;
using Tuinverlichting.Scenes;

var builder = WebApplication.CreateBuilder(args);
// Ook buiten Development (bijv. de exe direct starten) de user secrets lezen; op de Pi komt het uit de omgeving.
builder.Configuration.AddUserSecrets<LightController>(optional: true);

var password = builder.Configuration["Tapo:Password"];
if (string.IsNullOrEmpty(password))
{
    Console.Error.WriteLine("Geen Tapo:Password gevonden (user secrets of omgevingsvariabele Tapo__Password).");
    return 1;
}

var configPath = builder.Configuration["Garden:ConfigPath"];
var garden = string.IsNullOrWhiteSpace(configPath) ? GardenConfig.Find() : GardenConfig.Load(configPath);

builder.Services.AddSingleton(garden);
builder.Services.AddSingleton(sp => new LightController(garden, password, sp.GetRequiredService<ILogger<LightController>>()));
builder.Services.AddHostedService<LightHostedService>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.WriteIndented = true);

var app = builder.Build();

// Optionele API-sleutel
var apiKey = app.Configuration["Api:Key"];
if (!string.IsNullOrEmpty(apiKey))
{
    var expected = Encoding.UTF8.GetBytes(apiKey);
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api"))
        {
            var given = Encoding.UTF8.GetBytes(ctx.Request.Headers["X-Api-Key"].ToString());
            if (!CryptographicOperations.FixedTimeEquals(given, expected))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new { error = "X-Api-Key ontbreekt of klopt niet" });
                return;
            }
        }
        await next();
    });
    app.Logger.LogInformation("API-sleutel actief: /api vereist header X-Api-Key");
}
else
{
    app.Logger.LogWarning("Geen Api:Key ingesteld: iedereen op het netwerk kan de verlichting bedienen");
}

app.MapGet("/health", () => Results.Ok(new { ok = true }));

app.MapGet("/api/status", (LightController lights) => Results.Ok(lights.Status()));

app.MapGet("/api/scenes", (LightController lights) => Results.Ok(SceneCatalog.All.Select(s => new
{
    key = s.Key,
    name = s.Name,
    usesLanterns = s.UsesLanterns,
    active = lights.ActiveScene?.Key == s.Key,
})));

app.MapPost("/api/scenes/{key}/start", async (string key, double? fade, LightController lights) =>
{
    var scene = SceneCatalog.Find(key);
    if (scene is null)
        return Results.NotFound(new { error = $"Onbekende scène '{key}'", available = SceneCatalog.All.Select(s => s.Key) });
    var f = TimeSpan.FromSeconds(Math.Clamp(fade ?? 20, 0, 600));
    await lights.StartSceneAsync(scene, f);
    return Results.Ok(new { started = scene.Key, name = scene.Name, fadeSeconds = f.TotalSeconds });
});

app.MapPost("/api/stop", async (double? fade, LightController lights) =>
{
    var f = TimeSpan.FromSeconds(Math.Clamp(fade ?? 10, 0, 600));
    bool wasRunning = lights.ActiveScene is not null;
    await lights.StopAsync(f);
    return Results.Ok(new { stopping = wasRunning, fadeSeconds = f.TotalSeconds });
});

app.Run();
return 0;

/// Verbindt de lampen bij het opstarten en zet ze terug als de dienst stopt.
sealed class LightHostedService(LightController lights) : IHostedService
{
    public Task StartAsync(CancellationToken ct) => lights.InitializeAsync(ct);
    public Task StopAsync(CancellationToken ct) => lights.ShutdownAsync();
}
