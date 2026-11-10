namespace Tuinverlichting.Tapo;

/// Kleur in HSV zoals de Tapo-lampen hem kennen: hue 0–360°, saturation 0–100 %, brightness 1–100 %.
/// Waarden zijn doubles zodat effecten vloeiend kunnen rekenen; afronding gebeurt pas bij het versturen.
public readonly record struct Hsv(double Hue, double Saturation, double Brightness)
{
    /// Hue genormaliseerd naar 0..359 en afgerond, zoals de lamp hem krijgt.
    public int H => ((int)Math.Round(Hue) % 360 + 360) % 360;
    public int S => Math.Clamp((int)Math.Round(Saturation), 0, 100);
    /// Helderheid 1..100 (0 bestaat niet bij set_device_info; uitzetten gaat via device_on).
    public int B => Math.Clamp((int)Math.Round(Brightness), 1, 100);

    /// Of twee kleuren voor de lamp hetzelfde zijn (na afronding) — dan hoeft er niets gestuurd te worden.
    public bool SameOnLamp(Hsv other) => H == other.H && S == other.S && B == other.B;

    /// Interpolatie met hue via de kortste weg rond 360°.
    public static Hsv Lerp(Hsv a, Hsv b, double t)
    {
        double dh = ((b.Hue - a.Hue) % 360 + 540) % 360 - 180;   // -180..180
        return new Hsv(
            a.Hue + dh * t,
            a.Saturation + (b.Saturation - a.Saturation) * t,
            a.Brightness + (b.Brightness - a.Brightness) * t);
    }

    public override string ToString() => $"H{H} S{S} B{B}";
}
