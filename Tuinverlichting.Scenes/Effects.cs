// Effecten zijn pure functies van (lamp, tijd) -> HSV. Geen toestand, dus makkelijk te testen,
// te crossfaden en (later) als JSON op te slaan voor de portal.
//
// Ontwerpregel (gemeten op L630, zie CLAUDE.md): per lamp de tint niet sneller dan ~5°/s laten veranderen;
// snelle beweging langs de schutting maak je met een lange golflengte, niet met een korte periode.

using System.Text.Json.Serialization;
using Tuinverlichting.Tapo;

namespace Tuinverlichting.Scenes;

/// Waar een lamp staat, zoals een effect hem ziet. IsLantern = een van de lampen in de lantaarnpaal.
public readonly record struct LampPoint(string Name, double Pos, double LanternPos, bool IsLantern = false)
{
    /// Stabiel getal per lamp (op naam), voor effecten die per lamp eigen willekeur willen.
    /// Niet op positie: beide lantaarns staan op dezelfde positie.
    public int Id
    {
        get
        {
            unchecked
            {
                int h = 17;
                foreach (char c in Name) h = h * 31 + c;
                return h & 0x7FFFFFFF;
            }
        }
    }
}

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
[JsonDerivedType(typeof(LanternNightEffect), "lantaarnnacht")]
public abstract record Effect
{
    public abstract Hsv Evaluate(LampPoint lamp, double t);

    /// True = het effect geeft bewust sprongen en laat de ingebouwde fade van de lamp (~1 s, met optrekken en
    /// afremmen) het vloeiend maken. De engine slaat dan de snelheidsbegrenzer over (na de crossfade).
    [JsonIgnore]
    public virtual bool UsesLampFade => false;

    /// Hoe vaak de engine dit effect per lamp wil bemonsteren (null = EngineOptions.Interval).
    /// Alleen zinvol voor effecten die stapsgewijs constant zijn: gelijke waarden worden niet verstuurd.
    [JsonIgnore]
    public virtual TimeSpan? PreferredInterval => null;
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

/// Vuur dat flakkert: elke lamp springt om de ~1 s naar een nieuwe, willekeurige helderheid en tint, en de
/// ingebouwde fade van de L630 (~1 s, optrekken + afremmen) maakt daar vloeiende flakkeringen van.
/// - Elke lamp heeft een eigen ritme (periode en fase) én elke flakkering een licht wisselende duur
///   (tijdvervorming), zodat ze nooit gelijk lopen.
/// - Een trage onderstroom per lamp (Randomness < 1) voorkomt dat het pure ruis wordt.
/// - De waarde is stapsgewijs constant (ook de onderstroom wordt per flakkering bemonsterd): de engine kan het effect
///   dan vaak bemonsteren (PreferredInterval) zonder extra verkeer, want gelijke waarden worden niet verstuurd.
///   Bij 0,7 s bemonstering vielen de wissels van alle lampen op een gezamenlijk raster (zichtbaar ritme van ~2 s).
/// - Geen ruis over de positie: die gaf patronen die langs de schutting schoven (zag eruit als een golf).
/// Waarom niet met kleine stapjes zoals de golf: dat maakte het vuur tam, en bij lage helderheid zichtbaar trapjes.
public sealed record FireEffect : Effect
{
    public double HueMin { get; init; } = 0;
    public double HueMax { get; init; } = 45;
    public double Saturation { get; init; } = 100;
    /// Laag mag hier wel: elke flakkering is één sprong die de lamp zelf fadet, geen reeks kleine 1%-stapjes.
    public double BrightnessMin { get; init; } = 5;
    public double BrightnessMax { get; init; } = 75;
    /// Gemiddelde tijd tussen twee flakkeringen per lamp (per lamp 0,7–1,4× dit).
    public double FlickerS { get; init; } = 1.2;
    /// Deel van de waarde dat per flakkering opnieuw geloot wordt (0 = alleen trage onderstroom, 1 = pure ruis).
    public double BrightnessRandomness { get; init; } = 0.7;
    public double HueRandomness { get; init; } = 0.7;
    /// Seconden per punt van de trage onderstroom.
    public double SlowTimeScaleS { get; init; } = 6;
    public int Seed { get; init; } = 1;

    [JsonIgnore]
    public override bool UsesLampFade => true;

    [JsonIgnore]
    public override TimeSpan? PreferredInterval => TimeSpan.FromMilliseconds(200);

    public override Hsv Evaluate(LampPoint lamp, double t)
    {
        int id = (int)Math.Round(lamp.Pos * 10) + 1000;

        // eigen ritme: periode en fase per lamp, plus een trage tijdvervorming zodat ook opeenvolgende
        // flakkeringen van één lamp niet even lang duren. Helling ≤ 0,8 × 1,5 / 4 = 0,3 → duur 0,77–1,43× de periode.
        double period = FlickerS * (0.7 + 0.7 * Noise.Hash(id, 1, Seed + 31));
        double phase = period * Noise.Hash(id, 2, Seed + 37);
        double warp = 0.8 * Noise.Value2D(t / 4, id, Seed + 41);
        int k = (int)Math.Floor((t + phase + warp) / period);

        // per flakkering een nieuwe loting, plus trage onderstroom per lamp — die ook per flakkering bemonsterd,
        // zodat de waarde binnen een flakkering constant blijft
        double rb = Noise.Hash(k, id, Seed + 11), rh = Noise.Hash(k, id, Seed + 13);
        double tk = k * period;
        double sb = Noise.Value2D(tk / SlowTimeScaleS, id, Seed + 7), sh = Noise.Value2D(tk / SlowTimeScaleS / 1.5, id, Seed);
        double nb = BrightnessRandomness * rb + (1 - BrightnessRandomness) * sb;
        double nh = HueRandomness * rh + (1 - HueRandomness) * sh;

        return new Hsv(HueMin + (HueMax - HueMin) * nh, Saturation, BrightnessMin + (BrightnessMax - BrightnessMin) * nb);
    }
}

/// "Lantaarnnacht": maanverlichte schutting met een warm ademende lantaarn in het midden.
/// - Schutting: traag blauw-violet verloop dat vanuit de lantaarnpaal naar buiten loopt.
/// - Lantaarns: amberkleurige kaarsgloed die zacht ademt, elke lantaarn in een eigen ritme.
/// - Gloed: de schuttinglampen naast de lantaarnpaal krijgen een deel van de amber mee en ademen mee;
///   via de kortste hue-weg (blauw → violet → roze → amber) geeft dat een avondhemel-achtig verloop.
/// Alles blijft ruim binnen de snelheidsbegrenzer (vloeiend op de L630).
public sealed record LanternNightEffect : Effect
{
    // maanlicht op de schutting
    public double MoonHueMin { get; init; } = 220;
    public double MoonHueMax { get; init; } = 262;
    public double MoonSaturation { get; init; } = 65;
    public double MoonBrightnessMin { get; init; } = 18;
    public double MoonBrightnessMax { get; init; } = 38;
    public double MoonPeriodS { get; init; } = 45;
    public double MoonWavelength { get; init; } = 26;

    // kaarsgloed in de lantaarn
    public double GlowHue { get; init; } = 30;
    public double GlowHueSpread { get; init; } = 6;
    public double GlowSaturation { get; init; } = 85;
    public double GlowBrightnessMin { get; init; } = 50;
    public double GlowBrightnessMax { get; init; } = 75;
    /// Seconden per ademhaling-ruispunt: groter = rustiger.
    public double BreathS { get; init; } = 7;

    // gloed van de lantaarn op de schutting
    /// Hoeveel van de lantaarnkleur de schuttinglamp het dichtst bij de paal krijgt (0..1).
    public double HaloStrength { get; init; } = 0.75;
    /// Afstand (lampplaatsen) waarop de gloed tot ~37% is afgenomen.
    public double HaloRadius { get; init; } = 2.2;
    public int Seed { get; init; } = 1;

    public override Hsv Evaluate(LampPoint lamp, double t)
    {
        if (lamp.IsLantern) return Glow(lamp.Id, t);

        var moon = new WaveEffect
        {
            HueMin = MoonHueMin, HueMax = MoonHueMax, Saturation = MoonSaturation,
            BrightnessMin = MoonBrightnessMin, BrightnessMax = MoonBrightnessMax,
            PeriodS = MoonPeriodS, Wavelength = MoonWavelength, Direction = WaveDirection.FromLantern,
        }.Evaluate(lamp, t);

        double d = Math.Abs(lamp.Pos - lamp.LanternPos);
        double w = HaloStrength * Math.Exp(-(d / HaloRadius) * (d / HaloRadius));
        if (w < 0.005) return moon;

        // gloed op de schutting: gemiddelde ademhaling van de paal (vaste id), iets minder fel
        var glow = Glow(0, t);
        var halo = new Hsv(glow.Hue, glow.Saturation - 5, glow.Brightness * 0.8);
        return Hsv.Lerp(moon, halo, w);
    }

    Hsv Glow(int id, double t)
    {
        int row = id % 100_000;
        double u = t / BreathS;
        double nb = 0.75 * Noise.Value2D(u, row, Seed + 3) + 0.25 * Noise.Value2D(2 * u, row, Seed + 5);
        double nh = Noise.Value2D(u / 1.3, row, Seed + 9);
        return new Hsv(
            GlowHue + GlowHueSpread * (2 * nh - 1),
            GlowSaturation,
            GlowBrightnessMin + (GlowBrightnessMax - GlowBrightnessMin) * nb);
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

    /// Pseudo-willekeurig 0..1 voor een roosterpunt; zelfde invoer = zelfde uitkomst.
    public static double Hash(int x, int y, int seed)
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
