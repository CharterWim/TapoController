// Effecten zijn pure functies van (lamp, tijd) -> HSV. Geen toestand, dus makkelijk te testen,
// te crossfaden en (later) als JSON op te slaan voor de portal.
//
// Ontwerpregel (gemeten op L630, zie CLAUDE.md): per lamp de tint niet sneller dan ~5°/s laten veranderen;
// snelle beweging langs de schutting maak je met een lange golflengte, niet met een korte periode.

using System.Text.Json.Serialization;
using Tuinverlichting.Tapo;

namespace Tuinverlichting.Scenes;

/// Waar een lamp staat, zoals een effect hem ziet.
public readonly record struct LampPoint(string Name, double Pos, double LanternPos);

public enum WaveDirection
{
    /// L01 -> L13
    Forward,
    /// L13 -> L01
    Backward,
    /// Vanuit de lantaarnpaal (midden achterin) naar beide kanten
    FromLantern,
    /// Van beide uiteinden naar de lantaarnpaal toe
    ToLantern,
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(WaveEffect), "golf")]
[JsonDerivedType(typeof(FireEffect), "vuur")]
[JsonDerivedType(typeof(StaticEffect), "vast")]
public abstract record Effect
{
    public abstract Hsv Evaluate(LampPoint lamp, double t);
}

/// Vaste kleur.
public sealed record StaticEffect(double Hue, double Saturation, double Brightness) : Effect
{
    public override Hsv Evaluate(LampPoint lamp, double t) => new(Hue, Saturation, Brightness);
}

/// Twee trage sinussen (tint en helderheid, met verschillende snelheid) die langs de schutting lopen.
public sealed record WaveEffect : Effect
{
    public double HueMin { get; init; } = 190;
    public double HueMax { get; init; } = 240;
    public double Saturation { get; init; } = 70;
    public double BrightnessMin { get; init; } = 35;
    public double BrightnessMax { get; init; } = 60;
    /// Seconden per golf op één lamp. Bepaalt hoe snel één lamp verandert.
    public double PeriodS { get; init; } = 30;
    /// Lengte van één golf in lampplaatsen. Loopsnelheid langs de schutting = Wavelength / PeriodS.
    public double Wavelength { get; init; } = 30;
    public WaveDirection Direction { get; init; } = WaveDirection.Forward;

    public override Hsv Evaluate(LampPoint lamp, double t)
    {
        double x = Direction switch
        {
            WaveDirection.Forward => lamp.Pos,
            WaveDirection.Backward => -lamp.Pos,
            WaveDirection.FromLantern => Math.Abs(lamp.Pos - lamp.LanternPos),
            WaveDirection.ToLantern => -Math.Abs(lamp.Pos - lamp.LanternPos),
            _ => lamp.Pos,
        };
        double ph1 = 2 * Math.PI * (t / PeriodS - x / Wavelength);
        double ph2 = 2 * Math.PI * (t / (PeriodS * 1.37) - x / (Wavelength * 0.8)) + 1.0;
        double hue = (HueMin + HueMax) / 2 + (HueMax - HueMin) / 2 * Math.Sin(ph1);
        double bri = (BrightnessMin + BrightnessMax) / 2 + (BrightnessMax - BrightnessMin) / 2 * Math.Sin(ph2);
        return new Hsv(hue, Saturation, bri);
    }
}

/// Smeulend vuur: trage ruis op tint en helderheid, deels gedeeld tussen buren. Geen snel flikkeren.
public sealed record FireEffect : Effect
{
    public double HueMin { get; init; } = 8;
    public double HueMax { get; init; } = 35;
    public double Saturation { get; init; } = 95;
    public double BrightnessMin { get; init; } = 25;
    public double BrightnessMax { get; init; } = 55;
    /// Seconden tussen twee ruis-roosterpunten in de tijd: groter = trager.
    public double TimeScaleS { get; init; } = 8;
    /// Hoeveel ruisroosterpunten per lampplaats: kleiner = buren lijken meer op elkaar.
    public double SpatialScale { get; init; } = 0.35;
    public int Seed { get; init; } = 1;

    public override Hsv Evaluate(LampPoint lamp, double t)
    {
        double x = lamp.Pos * SpatialScale;
        double u = t / TimeScaleS;
        // twee octaven; helderheid met andere seed en iets sneller, zodat tint en helderheid niet gelijk lopen
        double nh = 0.7 * Noise.Value2D(x, u, Seed) + 0.3 * Noise.Value2D(2 * x, 2 * u, Seed + 101);
        double nb = 0.7 * Noise.Value2D(x, u * 1.3, Seed + 7) + 0.3 * Noise.Value2D(2 * x, 2.6 * u, Seed + 211);
        return new Hsv(HueMin + (HueMax - HueMin) * nh, Saturation, BrightnessMin + (BrightnessMax - BrightnessMin) * nb);
    }
}

/// Deterministische value noise in 2D, uitkomst 0..1, vloeiend (smoothstep-interpolatie).
static class Noise
{
    public static double Value2D(double x, double y, int seed)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = Smooth(x - x0), fy = Smooth(y - y0);
        double a = Hash(x0, y0, seed), b = Hash(x0 + 1, y0, seed);
        double c = Hash(x0, y0 + 1, seed), d = Hash(x0 + 1, y0 + 1, seed);
        return Lerp(Lerp(a, b, fx), Lerp(c, d, fx), fy);
    }

    static double Smooth(double f) => f * f * (3 - 2 * f);
    static double Lerp(double a, double b, double f) => a + (b - a) * f;

    static double Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1274126177);
            h = (h ^ (h >> 13)) * 1274126177;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / (double)0xFFFFFF;
        }
    }
}
