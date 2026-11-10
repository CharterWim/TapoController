// Eén Tapo-lamp met een blijvende TPAP-sessie.
// - Eén request tegelijk per lamp (geen parallelle requests naar dezelfde lamp).
// - Automatisch opnieuw inloggen als de lamp de sessie niet meer kent (één keer per request).
// - Submit(): "nieuwste wint" voor animaties. Loopt de lamp achter, dan worden tussenliggende kleuren
//   overgeslagen in plaats van opgestapeld; kleuren die na afronding gelijk zijn worden niet verstuurd.
// Let op: de L630 fadet zelf ~1–1,5 s per kleurwissel (niet instelbaar); stuur animaties ~elke 0,7 s.

using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Tuinverlichting.Tapo.Tpap;

namespace Tuinverlichting.Tapo;

public sealed class TapoLamp : IAsyncDisposable
{
    readonly string _password;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly CancellationTokenSource _disposed = new();
    readonly object _lock = new();
    TpapClient? _client;
    Hsv? _pending;
    Hsv? _lastSent;
    bool _pumping;
    Task _pump = Task.CompletedTask;
    DateTime _offlineUntil;

    public TapoLamp(string ip, string password, string? name = null)
    {
        Ip = ip;
        _password = password;
        Name = name ?? ip;
    }

    /// Time-out per HTTP-request (ook login). Kort houden: een vastgelopen lamp mag een animatie niet ophouden.
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// Hoe lang Submit() een onbereikbare lamp overslaat voordat hij het opnieuw probeert.
    public TimeSpan OfflineBackoff { get; init; } = TimeSpan.FromSeconds(20);

    /// True na een verbindingsfout in Submit(), tot OfflineBackoff verstreken is.
    public bool IsOffline => DateTime.UtcNow < _offlineUntil;

    public string Ip { get; }
    /// Naam uit de configuratie, of na ConnectAsync de nickname uit de lamp.
    public string Name { get; private set; }
    public string? Model { get; private set; }

    /// Statistiek voor diagnose.
    public LampStats Stats { get; } = new();

    /// Fouten uit Submit() (die zelf niet gooit). Na een fout probeert de volgende Submit gewoon opnieuw.
    public event Action<TapoLamp, Exception>? Error;

    /// Inloggen en apparaatinfo lezen. Geeft get_device_info-result terug.
    public async Task<JsonNode> ConnectAsync(CancellationToken ct = default)
    {
        var info = await GetDeviceInfoAsync(ct);
        Model = (string?)info["model"];
        if (Name == Ip && (string?)info["nickname"] is { } nick)
        {
            try { Name = Encoding.UTF8.GetString(Convert.FromBase64String(nick)); } catch (FormatException) { Name = nick; }
        }
        return info;
    }

    public async Task<JsonNode> GetDeviceInfoAsync(CancellationToken ct = default) =>
        (await RequestAsync("get_device_info", null, ct))["result"]!;

    /// Zet direct een kleur (wacht op de lamp). Voor animaties: gebruik Submit.
    public Task SetColorAsync(Hsv color, CancellationToken ct = default) =>
        RequestAsync("set_device_info", ColorParams(color), ct);

    public Task SetColorTemperatureAsync(int kelvin, int brightness, CancellationToken ct = default) =>
        RequestAsync("set_device_info", new JsonObject { ["color_temp"] = kelvin, ["brightness"] = Math.Clamp(brightness, 1, 100) }, ct);

    public Task SetOnAsync(bool on, CancellationToken ct = default) =>
        RequestAsync("set_device_info", new JsonObject { ["device_on"] = on }, ct);

    /// Willekeurig request binnen de sessie, met automatisch opnieuw inloggen. Geeft het volledige antwoord.
    public async Task<JsonNode> RequestAsync(string method, JsonNode? @params = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            bool fresh = false;
            if (_client is null) { await LoginAsync(ct); fresh = true; }
            try
            {
                return await _client!.RequestAsync(method, @params?.DeepClone(), ct);
            }
            catch (Exception e) when (!fresh && IsRecoverable(e, ct))
            {
                // Sessie verlopen of verbinding weg: één keer opnieuw inloggen en herhalen.
                Stats.Relogins++;
                await LoginAsync(ct);
                return await _client!.RequestAsync(method, @params?.DeepClone(), ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// Animatie-API: zet de gewenste kleur; keert direct terug. Nieuwste wint, niets stapelt op.
    public void Submit(Hsv color)
    {
        lock (_lock)
        {
            if (_pending is not null) Stats.Coalesced++;
            _pending = color;
            if (_pumping) return;
            _pumping = true;
        }
        _pump = Task.Run(PumpAsync);
    }

    /// Wacht tot de laatst ingediende kleur verstuurd is (of mislukt).
    public Task FlushAsync() => _pump;

    async Task PumpAsync()
    {
        while (true)
        {
            Hsv next;
            lock (_lock)
            {
                if (_pending is null || _disposed.IsCancellationRequested) { _pumping = false; return; }
                next = _pending.Value;
                _pending = null;
            }

            if (_lastSent is { } last && last.SameOnLamp(next)) { Stats.Unchanged++; continue; }
            if (IsOffline) { Stats.SkippedOffline++; continue; }

            var sw = Stopwatch.StartNew();
            try
            {
                await SetColorAsync(next, _disposed.Token);
                _lastSent = next;
                Stats.Record(sw.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (_disposed.IsCancellationRequested) { }
            catch (Exception e)
            {
                Stats.Errors++;
                _lastSent = null;              // na een fout altijd opnieuw sturen
                if (IsRecoverable(e, CancellationToken.None))
                {
                    // Ook na opnieuw inloggen niet bereikbaar: even met rust laten i.p.v. steeds op time-outs wachten.
                    _offlineUntil = DateTime.UtcNow + OfflineBackoff;
                    Stats.OfflinePeriods++;
                }
                Error?.Invoke(this, e);
            }
        }
    }

    async Task LoginAsync(CancellationToken ct)
    {
        _client?.Dispose();
        _client = null;
        var client = new TpapClient(Ip, timeout: RequestTimeout);
        try
        {
            await client.LoginAsync(_password, ct);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        _client = client;
        Stats.Logins++;
    }

    static bool IsRecoverable(Exception e, CancellationToken ct) => e switch
    {
        TapoException t => t.IsSessionInvalid,
        HttpRequestException => true,
        TaskCanceledException => !ct.IsCancellationRequested,   // HttpClient-timeout
        _ => false,
    };

    static JsonObject ColorParams(Hsv c) =>
        new() { ["hue"] = c.H, ["saturation"] = c.S, ["brightness"] = c.B, ["color_temp"] = 0 };

    public async ValueTask DisposeAsync()
    {
        _disposed.Cancel();
        try { await _pump; } catch { /* al gemeld via Error */ }
        _client?.Dispose();
        _gate.Dispose();
        _disposed.Dispose();
    }
}

public sealed class LampStats
{
    readonly object _lock = new();
    readonly List<long> _latencies = [];

    public int Sent { get; private set; }
    public int Coalesced { get; internal set; }
    public int Unchanged { get; internal set; }
    public int Errors { get; internal set; }
    public int Logins { get; internal set; }
    public int Relogins { get; internal set; }
    /// Kleuren niet verstuurd omdat de lamp als onbereikbaar gemarkeerd stond.
    public int SkippedOffline { get; internal set; }
    /// Aantal keer dat de lamp als onbereikbaar gemarkeerd werd.
    public int OfflinePeriods { get; internal set; }

    internal void Record(long ms)
    {
        lock (_lock) { Sent++; _latencies.Add(ms); }
    }

    public (long Median, long Max) Latency()
    {
        lock (_lock)
        {
            if (_latencies.Count == 0) return (0, 0);
            var s = _latencies.Order().ToArray();
            return (s[s.Length / 2], s[^1]);
        }
    }

    public override string ToString()
    {
        var (med, max) = Latency();
        var offline = OfflinePeriods > 0 ? $", onbereikbaar {OfflinePeriods}x ({SkippedOffline} kleuren overgeslagen)" : "";
        return $"verstuurd {Sent}, overgeslagen {Coalesced}, ongewijzigd {Unchanged}, fouten {Errors}{offline}, " +
               $"logins {Logins} (her: {Relogins}), latency mediaan {med} / max {max} ms";
    }
}
