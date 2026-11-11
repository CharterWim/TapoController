// Houdt de lampen vast en draait de scène-engine. Eén instantie per proces.
//
// - Bij opstarten: alle lampen verbinden en hun basisstand onthouden (kleur of kleurtemperatuur).
// - StartScene: crossfade naar een scène; lantaarns doen alleen mee als de scène ze een rol geeft.
// - Stop: crossfade terug naar de basisstand, daarna de lampen exact terugzetten (incl. kleurtemperatuur).
// - Shutdown (dienst stopt): direct terugzetten.
// Commando's worden achter elkaar afgehandeld (_cmd), de engine draait op de achtergrond.

using System.Diagnostics;
using System.Text.Json.Nodes;
using Tuinverlichting.Scenes;
using Tuinverlichting.Tapo;

namespace Tuinverlichting.Host;

/// Basisstand van een lamp: kleur, of kleurtemperatuur (wit) met helderheid.
public sealed record BaseState(Hsv? Color, int ColorTemp, int Brightness)
{
    /// Benadering in HSV, alleen gebruikt om er tijdens een crossfade naartoe te faden.
    public Hsv AsHsv => Color ?? (ColorTemp < 4000 ? new Hsv(30, 35, Brightness) : new Hsv(220, 8, Brightness));

    public static BaseState FromDeviceInfo(JsonNode info) =>
        (int?)info["color_temp"] is > 0 and var ct
            ? new BaseState(null, ct, (int?)info["brightness"] ?? 100)
            : new BaseState(new Hsv((int?)info["hue"] ?? 0, (int?)info["saturation"] ?? 0, (int?)info["brightness"] ?? 50), 0, (int?)info["brightness"] ?? 50);

    public override string ToString() => Color is { } c ? c.ToString() : $"wit {ColorTemp} K B{Brightness}";
}

public sealed class LightController : IAsyncDisposable
{
    sealed class Lamp(LampConfig config, TapoLamp tapo, bool isLantern)
    {
        public LampConfig Config { get; } = config;
        public TapoLamp Tapo { get; } = tapo;
        public bool IsLantern { get; } = isLantern;
        public BaseState Base { get; set; } = isLantern ? new BaseState(null, 2700, 80) : new BaseState(new Hsv(30, 20, 40), 0, 40);
        public bool BaseKnown { get; set; }
    }

    readonly GardenConfig _garden;
    readonly ILogger<LightController> _log;
    readonly List<Lamp> _lamps;
    readonly SceneEngine _engine;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly SemaphoreSlim _cmd = new(1, 1);
    CancellationTokenSource? _runCts;
    Task _run = Task.CompletedTask;
    CancellationTokenSource? _pendingStop;

    public LightController(GardenConfig garden, string password, ILogger<LightController> log, EngineOptions? options = null)
    {
        _garden = garden;
        _log = log;
        _lamps = garden.Lamps.Select(c => new Lamp(c, new TapoLamp(c.Ip, password, c.Name), false))
            .Concat(garden.Lanterns.Select(c => new Lamp(c, new TapoLamp(c.Ip, password, c.Name), true)))
            .ToList();
        foreach (var l in _lamps)
            l.Tapo.Error += (lamp, e) => _log.LogWarning("{Lamp}: {Error}{Offline}", lamp.Name, e.Message,
                lamp.IsOffline ? $" (onbereikbaar, {lamp.OfflineBackoff.TotalSeconds:0} s overslaan)" : "");
        _engine = new SceneEngine(_lamps.Select(l => (l.Config, (ILightOutput)l.Tapo)), garden.LanternPos, options);
    }

    public SceneDefinition? ActiveScene { get; private set; }
    public DateTimeOffset? ActiveSince { get; private set; }
    public bool Stopping { get; private set; }
    double Now => _clock.Elapsed.TotalSeconds;
    static TimeOnly LocalNow() => TimeOnly.FromDateTime(DateTime.Now);
    bool Running => !_run.IsCompleted;

    /// Alle lampen verbinden (parallel, tot 3 pogingen) en hun basisstand onthouden.
    public async Task InitializeAsync(CancellationToken ct)
    {
        _log.LogInformation("{Count} lampen verbinden ...", _lamps.Count);
        await Task.WhenAll(_lamps.Select(async l =>
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    var info = await l.Tapo.ConnectAsync(ct);
                    l.Base = BaseState.FromDeviceInfo(info);
                    l.BaseKnown = true;
                    _log.LogInformation("{Lamp} verbonden, basisstand {Base}", l.Config.Name, l.Base);
                    return;
                }
                catch (TapoException e) when (e.IsCredentialProblem)
                {
                    _log.LogError("{Lamp}: inloggen geweigerd ({Error}); controleer Tapo:Password", l.Config.Name, e.Message);
                    return;
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    if (attempt == 3)
                        _log.LogWarning("{Lamp} niet bereikbaar ({Error}); standaard-basisstand {Base}", l.Config.Name, e.Message, l.Base);
                    else await Task.Delay(500 * attempt, ct);
                }
            }
        }));
    }

    /// Start (of wissel naar) een scène met een crossfade.
    public async Task StartSceneAsync(SceneDefinition scene, TimeSpan fade)
    {
        await _cmd.WaitAsync();
        try
        {
            _pendingStop?.Cancel();
            _pendingStop = null;
            Stopping = false;
            double now = Now;

            if (!Running)
            {
                // vanuit rust: alles fadet vanuit de basisstand
                _engine.Reset(_lamps.ToDictionary(l => l.Config.Name, l => l.Base.AsHsv));
                foreach (var l in _lamps) _engine.SetActive(l.Config.Name, !l.IsLantern || scene.UsesLanterns);
                _engine.SetEffect(scene.Effect, fade, now);
                _runCts = new CancellationTokenSource();
                var token = _runCts.Token;
                _run = Task.Run(() => _engine.RunAsync(_clock, LocalNow, token));
            }
            else
            {
                _engine.SetEffect(scene.Effect, fade, now);
                foreach (var l in _lamps.Where(l => l.IsLantern))
                {
                    bool active = _engine.IsActive(l.Config.Name);
                    if (scene.UsesLanterns && !active)
                        _engine.SetActive(l.Config.Name, true, l.Base.AsHsv);   // fadet vanaf wit naar de scène
                    else if (!scene.UsesLanterns && active)
                    {
                        _engine.SetActive(l.Config.Name, false);
                        await l.Tapo.FlushAsync();
                        await RestoreAsync(l);                                  // lantaarn terug naar wit
                    }
                }
            }

            ActiveScene = scene;
            ActiveSince = DateTimeOffset.Now;
            _log.LogInformation("Scène {Scene} gestart (crossfade {Fade} s)", scene.Key, fade.TotalSeconds);
        }
        finally
        {
            _cmd.Release();
        }
    }

    /// Fadet terug naar de basisstand en zet de lampen daarna exact terug. Keert direct terug;
    /// een nieuwe StartScene tijdens het uitfaden annuleert het stoppen.
    public async Task StopAsync(TimeSpan fade)
    {
        await _cmd.WaitAsync();
        try
        {
            if (!Running || Stopping) return;
            Stopping = true;
            var colors = _lamps.ToDictionary(l => l.Config.Name, l => l.Base.AsHsv);
            _engine.SetEffect(new PerLampStaticEffect(colors, new Hsv(30, 20, 40)), fade, Now);
            var cts = _pendingStop = new CancellationTokenSource();
            _log.LogInformation("Stoppen: {Fade} s terugfaden naar de basisstand", fade.TotalSeconds);
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(fade + TimeSpan.FromSeconds(1.5), cts.Token); }
                catch (OperationCanceledException) { return; }
                await _cmd.WaitAsync();
                try
                {
                    if (cts.IsCancellationRequested) return;
                    await HaltAndRestoreAsync();
                }
                finally { _cmd.Release(); }
            });
        }
        finally
        {
            _cmd.Release();
        }
    }

    /// Dienst stopt: engine stoppen en alles direct terugzetten.
    public async Task ShutdownAsync()
    {
        await _cmd.WaitAsync();
        try
        {
            _pendingStop?.Cancel();
            if (Running) await HaltAndRestoreAsync();
        }
        finally { _cmd.Release(); }
    }

    async Task HaltAndRestoreAsync()
    {
        _runCts?.Cancel();
        await _run;
        await Task.WhenAll(_lamps.Select(l => l.Tapo.FlushAsync()));
        await Task.WhenAll(_lamps.Select(RestoreAsync));
        ActiveScene = null;
        ActiveSince = null;
        Stopping = false;
        _log.LogInformation("Gestopt; lampen terug op hun basisstand");
    }

    async Task RestoreAsync(Lamp l)
    {
        try
        {
            if (l.Base.Color is { } c) await l.Tapo.SetColorAsync(c);
            else await l.Tapo.SetColorTemperatureAsync(l.Base.ColorTemp, l.Base.Brightness);
        }
        catch (Exception e)
        {
            _log.LogWarning("{Lamp}: terugzetten mislukt: {Error}", l.Config.Name, e.Message);
        }
    }

    public object Status() => new
    {
        scene = ActiveScene?.Key,
        sceneName = ActiveScene?.Name,
        since = ActiveSince,
        stopping = Stopping,
        eveningFactor = Math.Round(_engine.EveningFactor(LocalNow()), 2),
        lamps = _lamps.OrderBy(l => l.Config.Pos).ThenBy(l => l.Config.Name).Select(l => new
        {
            name = l.Config.Name,
            ip = l.Config.Ip,
            zone = l.Config.Zone,
            driven = Running && _engine.IsActive(l.Config.Name),
            offline = l.Tapo.IsOffline,
            baseState = l.Base.ToString(),
            baseKnown = l.BaseKnown,
            stats = l.Tapo.Stats.ToString(),
        }),
    };

    public async ValueTask DisposeAsync()
    {
        _runCts?.Cancel();
        try { await _run; } catch { }
        foreach (var l in _lamps) await l.Tapo.DisposeAsync();
        _cmd.Dispose();
    }
}
