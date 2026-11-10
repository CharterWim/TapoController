using System.Text.Json;

namespace Tuinverlichting.Scenes;

/// Eén lamp uit lamps.json. Pos = positie langs de schutting (voorlopig lampplaatsen, later meters).
public sealed record LampConfig(string Name, string Ip, string Zone, double Pos, int MaxBrightness = 100);

/// Inhoud van lamps.json.
public sealed record GardenConfig(LampConfig[] Lamps, LampConfig[] Lanterns)
{
    /// Positie van de lantaarnpaal: middelpunt voor gespiegelde effecten.
    public double LanternPos => Lanterns.Length > 0 ? Lanterns.Average(l => l.Pos) : Lamps.Average(l => l.Pos);

    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static GardenConfig Load(string path) =>
        JsonSerializer.Deserialize<GardenConfig>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException($"{path} is leeg");

    /// lamps.json zoeken vanaf de huidige map omhoog, dan vanaf de map van de exe omhoog.
    public static GardenConfig Find()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var f = Path.Combine(dir.FullName, "lamps.json");
                if (File.Exists(f)) return Load(f);
            }
        throw new FileNotFoundException("lamps.json niet gevonden");
    }
}
