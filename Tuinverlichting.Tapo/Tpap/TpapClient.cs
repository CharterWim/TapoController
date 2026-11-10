// TPAP-client voor Tapo-lampen in "tls=0, pake=[2]"-modus (HTTP op poort 80, accountwachtwoord).
// Handshake: login/pake_register -> SPAKE2+ -> login/pake_share -> stok + start_seq,
// daarna versleutelde requests via POST /stok=<token>/ds.

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tuinverlichting.Tapo.Tpap;

/// Laag-niveau TPAP-verbinding met één lamp. Niet thread-safe: gebruik TapoLamp voor gedeeld gebruik.
sealed class TpapClient : IDisposable
{
    const int CipherSuite = 1;
    const string Encryption = "aes_128_ccm";
    const int MaxIterations = 100_000;

    readonly HttpClient _http;
    readonly string _baseUrl;
    TpapCipher? _cipher;
    string? _dataUrl;

    public TpapClient(string ip, int port = 80, TimeSpan? timeout = null)
    {
        _baseUrl = $"http://{ip}:{port}";
        _http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(10) };
    }

    public async Task LoginAsync(string password, CancellationToken ct = default)
    {
        var userRandom = RandomNumberGenerator.GetBytes(32);

        // 1. pake_register
        var reg = await LoginRequestAsync("pake_register", new JsonObject
        {
            ["username"] = Convert.ToHexStringLower(MD5.HashData("admin"u8)),   // altijd "admin", nooit het e-mailadres
            ["user_random"] = Convert.ToBase64String(userRandom),
            ["cipher_suites"] = new JsonArray(CipherSuite),
            ["encryption"] = new JsonArray(Encryption),
            ["passcode_type"] = "userpw",
        }, ct);

        if ((int?)reg["cipher_suites"] != CipherSuite)
            throw new TapoException($"Apparaat koos cipher suite {reg["cipher_suites"]}, niet ondersteund");
        if ((string?)reg["encryption"] != Encryption)
            throw new TapoException($"Apparaat koos encryptie {reg["encryption"]}, niet ondersteund");
        int iterations = (int?)reg["iterations"] ?? 0;
        if (iterations is < 1 or > MaxIterations)
            throw new TapoException($"Apparaat vraagt {iterations} PBKDF2-iteraties, buiten 1..{MaxIterations}");

        var deviceRandom = Convert.FromBase64String((string)reg["dev_random"]!);
        var salt = Convert.FromBase64String((string)reg["dev_salt"]!);
        var deviceShare = Convert.FromBase64String((string)reg["dev_share"]!);
        if (deviceRandom.Length != 32)
            throw new TapoException($"dev_random is {deviceRandom.Length} bytes i.p.v. 32");

        var credential = Credential(password, reg["extra_crypt"]);

        // 2. SPAKE2+ (PBKDF2 kost even CPU, dus van de async-thread af)
        var proof = await Task.Run(() => Spake2Plus.Prove(credential, salt, iterations, userRandom, deviceRandom, deviceShare), ct);

        // 3. pake_share
        var share = await LoginRequestAsync("pake_share", new JsonObject
        {
            ["user_share"] = Convert.ToBase64String(proof.Share),
            ["user_confirm"] = Convert.ToBase64String(proof.Confirm),
        }, ct);

        var devConfirm = Convert.FromBase64String((string)share["dev_confirm"]!);
        if (!proof.Verify(devConfirm))
            throw new TapoException("dev_confirm klopt niet: apparaat bewees niet dat het het wachtwoord kent");

        _cipher?.Dispose();
        _cipher = new TpapCipher(proof.SharedKey, (int)share["start_seq"]!);
        _dataUrl = $"{_baseUrl}/stok={(string)share["stok"]!}/ds";
    }

    /// Stuurt een versleuteld request en geeft het volledige (ontsleutelde) antwoord terug.
    public async Task<JsonNode> RequestAsync(string method, JsonNode? @params = null, CancellationToken ct = default)
    {
        if (_cipher is null || _dataUrl is null) throw new InvalidOperationException("Eerst inloggen");

        var request = new JsonObject { ["method"] = method, ["params"] = @params };
        var (body, seq) = _cipher.Encrypt(request.ToJsonString());

        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await _http.PostAsync(_dataUrl, content, ct);
        if (!response.IsSuccessStatusCode)
            throw new TapoException($"{method}: HTTP {(int)response.StatusCode}", sessionInvalid: true);

        var responseBody = await response.Content.ReadAsByteArrayAsync(ct);
        string json;
        try
        {
            json = _cipher.Decrypt(seq, responseBody);
        }
        catch (Exception e) when (e is CryptographicException or TapoException)
        {
            // Een apparaat dat de sessie niet meer kent antwoordt met platte JSON + error_code
            // (op dit pad betekent ook -2203 géén verkeerd wachtwoord).
            var plain = TryParse(responseBody);
            if (plain?["error_code"] is { } ec)
                throw new TapoException($"{method}: sessie ongeldig (error_code {ec})", (int)ec, sessionInvalid: true);
            throw new TapoException($"{method}: antwoord niet te ontsleutelen: {e.Message}", sessionInvalid: true);
        }

        var node = JsonNode.Parse(json) ?? throw new TapoException($"{method}: leeg antwoord");
        int errorCode = (int?)node["error_code"] ?? 0;
        if (errorCode != 0)
            throw new TapoException($"{method}: error_code {errorCode}", errorCode);
        return node;
    }

    async Task<JsonNode> LoginRequestAsync(string subMethod, JsonObject @params, CancellationToken ct)
    {
        @params.Insert(0, "sub_method", subMethod);
        var body = new JsonObject { ["method"] = "login", ["params"] = @params };

        // Geen PostAsJsonAsync: die streamt chunked, en de lampserver ("SHIP 2.0") snapt dat niet
        // (antwoordt dan met een HTML-pagina). ByteArrayContent geeft een gewone Content-Length.
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body.ToJsonString()));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _http.PostAsync($"{_baseUrl}/", content, ct);
        if (!response.IsSuccessStatusCode)
            throw new TapoException($"{subMethod}: HTTP {(int)response.StatusCode}");

        var raw = await response.Content.ReadAsByteArrayAsync(ct);
        var node = TryParse(raw)
                   ?? throw new TapoException($"{subMethod}: geen JSON terug: {Encoding.UTF8.GetString(raw.AsSpan(0, Math.Min(raw.Length, 120)))}");
        int errorCode = (int?)node["error_code"] ?? 0;
        if (errorCode != 0)
            throw new TapoException($"{subMethod}: error_code {errorCode}{Hint(errorCode)}", errorCode);
        return node["result"] ?? throw new TapoException($"{subMethod}: geen result");
    }

    static string Hint(int errorCode) => errorCode switch
    {
        -2101 => " (te veel mislukte logins, lamp tijdelijk geblokkeerd)",
        -1501 or -2203 or -40401 => " (verkeerd wachtwoord?)",
        _ => "",
    };

    /// Wat er in plaats van het wachtwoord in SPAKE2+ gaat, afhankelijk van extra_crypt.
    public static string Credential(string password, JsonNode? extraCrypt)
    {
        if (extraCrypt is null)
            throw new TapoException("Apparaat noemt geen extra_crypt; niet ondersteund");

        var type = (string?)extraCrypt["type"];
        var id = (int?)extraCrypt["params"]?["passwd_id"];
        return (type, id) switch
        {
            ("password_shadow", 2) => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(password))),
            ("password_shadow", 4) => password,
            _ => throw new TapoException($"Onbekende wachtwoordtransformatie: {extraCrypt.ToJsonString()}"),
        };
    }

    static JsonNode? TryParse(byte[] body)
    {
        try { return JsonNode.Parse(body); } catch (JsonException) { return null; }
    }

    public void Dispose()
    {
        _cipher?.Dispose();
        _http.Dispose();
    }
}
