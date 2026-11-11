// TapoProbe - verkenning van de Tapo-lampen.
//   TapoProbe discover [broadcastadres]   stap 1: UDP-discovery -> discovery.json
//   TapoProbe selftest                    crypto controleren tegen testvectoren
//   TapoProbe login [ip ...]              stap 2: TPAP-login + component_nego + get_device_info -> devices/<ip>.json
//                                         (zonder IP's: alle TPAP-lampen uit discovery.json)
// Wachtwoord (Tapo-account) via user secrets:  dotnet user-secrets set "Tapo:Password" "..."
// of omgevingsvariabele Tapo__Password.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Tuinverlichting.Tapo;
using Tuinverlichting.Tapo.Tpap;

var command = args.FirstOrDefault()?.ToLowerInvariant();
switch (command)
{
    case "discover":
        await Discovery.RunAsync(args.Length > 1 ? IPAddress.Parse(args[1]) : IPAddress.Broadcast, "discovery.json");
        return 0;
    case "selftest":
        return SelfTest.Run() ? 0 : 1;
    case "login":
        return await LoginAll(args[1..]);
    case "control" when args.Length > 1:
        return await ControlTest(args[1]);
    case "wave" when args.Length > 1:
        // wave <ip> [periode s=20] [interval ms=1000] [transition ms=interval] [duur s=40]
        {
            double period = args.Length > 2 ? double.Parse(args[2]) : 20;
            int interval = args.Length > 3 ? int.Parse(args[3]) : 1000;
            int transition = args.Length > 4 ? int.Parse(args[4]) : interval;
            double duration = args.Length > 5 ? double.Parse(args[5]) : 40;
            return await Wave(args[1], period, interval, transition, duration);
        }
    case "wavemark" when args.Length > 1:
        // wavemark <ip> [periode s=40] [interval ms=700] [duur s=120] [beide|tint|hel]  -- Enter = "stapje gezien"
        return await WaveMark(args[1],
            args.Length > 2 ? double.Parse(args[2]) : 40,
            args.Length > 3 ? int.Parse(args[3]) : 700,
            args.Length > 4 ? double.Parse(args[4]) : 120,
            args.Length > 5 ? args[5].ToLowerInvariant() : "beide");
    case "fade" when args.Length >= 5:
        // fade <ip> <hue 0-360> <saturation 0-100> <brightness 1-100> [transition]
        {
            var p = new JsonObject
            {
                ["hue"] = int.Parse(args[2]), ["saturation"] = int.Parse(args[3]),
                ["brightness"] = int.Parse(args[4]), ["color_temp"] = 0,
            };
            if (args.Length > 5) p["transition"] = int.Parse(args[5]);
            return await Raw(args[1], ["set_device_info", p.ToJsonString()]);
        }
    case "fadetest" when args.Length > 1:
        // fadetest <ip> [on_state.duration s=5]
        return await FadeTest(args[1], args.Length > 2 ? int.Parse(args[2]) : 5);
    case "raw" when args.Length > 2:
        return await Raw(args[1], args[2..]);
    case "connectall":
        return Password() is { } pw1 ? await Fence.ConnectAllCommand(pw1) : 1;
    case "fencewave":
        return Password() is { } pw2
            // fencewave [periode s=10] [duur s=90] [interval ms=700] [tint min=190] [tint max=240] [golflengte=10]
            ? await Fence.FenceWaveCommand(pw2,
                args.Length > 1 ? double.Parse(args[1]) : 10,
                args.Length > 2 ? double.Parse(args[2]) : 90,
                args.Length > 3 ? int.Parse(args[3]) : 700,
                args.Length > 4 ? double.Parse(args[4]) : 190,
                args.Length > 5 ? double.Parse(args[5]) : 240,
                args.Length > 6 ? double.Parse(args[6]) : 10)
            : 1;
    case "scenedemo":
        // scenedemo [deel s=60] [crossfade s=20]
        return Password() is { } pw3
            ? await Fence.SceneDemoCommand(pw3,
                args.Length > 1 ? double.Parse(args[1]) : 60,
                args.Length > 2 ? double.Parse(args[2]) : 20)
            : 1;
    case "scene" when args.Length > 1:
        // scene <golf-lantaarn|vuur|golf|lantaarnnacht> [duur s=120]  -- één scène, fadet in vanuit de huidige kleur (10 s)
        return Password() is { } pw4
            ? await Fence.SceneDemoCommand(pw4, args.Length > 2 ? double.Parse(args[2]) : 120, 10, args[1].ToLowerInvariant())
            : 1;
    default:
        Console.WriteLine("Gebruik: TapoProbe discover [broadcast] | selftest | login [ip ...] | control <ip>\n" +
                          "         | fade <ip> <hue> <sat> <bri> [transition] | wave <ip> [periode s] [interval ms] [transition ms] [duur s]\n" +
                          "         | raw <ip> <method> [json] [wait ms] ...\n" +
                          "         | wavemark <ip> [periode] [interval] [duur] [beide|tint|hel] | fadetest <ip> [duur]\n" +
                          "         | connectall | fencewave [periode s=10] [duur s=90] [interval ms=700] [tint min=190] [tint max=240] [golflengte=10]\n" +
                          "         | scenedemo [deel s=60] [crossfade s=20] | scene <golf-lantaarn|vuur|golf|lantaarnnacht> [duur s=120]");
        return 1;
}

// Blauwe golf (hue 190..235, helderheid 35..60) met de fade van de lamp zelf; zet daarna de oude stand terug.
static async Task<int> Wave(string ip, double periodS, int intervalMs, int transitionMs, double durationS)
{
    if (Password() is not { } password) return 1;
    using var client = new TpapClient(ip);
    await client.LoginAsync(password);
    var orig = (await client.RequestAsync("get_device_info"))["result"]!;
    Console.WriteLine($"Golf: periode {periodS} s, elke {intervalMs} ms een update met transition {transitionMs} ms, {durationS} s lang");

    var times = new List<long>();
    int skipped = 0;
    var clock = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        for (int i = 0; i * intervalMs < durationS * 1000; i++)
        {
            long due = (long)i * intervalMs;
            if (clock.ElapsedMilliseconds > due + intervalMs) { skipped++; continue; }   // achter: stap overslaan
            if (clock.ElapsedMilliseconds < due) await Task.Delay((int)(due - clock.ElapsedMilliseconds));

            // doel = waarde aan het eind van deze fade, zodat de lamp naar het juiste punt toe beweegt
            double t = (due + transitionMs) / 1000.0;
            double phase = 2 * Math.PI * t / periodS;
            int hue = (int)Math.Round(212.5 + 22.5 * Math.Sin(phase));
            int bri = (int)Math.Round(47.5 + 12.5 * Math.Sin(phase + 1));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await client.RequestAsync("set_device_info", new JsonObject
                { ["hue"] = hue, ["saturation"] = 70, ["brightness"] = bri, ["color_temp"] = 0, ["transition"] = transitionMs });
            times.Add(sw.ElapsedMilliseconds);
        }
    }
    finally
    {
        await client.RequestAsync("set_device_info", new JsonObject
        {
            ["hue"] = (int)orig["hue"]!, ["saturation"] = (int)orig["saturation"]!,
            ["brightness"] = (int)orig["brightness"]!, ["color_temp"] = (int)orig["color_temp"]!, ["transition"] = 2000,
        });
    }
    times.Sort();
    Console.WriteLine($"{times.Count} updates, {skipped} overgeslagen; mediaan {times[times.Count / 2]} ms, max {times[^1]} ms. Oude stand teruggezet.");
    return 0;
}

// Interactief: de gebruiker start elke wissel met Enter en drukt Enter zodra de kleur stilstaat;
// het programma meet de tijd. Zet daarna kleur en on_state.duration terug.
static async Task<int> FadeTest(string ip, int durationS)
{
    if (Password() is not { } password) return 1;
    using var client = new TpapClient(ip);
    await client.LoginAsync(password);

    var orig = (await client.RequestAsync("get_device_info"))["result"]!;
    var origGradual = (await client.RequestAsync("get_on_off_gradually_info"))["result"]!["on_state"]!;

    static JsonObject Color(int h, int s, int b) => new() { ["hue"] = h, ["saturation"] = s, ["brightness"] = b, ["color_temp"] = 0 };
    var tests = new (string Name, JsonObject From, JsonObject To)[]
    {
        ("blauw 200 -> paars 280   verz 80  hel 40", Color(200, 80, 40), Color(280, 80, 40)),
        ("rood 0    -> blauw 240   verz 80  hel 40", Color(0, 80, 40), Color(240, 80, 40)),
        ("blauw 200 -> paars 280   verz 100 hel 50", Color(200, 100, 50), Color(280, 100, 50)),
        ("rood 0    -> blauw 240   verz 100 hel 50", Color(0, 100, 50), Color(240, 100, 50)),
    };
    var results = new List<string>();

    // Ctrl+C: niet hard afbreken, maar ReadLine laten terugkeren zodat finally alles terugzet.
    bool cancelled = false;
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancelled = true; };
    void Wait(string prompt)
    {
        Console.Write(prompt);
        if (Console.ReadLine() is null || cancelled) throw new OperationCanceledException();
    }

    try
    {
        await client.RequestAsync("set_on_off_gradually_info",
            new JsonObject { ["on_state"] = new JsonObject { ["enable"] = true, ["duration"] = durationS } });

        Console.WriteLine($"""
            FADETEST  lamp {ip} ({Nick(orig)}), ingestelde fadeduur {durationS} s

            Lees dit eerst. Per test druk je 3 keer op Enter:
              1. Enter  -> lamp gaat naar de startkleur. Wacht tot die stilstaat.
              2. Enter  -> de wissel start. Blijf naar de lamp KIJKEN.
              3. Enter  -> zodra de kleur niet meer verandert. Dat is de gemeten tijd.
            Je kunt dus blijven kijken en gewoon 3x Enter tellen; niet op het scherm nodig.
            Ctrl+C stopt netjes en zet alles terug.

            """);
        Wait("Enter = beginnen ... ");

        for (int i = 0; i < tests.Length; i++)
        {
            var (name, from, to) = tests[i];
            Console.WriteLine($"\n--- Test {i + 1}/{tests.Length}: {name}");
            Wait("  [1] startkleur ");
            await client.RequestAsync("set_device_info", from);
            Wait("  [2] wissel     ");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await client.RequestAsync("set_device_info", to);
            Wait("  [3] stil?      ");
            var s = sw.Elapsed.TotalSeconds;
            Console.WriteLine($"  => {s:0.0} s");
            results.Add($"Test {i + 1}: {name}   {s:0.0} s");
        }
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\nAfgebroken.");
    }
    finally
    {
        await client.RequestAsync("set_device_info", new JsonObject
        {
            ["hue"] = (int)orig["hue"]!, ["saturation"] = (int)orig["saturation"]!,
            ["brightness"] = (int)orig["brightness"]!, ["color_temp"] = (int)orig["color_temp"]!,
        });
        await client.RequestAsync("set_on_off_gradually_info", new JsonObject
            { ["on_state"] = new JsonObject { ["enable"] = (bool)origGradual["enable"]!, ["duration"] = (int)origGradual["duration"]! } });
        Console.WriteLine($"Oude kleur en on_state.duration ({origGradual["duration"]} s) teruggezet.");
    }

    Console.WriteLine($"\nResultaat (on_state.duration = {durationS} s):");
    results.ForEach(Console.WriteLine);
    var log = Path.Combine(AppContext.BaseDirectory, "fadetest.log");
    File.AppendAllLines(log, [$"# {DateTime.Now:yyyy-MM-dd HH:mm} {ip} duration={durationS}", .. results]);
    Console.WriteLine($"(ook opgeslagen in {log})");
    return 0;
}

// Golf zoals `wave`, maar interactief: de gebruiker drukt Enter bij elk zichtbaar stapje.
// Logt elke update en elke markering naar wavemark.csv (naast de exe) voor analyse.
static async Task<int> WaveMark(string ip, double periodS, int intervalMs, double durationS, string mode)
{
    if (mode is not ("beide" or "tint" or "hel")) { Console.WriteLine("modus moet beide, tint of hel zijn"); return 1; }
    if (Password() is not { } password) return 1;
    using var client = new TpapClient(ip);
    await client.LoginAsync(password);
    var orig = (await client.RequestAsync("get_device_info"))["result"]!;

    Console.WriteLine($"""
        WAVEMARK  lamp {ip} ({Nick(orig)})
        Blauwe golf, modus {mode.ToUpperInvariant()}: {mode switch
        {
            "tint" => "alleen de TINT beweegt (190-235), helderheid vast 48%",
            "hel" => "alleen de HELDERHEID beweegt (35-60%), tint vast 212",
            _ => "tint (190-235) en helderheid (35-60%) bewegen allebei",
        }}
        Periode {periodS} s, elke {intervalMs} ms een update, {durationS} s lang.

        Lees dit eerst:
          - Na Enter begint de golf. Kijk naar de lamp.
          - Druk op Enter op het moment dat je een STAPJE ziet (zo vaak als nodig).
          - Je hoeft niet op het scherm te kijken. Na {durationS} s stopt de golf vanzelf
            en gaat de lamp terug naar de oude kleur.
          - Ctrl+C stopt eerder, ook netjes.

        """);
    Console.Write("Enter = golf starten ... ");
    if (Console.ReadLine() is null) return 1;

    var clock = System.Diagnostics.Stopwatch.StartNew();
    var marks = new System.Collections.Concurrent.ConcurrentQueue<long>();
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(durationS));
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };

    // Enter-lezer op de achtergrond
    _ = Task.Run(() =>
    {
        while (!stop.IsCancellationRequested && Console.ReadLine() is not null)
        {
            marks.Enqueue(clock.ElapsedMilliseconds);
            Console.WriteLine($"  stapje gemarkeerd op {clock.ElapsedMilliseconds / 1000.0:0.0} s");
        }
    });

    var rows = new List<string> { "kind,t_ms,hue,bri,hue_exact,bri_exact,latency_ms" };
    int skipped = 0;
    try
    {
        for (int i = 0; !stop.IsCancellationRequested; i++)
        {
            long due = (long)i * intervalMs;
            if (clock.ElapsedMilliseconds > due + intervalMs) { skipped++; rows.Add($"skip,{due},,,,,"); continue; }
            if (clock.ElapsedMilliseconds < due)
                try { await Task.Delay((int)(due - clock.ElapsedMilliseconds), stop.Token); } catch (OperationCanceledException) { break; }

            double t = due / 1000.0 + 1.0;            // doel = waar de golf over ~1 s (fadeduur) moet zijn
            double phase = 2 * Math.PI * t / periodS;
            double hueX = mode == "hel" ? 212 : 212.5 + 22.5 * Math.Sin(phase);
            double briX = mode == "tint" ? 48 : 47.5 + 12.5 * Math.Sin(phase + 1);
            int hue = (int)Math.Round(hueX), bri = (int)Math.Round(briX);

            long sent = clock.ElapsedMilliseconds;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await client.RequestAsync("set_device_info", new JsonObject
                { ["hue"] = hue, ["saturation"] = 70, ["brightness"] = bri, ["color_temp"] = 0 });
            rows.Add(FormattableString.Invariant($"update,{sent},{hue},{bri},{hueX:0.00},{briX:0.00},{sw.ElapsedMilliseconds}"));
        }
    }
    finally
    {
        await client.RequestAsync("set_device_info", new JsonObject
        {
            ["hue"] = (int)orig["hue"]!, ["saturation"] = (int)orig["saturation"]!,
            ["brightness"] = (int)orig["brightness"]!, ["color_temp"] = (int)orig["color_temp"]!,
        });
    }

    foreach (var m in marks) rows.Add($"mark,{m},,,,,");
    var file = Path.Combine(AppContext.BaseDirectory, $"wavemark-{mode}-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
    File.WriteAllLines(file, rows);
    Console.WriteLine($"\nKlaar: {rows.Count(r => r.StartsWith("update"))} updates, {skipped} overgeslagen, {marks.Count} stapjes gemarkeerd.");
    Console.WriteLine($"Oude kleur teruggezet. Log: {file}");
    return 0;
}

// raw <ip> <method> [params-json] [<method> [params-json] ...] [wait <ms>] : losse requests binnen één sessie, voor experimenten
static async Task<int> Raw(string ip, string[] steps)
{
    if (Password() is not { } password) return 1;
    using var client = new TpapClient(ip);
    await client.LoginAsync(password);
    for (int i = 0; i < steps.Length; i++)
    {
        if (steps[i] == "wait") { await Task.Delay(int.Parse(steps[++i])); continue; }
        var method = steps[i];
        JsonNode? p = i + 1 < steps.Length && steps[i + 1].StartsWith('{') ? JsonNode.Parse(steps[++i]) : null;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = await client.RequestAsync(method, p);
            Console.WriteLine($"{method} {p?.ToJsonString()} ({sw.ElapsedMilliseconds} ms) -> {res.ToJsonString()}");
        }
        catch (TapoException e)
        {
            Console.WriteLine($"{method} {p?.ToJsonString()} -> FOUT {e.Message}");
        }
    }
    return 0;
}

static string? Password()
{
    var config = new ConfigurationBuilder()
        .AddUserSecrets(typeof(Fence).Assembly, optional: true)
        .AddEnvironmentVariables()
        .Build();
    var password = config["Tapo:Password"];
    if (string.IsNullOrEmpty(password))
        Console.WriteLine("Geen wachtwoord gevonden. Zet het eenmalig met:\n" +
                          "  dotnet user-secrets set \"Tapo:Password\" \"<je Tapo-wachtwoord>\"");
    return string.IsNullOrEmpty(password) ? null : password;
}

// Test aansturing op één lamp: kleur zetten, uitlezen, korte 2 Hz-golf met tijdmeting, en
// daarna de oorspronkelijke stand terugzetten.
static async Task<int> ControlTest(string ip)
{
    if (Password() is not { } password) return 1;
    using var client = new TpapClient(ip);
    await client.LoginAsync(password);

    var orig = (await client.RequestAsync("get_device_info"))["result"]!;
    Console.WriteLine($"Start:   aan={orig["device_on"]} bri={orig["brightness"]} hue={orig["hue"]} sat={orig["saturation"]} ct={orig["color_temp"]}");

    try
    {
        // 1. Eén kleur zetten en teruglezen
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await client.RequestAsync("set_device_info", new JsonObject
            { ["device_on"] = true, ["hue"] = 210, ["saturation"] = 70, ["brightness"] = 45, ["color_temp"] = 0 });
        Console.WriteLine($"Blauw gezet in {sw.ElapsedMilliseconds} ms");
        var now = (await client.RequestAsync("get_device_info"))["result"]!;
        Console.WriteLine($"Gelezen: bri={now["brightness"]} hue={now["hue"]} sat={now["saturation"]} ct={now["color_temp"]}");
        await Task.Delay(3000);

        // 2. Golf: 20 stappen op 2 Hz, hue 190..235 en helderheid 35..60
        Console.WriteLine("Golf 10 s op 2 Hz ...");
        var times = new List<long>();
        var tick = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 20; i++)
        {
            double phase = i / 20.0 * 2 * Math.PI;
            int hue = (int)Math.Round(212.5 + 22.5 * Math.Sin(phase));
            int bri = (int)Math.Round(47.5 + 12.5 * Math.Sin(phase + 1));
            sw.Restart();
            await client.RequestAsync("set_device_info", new JsonObject
                { ["hue"] = hue, ["saturation"] = 70, ["brightness"] = bri, ["color_temp"] = 0 });
            times.Add(sw.ElapsedMilliseconds);
            var wait = (i + 1) * 500 - tick.ElapsedMilliseconds;
            if (wait > 0) await Task.Delay((int)wait);
        }
        times.Sort();
        Console.WriteLine($"set_device_info: min {times[0]} ms, mediaan {times[times.Count / 2]} ms, max {times[^1]} ms");
    }
    finally
    {
        // 3. Terugzetten
        var restore = new JsonObject { ["brightness"] = (int)orig["brightness"]! };
        if ((int)orig["color_temp"]! > 0) restore["color_temp"] = (int)orig["color_temp"]!;
        else { restore["hue"] = (int)orig["hue"]!; restore["saturation"] = (int)orig["saturation"]!; restore["color_temp"] = 0; }
        await client.RequestAsync("set_device_info", restore);
        if (!(bool)orig["device_on"]!) await client.RequestAsync("set_device_info", new JsonObject { ["device_on"] = false });
        var back = (await client.RequestAsync("get_device_info"))["result"]!;
        Console.WriteLine($"Hersteld: aan={back["device_on"]} bri={back["brightness"]} hue={back["hue"]} sat={back["saturation"]} ct={back["color_temp"]}");
    }
    return 0;
}

static async Task<int> LoginAll(string[] ips)
{
    if (Password() is not { } password) return 1;

    if (ips.Length == 0)
    {
        if (!File.Exists("discovery.json"))
        {
            Console.WriteLine("Geen discovery.json; draai eerst 'TapoProbe discover'.");
            return 1;
        }
        var discovery = JsonNode.Parse(File.ReadAllText("discovery.json"))!.AsObject();
        ips = discovery
            .Where(kv => (string?)kv.Value?["result"]?["mgt_encrypt_schm"]?["encrypt_type"] == "TPAP"
                         && ((string?)kv.Value?["result"]?["device_type"])?.EndsWith("BULB") == true)
            .Select(kv => kv.Key)
            .ToArray();
    }

    Directory.CreateDirectory("devices");
    var json = new JsonSerializerOptions { WriteIndented = true };
    int ok = 0;

    foreach (var ip in ips)
    {
        Console.Write($"{ip,-15} ");
        try
        {
            using var client = new TpapClient(ip);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await client.LoginAsync(password);
            var loginMs = sw.ElapsedMilliseconds;

            var components = await client.RequestAsync("component_nego");
            var info = await client.RequestAsync("get_device_info");

            File.WriteAllText(Path.Combine("devices", $"{ip}.json"),
                new JsonObject { ["component_nego"] = components.DeepClone(), ["get_device_info"] = info.DeepClone() }
                    .ToJsonString(json));

            var r = info["result"]!;
            Console.WriteLine(
                $"{Nick(r),-12} {r["model"],-6} fw {r["fw_ver"],-26} login {loginMs,4} ms  " +
                $"aan={r["device_on"]} bri={r["brightness"]} hue={r["hue"]} sat={r["saturation"]} ct={r["color_temp"]}");
            ok++;
        }
        catch (TapoException e) when (e.IsCredentialProblem)
        {
            Console.WriteLine($"FOUT: {e.Message}");
            Console.WriteLine("Gestopt om te voorkomen dat de lampen geblokkeerd raken. Controleer het wachtwoord.");
            return 1;
        }
        catch (Exception e) when (e is TapoException or HttpRequestException or TaskCanceledException)
        {
            Console.WriteLine($"FOUT: {e.Message}");
        }
    }

    Console.WriteLine($"\n{ok}/{ips.Length} gelukt. Details in {Path.GetFullPath("devices")}");
    return ok == ips.Length ? 0 : 1;
}

// nickname staat base64-gecodeerd in get_device_info
static string Nick(JsonNode r)
{
    var s = (string?)r["nickname"];
    if (s is null) return "?";
    try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); } catch (FormatException) { return s; }
}
