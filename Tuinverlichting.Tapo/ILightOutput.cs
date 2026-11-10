namespace Tuinverlichting.Tapo;

/// Iets waar een animatie kleuren naartoe stuurt. TapoLamp implementeert dit; tests gebruiken een nep-uitgang.
public interface ILightOutput
{
    string Name { get; }

    /// Gewenste kleur doorgeven; keert direct terug (nieuwste wint).
    void Submit(Hsv color);
}
