// Commando's voor alle schuttinglampen tegelijk, via de Tuinverlichting.Tapo-library.
//   connectall            alle lampen uit lamps.json parallel verbinden, naam/model/stand tonen (verandert niets)
//   fencewave [periode s=10] [duur s=90] [interval ms] [tint min] [tint max] [golflengte]
//                         blauwe golf die langs de schutting loopt (L01 -> L13), interactief gestart
//   scenedemo [deel s=60] [crossfade s=20]
//                         scène-engine: golf vanuit de lantaarn -> vuur -> golf L01->L13, met crossfades

using System.Diagnostics;
using System.Text.Json.Nodes;
using Tuinverlichting.Scenes;
using Tuinverlichting.Tapo;

static partial class Fence
{
    static GardenConfig LoadConfig() => GardenConfig.Find();

    record Connected(LampConfig Config, TapoLamp Lamp, JsonNode Info, long LoginMs);

    static async Task<List<Connected>> ConnectAll(IEnumerable<LampConfig> configs, string password)
    {
        var tasks = configs.Select(async c =>
        {
            var lamp = new TapoLamp(c.Ip, password, c.Name);
            var sw = Stopwatch.StartNew();
            // Met 13 logins tegelijk duurt er soms één langer dan de time-out: tot 3 pogingen.
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    var info = await lamp.ConnectAsync();
                    if (attempt > 1) Console.WriteLine($"{c.Name,-11} {c.Ip,-15} verbonden bij poging {attempt}");
                    return new Connected(c, lamp, info, sw.ElapsedMilliseconds);
                }
                catch (Exception e) when (e is not TapoException { IsCredentialProblem: true } && attempt < 3)
                {
                    await Task.Delay(500 * attempt);
                }
                catch (Exception e)
                {
                    await lamp.DisposeAsync();
                    Console.WriteLine($"{c.Name,-11} {c.Ip,-15} FOUT: {e.Message}");
                    if (e is TapoException { IsCredentialProblem: true }) throw;
                    return null;
                }
            }
        });
        var results = await Task.WhenAll(tasks);
        return results.Where(r => r is not null).Select(r => r!).ToList();
    }

    static string Nick(JsonNode info)
    {
        var s = (string?)info["nickname"];
        try { return s is null ? "?" : System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(s)); }
        catch (FormatException) { return s!; }
    }

    public static async Task<int> ConnectAllCommand(string password)
    {
        var cfg = LoadConfig();
        var all = cfg.Lamps.Concat(cfg.Lanterns).ToArray();
        Console.WriteLine($"{all.Length} lampen parallel verbinden ...\n");
        var sw = Stopwatch.StartNew();
        var connected = await ConnectAll(all, password);
        var total = sw.ElapsedMilliseconds;

        foreach (var c in connected.OrderBy(c => c.Config.Pos).ThenBy(c => c.Config.Name))
        {
            var nick = Nick(c.Info);
            var check = nick == c.Config.Name ? "ok" : $"LET OP: lamp heet '{nick}'";
            Console.WriteLine($"{c.Config.Name,-11} {c.Config.Ip,-15} {c.Info["model"],-5} login {c.LoginMs,5} ms  " +
                              $"bri={c.Info["brightness"]} hue={c.Info["hue"]} sat={c.Info["saturation"]} ct={c.Info["color_temp"]}  naam {check}");
            await c.Lamp.DisposeAsync();
        }
        Console.WriteLine($"\n{connected.Count}/{all.Length} verbonden in {total} ms totaal (parallel).");
        return connected.Count == all.Length ? 0 : 1;
    }


    public static async Task<int> FenceWaveCommand(string password, double periodS, double durationS,
        int intervalMs, double hueMin, double hueMax, double wavelength)
    {
        double Wavelength = wavelength;   // in lampplaatsen (schutting = 15 plaatsen)

        var cfg = LoadConfig();
        Console.WriteLine($"{cfg.Lamps.Length} schuttinglampen verbinden ...");
        var lamps = await ConnectAll(cfg.Lamps, password);
        if (lamps.Count == 0) return 1;
        foreach (var l in lamps)
            l.Lamp.Error += (lamp, e) => Console.WriteLine(
                $"\n  [{lamp.Name}] fout: {e.Message}{(lamp.IsOffline ? $" -> {lamp.OfflineBackoff.TotalSeconds:0} s overslaan" : "")}");

        double hueMid = (hueMin + hueMax) / 2, hueAmp = (hueMax - hueMin) / 2;
        Console.WriteLine($"""

            FENCEWAVE  {lamps.Count} van {cfg.Lamps.Length} lampen, golf loopt van L01 naar L13
            Periode {periodS} s, golflengte {Wavelength} lampplaatsen, {durationS} s lang.
            Elke lamp krijgt elke {intervalMs} ms een update; de lampen zijn gespreid binnen dat interval.
            Tint {hueMin:0}-{hueMax:0}, helderheid 35-60% met plafond per zone
            (noordzijde L01-L06 max 50% i.v.m. de buren). Lantaarns doen niet mee.
            Een lamp die niet reageert wordt {lamps[0].Lamp.OfflineBackoff.TotalSeconds:0} s overgeslagen en dan opnieuw geprobeerd.

            Lees dit eerst:
              - Na Enter faden alle lampen naar het begin van de golf, daarna loopt hij.
              - Na {durationS} s gaan alle lampen terug naar hun oude stand.
              - Ctrl+C stopt eerder, ook netjes (lampen worden teruggezet).

            """);
        Console.Write("Enter = golf starten ... ");
        if (Console.ReadLine() is null) return 1;

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(durationS));
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += onCancel;

        // Gespreid sturen: per tik één lamp, zodat er nooit 13 requests tegelijk de lucht in gaan.
        double tickMs = (double)intervalMs / lamps.Count;
        var ordered = lamps.OrderBy(l => l.Config.Pos).ToArray();
        var clock = Stopwatch.StartNew();
        try
        {
            for (long k = 0; !stop.IsCancellationRequested; k++)
            {
                long due = (long)(k * tickMs);
                if (clock.ElapsedMilliseconds < due)
                    try { await Task.Delay((int)(due - clock.ElapsedMilliseconds), stop.Token); } catch (OperationCanceledException) { break; }

                var l = ordered[k % ordered.Length];
                double t = due / 1000.0 + 1.0;   // doel = waar de golf over ~1 s (ingebouwde fade) moet zijn
                double x = l.Config.Pos;
                // twee sinussen: tint en helderheid lopen met verschillende snelheid, wat organischer oogt
                double ph1 = 2 * Math.PI * (t / periodS - x / Wavelength);
                double ph2 = 2 * Math.PI * (t / (periodS * 1.37) - x / (Wavelength * 0.8)) + 1.0;
                double hue = hueMid + hueAmp * Math.Sin(ph1);
                double bri = Math.Min(47.5 + 12.5 * Math.Sin(ph2), l.Config.MaxBrightness);
                l.Lamp.Submit(new Hsv(hue, 70, bri));

                if (k % (ordered.Length * 10) == 0) Console.Write($"\r  {clock.Elapsed.TotalSeconds,5:0} s ");
            }
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }

        Console.WriteLine("\nTerugzetten ...");
        await Task.WhenAll(lamps.Select(l => l.Lamp.FlushAsync()));
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

        Console.WriteLine("\nPer lamp:");
        foreach (var l in ordered)
        {
            Console.WriteLine($"  {l.Config.Name,-9} {l.Lamp.Stats}");
            await l.Lamp.DisposeAsync();
        }
        var missing = cfg.Lamps.Select(c => c.Name).Except(lamps.Select(l => l.Config.Name)).ToList();
        if (missing.Count > 0) Console.WriteLine($"Niet verbonden (deden niet mee): {string.Join(", ", missing)}");
        Console.WriteLine(failed.IsEmpty
            ? $"Alle {lamps.Count} deelnemende lampen teruggezet naar hun oude stand."
            : $"{lamps.Count - failed.Count} van {lamps.Count} teruggezet. NIET teruggezet: {string.Join(", ", failed.Order())}");
        return failed.IsEmpty && missing.Count == 0 ? 0 : 1;
    }
}
