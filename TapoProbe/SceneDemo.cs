// scenedemo: de scène-engine op alle schuttinglampen, met drie scènes en crossfades ertussen.

using System.Diagnostics;
using Tuinverlichting.Scenes;
using Tuinverlichting.Tapo;

static partial class Fence
{
    public static async Task<int> SceneDemoCommand(string password, double partS, double fadeS)
    {
        var cfg = LoadConfig();
        Console.WriteLine($"{cfg.Lamps.Length} schuttinglampen verbinden ...");
        var lamps = await ConnectAll(cfg.Lamps, password);
        if (lamps.Count == 0) return 1;
        foreach (var l in lamps)
            l.Lamp.Error += (lamp, e) => Console.WriteLine(
                $"\n  [{lamp.Name}] fout: {e.Message}{(lamp.IsOffline ? $" -> {lamp.OfflineBackoff.TotalSeconds:0} s overslaan" : "")}");

        var engine = new SceneEngine(lamps.Select(l => (l.Config, (ILightOutput)l.Lamp)), cfg.LanternPos);
        foreach (var l in lamps)
        {
            var o = l.Info;
            // Lamp op kleurtemperatuur (wit)? Dan als startpunt een neutraal wit in HSV.
            var start = (int?)o["color_temp"] > 0
                ? new Hsv(0, 0, (int)o["brightness"]!)
                : new Hsv((int)o["hue"]!, (int)o["saturation"]!, (int)o["brightness"]!);
            engine.SetInitialState(l.Config.Name, start);
        }

        var scenes = new (string Name, Effect Effect)[]
        {
            ("Golf vanuit de lantaarn (blauw)", new WaveEffect { Direction = WaveDirection.FromLantern, PeriodS = 30, Wavelength = 20 }),
            ("Smeulend vuur (rood/oranje)", new FireEffect()),
            ("Golf L01 -> L13 (blauw, lang verloop)", new WaveEffect { Direction = WaveDirection.Forward, PeriodS = 30, Wavelength = 30 }),
        };

        var factor = engine.EveningFactor(TimeOnly.FromDateTime(DateTime.Now));
        Console.WriteLine($"""

            SCENEDEMO  {lamps.Count} van {cfg.Lamps.Length} lampen, lantaarnpaal op positie {cfg.LanternPos}
            Elke scène {partS} s, crossfade {fadeS} s. Totaal ~{scenes.Length * partS + fadeS:0} s.
            {string.Join("\n", scenes.Select((s, i) => $"  {i + 1}. {s.Name}"))}

            Veiligheidslagen: max {engine.Options.MaxHueSpeed}°/s tint en {engine.Options.MaxBrightnessSpeed}%/s helderheid per lamp,
            plafond per zone (noord max 50%), avondcurve (nu: helderheid x {factor:0.00}{(factor < 1 ? ", het is na 22:00" : "")}).

            Lees dit eerst:
              - Na Enter fadet elke lamp vanuit zijn huidige kleur naar scène 1.
              - Bij elke scènewissel zie je het nummer hier verschijnen.
              - Aan het eind gaan alle lampen terug naar hun oude stand.
              - Ctrl+C stopt eerder, ook netjes.

            """);
        Console.Write("Enter = demo starten ... ");
        if (Console.ReadLine() is null) return 1;

        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += onCancel;

        var clock = Stopwatch.StartNew();
        var run = engine.RunAsync(clock, () => TimeOnly.FromDateTime(DateTime.Now), stop.Token);
        try
        {
            for (int i = 0; i < scenes.Length && !stop.IsCancellationRequested; i++)
            {
                engine.SetEffect(scenes[i].Effect, TimeSpan.FromSeconds(fadeS), clock.Elapsed.TotalSeconds);
                Console.WriteLine($"\n[{clock.Elapsed.TotalSeconds,4:0} s] scène {i + 1}: {scenes[i].Name} (crossfade {fadeS} s)");
                try { await Task.Delay(TimeSpan.FromSeconds(partS + (i == scenes.Length - 1 ? fadeS : 0)), stop.Token); }
                catch (OperationCanceledException) { }
            }
        }
        finally
        {
            stop.Cancel();
            await run;
            Console.CancelKeyPress -= onCancel;
        }

        Console.WriteLine("\nTerugzetten ...");
        await Task.WhenAll(lamps.Select(l => l.Lamp.FlushAsync()));
        var failed = await RestoreAll(lamps);

        Console.WriteLine("\nPer lamp:");
        foreach (var l in lamps.OrderBy(l => l.Config.Pos))
        {
            Console.WriteLine($"  {l.Config.Name,-9} {l.Lamp.Stats}");
            await l.Lamp.DisposeAsync();
        }
        var missing = cfg.Lamps.Select(c => c.Name).Except(lamps.Select(l => l.Config.Name)).ToList();
        if (missing.Count > 0) Console.WriteLine($"Niet verbonden (deden niet mee): {string.Join(", ", missing)}");
        Console.WriteLine(failed.Count == 0
            ? $"Alle {lamps.Count} deelnemende lampen teruggezet naar hun oude stand."
            : $"{lamps.Count - failed.Count} van {lamps.Count} teruggezet. NIET teruggezet: {string.Join(", ", failed)}");
        return failed.Count == 0 && missing.Count == 0 ? 0 : 1;
    }

    static async Task<List<string>> RestoreAll(List<Connected> lamps)
    {
        var failed = new System.Collections.Concurrent.ConcurrentBag<string>();
        await Task.WhenAll(lamps.Select(async l =>
        {
            try
            {
                var o = l.Info;
                if ((int?)o["color_temp"] is > 0 and var ct)
                    await l.Lamp.SetColorTemperatureAsync(ct, (int)o["brightness"]!);
                else
                    await l.Lamp.SetColorAsync(new Hsv((int)o["hue"]!, (int)o["saturation"]!, (int)o["brightness"]!));
            }
            catch (Exception e)
            {
                failed.Add(l.Config.Name);
                Console.WriteLine($"  [{l.Config.Name}] terugzetten mislukt: {e.Message}");
            }
        }));
        return failed.Order().ToList();
    }
}
