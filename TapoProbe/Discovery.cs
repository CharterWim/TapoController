// UDP-discovery van Tapo-apparaten op poort 20002.
// Logt per apparaat model, type, MAC en encryptieschema (KLAP/AES/TPAP).

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

static class Discovery
{
    const int Port = 20002;

    public static async Task RunAsync(IPAddress broadcast, string outFile)
    {
        var target = new IPEndPoint(broadcast, Port);
        byte[] query = BuildQuery();

        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        Console.WriteLine($"Discovery naar {target} ...\n");

        for (int i = 0; i < 3; i++)
        {
            await udp.SendAsync(query, target);
            await Task.Delay(200);
        }

        var found = new SortedDictionary<string, JsonNode>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        try
        {
            while (true)
            {
                var r = await udp.ReceiveAsync(cts.Token);
                var ip = r.RemoteEndPoint.Address.ToString();
                if (r.Buffer.Length <= 16 || found.ContainsKey(ip)) continue;

                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(Encoding.UTF8.GetString(r.Buffer, 16, r.Buffer.Length - 16));
                }
                catch (JsonException)
                {
                    Console.WriteLine($"{ip,-15} onleesbaar antwoord ({r.Buffer.Length} bytes)");
                    continue;
                }
                if (node is null) continue;
                found[ip] = node;

                var res = node["result"];
                var schm = res?["mgt_encrypt_schm"];
                Console.WriteLine(
                    $"{ip,-15} {res?["device_model"],-12} {res?["device_type"],-18} {res?["mac"],-18} " +
                    $"encrypt={schm?["encrypt_type"]} https={schm?["is_support_https"]} port={schm?["http_port"]} lv={schm?["lv"]}");
            }
        }
        catch (OperationCanceledException) { }

        Console.WriteLine($"\n{found.Count} apparaat/apparaten gevonden.");

        var all = new JsonObject();
        foreach (var (ip, node) in found) all[ip] = node;
        var file = Path.GetFullPath(outFile);
        File.WriteAllText(file, all.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Volledige antwoorden opgeslagen in {file}");
    }

    // --- Discovery-pakket: 16-byte header + JSON met RSA public key ---
    static byte[] BuildQuery()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportSubjectPublicKeyInfoPem() + "\n";
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new { @params = new { rsa_key = pem } },
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        var packet = new byte[16 + payload.Length];
        var h = packet.AsSpan();
        h[0] = 2;                                                         // version
        h[1] = 0;                                                         // msg type
        BinaryPrimitives.WriteUInt16BigEndian(h[2..], 1);                 // op code
        BinaryPrimitives.WriteUInt16BigEndian(h[4..], (ushort)payload.Length);
        h[6] = 17;                                                        // flags
        h[7] = 0;                                                         // padding
        RandomNumberGenerator.Fill(h.Slice(8, 4));                        // serial
        BinaryPrimitives.WriteUInt32BigEndian(h[12..], 0x5A6B7C8D);       // CRC-placeholder
        payload.CopyTo(packet, 16);

        BinaryPrimitives.WriteUInt32BigEndian(h[12..], Crc32(packet));    // echte CRC over geheel
        return packet;
    }

    static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }
}
