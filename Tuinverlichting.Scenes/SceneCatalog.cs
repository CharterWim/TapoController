using Tuinverlichting.Tapo;

namespace Tuinverlichting.Scenes;

/// Een benoemde scène. UsesLanterns = de lantaarns krijgen in deze scène een eigen rol;
/// anders blijven ze op hun basisstand en stuurt de engine ze niet aan.
public sealed record SceneDefinition(string Key, string Name, Effect Effect, bool UsesLanterns = false);

/// De beschikbare scènes (later uit JSON). Gedeeld door TapoProbe en de host.
public static class SceneCatalog
{
    public static IReadOnlyList<SceneDefinition> All { get; } =
    [
        new("golf-lantaarn", "Golf vanuit de lantaarn (blauw)",
            new WaveEffect { Direction = WaveDirection.FromLantern, PeriodS = 30, Wavelength = 20 }),
        new("vuur", "Smeulend vuur (rood/oranje)", new FireEffect()),
        new("golf", "Golf L01 -> L13 (blauw, lang verloop)",
            new WaveEffect { Direction = WaveDirection.Forward, PeriodS = 30, Wavelength = 30 }),
        new("lantaarnnacht", "Lantaarnnacht (maanlicht + ademende lantaarn)", new LanternNightEffect(), UsesLanterns: true),
        new("looplicht", "Looplicht (rode kop heen en weer)", new ScannerEffect()),
    ];

    public static SceneDefinition? Find(string key) =>
        All.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
}

/// Vaste kleur per lamp (op naam); lampen die er niet in staan blijven op Fallback.
/// Gebruikt om bij "stop" terug te faden naar de basisstand. Niet als JSON-scène bedoeld.
public sealed record PerLampStaticEffect(IReadOnlyDictionary<string, Hsv> Colors, Hsv Fallback) : Effect
{
    public override Hsv Evaluate(LampPoint lamp, double t) => Colors.TryGetValue(lamp.Name, out var c) ? c : Fallback;
}
