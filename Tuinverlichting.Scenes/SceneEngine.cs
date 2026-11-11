// Scène-engine: rekent per lamp de kleur uit en stuurt die naar een ILightOutput.
//
// Per lamp, per stap:
//   1. effect(s) evalueren op t + Lookahead (de lamp fadet zelf ~1 s, dus we sturen waar hij moet uitkomen)
//   2. crossfade tussen vorig en nieuw effect (smoothstep over de ingestelde duur)
//   3. avondcurve (helderheid × factor) en helderheidsplafond van de zone
//   4. snelheidsbegrenzer: max °/s tint, %/s verzadiging en helderheid t.o.v. de vorige uitvoer van die lamp
//
// ComputeStep/ComputeFrame zijn puur (geen klok, geen netwerk) zodat ze te testen zijn; RunAsync doet de timing.

using System.Diagnostics;
using Tuinverlichting.Tapo;

namespace Tuinverlichting.Scenes;

public sealed record EngineOptions
{
    /// Tijd vooruit kijken (≈ ingebouwde fade van de L630).
    public double LookaheadS { get; init; } = 1.0;
    public double MaxHueSpeed { get; init; } = 6;          // °/s
    public double MaxSaturationSpeed { get; init; } = 10;  // %/s
    /// %/s. Helderheidsstappen bleken in kijktests niet zichtbaar (alleen-helderheidsgolf: 0 markeringen),
    /// dus ruimer dan tint; nodig voor het opflakkeren van vuur.
    public double MaxBrightnessSpeed { get; init; } = 8;
    /// Vanaf DimStart loopt de helderheid lineair af tot DimFactor om DimEnd; tot NightEnds blijft hij gedimd.
    public TimeOnly DimStart { get; init; } = new(22, 0);
    public TimeOnly DimEnd { get; init; } = new(23, 0);
    public TimeOnly NightEnds { get; init; } = new(6, 0);
    public double DimFactor { get; init; } = 0.6;
    /// Interval waarin elke lamp één update krijgt (gespreid over de lampen).
    public TimeSpan Interval { get; init; } = TimeSpan.FromMilliseconds(700);
}

public sealed class SceneEngine
{
    sealed class LampState(LampConfig config, ILightOutput output, LampPoint point)
    {
        public LampConfig Config { get; } = config;
        public ILightOutput Output { get; } = output;
        public LampPoint Point { get; } = point;
        public Hsv? Last;
        public double LastT;
        public Hsv? Initial;
        /// Stuurt de engine deze lamp aan? (bijv. lantaarns alleen in scènes die ze een rol geven)
        public volatile bool Active = true;
        /// Net geactiveerd: crossfade vanaf Initial i.p.v. vanaf het vorige effect.
        public volatile bool FadeFromInitial;
    }

    readonly LampState[] _lamps;
    readonly object _lock = new();
    Effect? _from;
    Effect? _to;
    double _fadeStart;
    double _fadeDuration;

    public SceneEngine(IEnumerable<(LampConfig Config, ILightOutput Output)> lamps, double lanternPos, EngineOptions? options = null)
    {
        _lamps = lamps.OrderBy(l => l.Config.Pos)
            .Select(l => new LampState(l.Config, l.Output,
                new LampPoint(l.Config.Name, l.Config.Pos, lanternPos, IsLantern: l.Config.Zone == "lantaarn")))
            .ToArray();
        Options = options ?? new EngineOptions();
    }

    public EngineOptions Options { get; }
    public Effect? CurrentEffect { get { lock (_lock) return _to; } }

    /// Begintoestand van een lamp (zijn huidige kleur): het eerste effect fadet hier vandaan.
    public void SetInitialState(string lampName, Hsv color)
    {
        var l = _lamps.First(l => l.Config.Name == lampName);
        l.Initial = color;
        l.Last = color;
    }

    /// Nieuw effect, met crossfade vanaf wat er nu draait (of vanaf de begintoestand).
    public void SetEffect(Effect effect, TimeSpan crossfade, double now)
    {
        lock (_lock)
        {
            // Liep er nog een crossfade? Dan wordt het nieuwe "van" het doel van die fade;
            // de snelheidsbegrenzer vangt een eventuele sprong op.
            _from = _to;
            _to = effect;
            _fadeStart = now;
            _fadeDuration = Math.Max(0, crossfade.TotalSeconds);
            foreach (var l in _lamps) l.FadeFromInitial = false;
        }
    }

    /// Alles terug naar af: geen effect, en per lamp een nieuwe begintoestand (de huidige kleur van de lamp).
    /// Het volgende SetEffect fadet vanuit die begintoestand.
    public void Reset(IReadOnlyDictionary<string, Hsv> startColors)
    {
        lock (_lock)
        {
            _from = _to = null;
            _fadeDuration = 0;
            foreach (var l in _lamps)
            {
                if (startColors.TryGetValue(l.Config.Name, out var c)) { l.Initial = c; l.Last = c; }
                l.FadeFromInitial = false;
            }
        }
    }

    /// Lamp wel of niet aansturen. Bij activeren (ná SetEffect aanroepen): crossfade vanaf startColor
    /// i.p.v. vanaf het vorige effect, omdat de lamp daar niet aan meedeed.
    public void SetActive(string lampName, bool active, Hsv? startColor = null)
    {
        var l = _lamps.First(l => l.Config.Name == lampName);
        if (active && !l.Active && startColor is { } c)
        {
            l.Initial = c;
            l.Last = c;
            l.FadeFromInitial = true;
        }
        l.Active = active;
    }

    public bool IsActive(string lampName) => _lamps.First(l => l.Config.Name == lampName).Active;

    /// Avondcurve: helderheidsfactor voor een lokale tijd.
    public double EveningFactor(TimeOnly local)
    {
        var o = Options;
        if (local >= o.DimEnd || local < o.NightEnds) return o.DimFactor;
        if (local < o.DimStart) return 1.0;
        double f = (local - o.DimStart).TotalMinutes / (o.DimEnd - o.DimStart).TotalMinutes;
        return 1.0 + (o.DimFactor - 1.0) * f;
    }

    /// Kleur voor één lamp op tijd t (seconden sinds start), inclusief alle veiligheidslagen. Werkt de lampstaat bij.
    public Hsv ComputeStep(int lampIndex, double t, TimeOnly local)
    {
        var l = _lamps[lampIndex];
        Effect? from, to;
        double progress;
        lock (_lock)
        {
            from = _from;
            to = _to;
            progress = _fadeDuration <= 0 ? 1 : Math.Clamp((t - _fadeStart) / _fadeDuration, 0, 1);
        }

        double Te(Effect e) => e.UsesLookahead ? t + Options.LookaheadS : t;
        Hsv target;
        if (to is null)
            target = l.Last ?? l.Initial ?? new Hsv(0, 0, 1);
        else
        {
            var b = to.Evaluate(l.Point, Te(to));
            var a = from is null || l.FadeFromInitial ? l.Initial ?? b : from.Evaluate(l.Point, Te(from));
            double s = progress * progress * (3 - 2 * progress);   // smoothstep
            target = Hsv.Lerp(a, b, s);
        }

        // avondcurve en zoneplafond (alleen op wat het effect vraagt, niet op de begintoestand van de lamp)
        double bri = Math.Min(target.Brightness * EveningFactor(local), l.Config.MaxBrightness);
        target = target with { Brightness = Math.Max(1, bri) };

        // snelheidsbegrenzer t.o.v. de vorige uitvoer van deze lamp — niet voor effecten die bewust springen en
        // de lampfade het werk laten doen (vuur), zodra de crossfade daarheen klaar is
        bool lampFades = to is { UsesLampFade: true } && progress >= 1;
        if (!lampFades && l.Last is { } last)
        {
            double dt = Math.Max(0, t - l.LastT);
            target = Limit(last, target, dt);
        }
        l.Last = target;
        l.LastT = t;
        return target;
    }

    /// Alle lampen op tijd t (voor tests en simulatie).
    public IReadOnlyList<(string Name, Hsv Color)> ComputeFrame(double t, TimeOnly local) =>
        _lamps.Select((l, i) => (l.Config.Name, ComputeStep(i, t, local))).ToArray();

    Hsv Limit(Hsv last, Hsv target, double dt)
    {
        double dh = ((target.Hue - last.Hue) % 360 + 540) % 360 - 180;   // kortste weg, -180..180
        double maxH = Options.MaxHueSpeed * dt, maxS = Options.MaxSaturationSpeed * dt, maxB = Options.MaxBrightnessSpeed * dt;
        return new Hsv(
            last.Hue + Math.Clamp(dh, -maxH, maxH),
            last.Saturation + Math.Clamp(target.Saturation - last.Saturation, -maxS, maxS),
            last.Brightness + Math.Clamp(target.Brightness - last.Brightness, -maxB, maxB));
    }

    /// Bemonsteringsinterval per lamp op tijd t: dat van het effect zelf (bijv. vuur: vaker, want stapsgewijs
    /// constant) zodra de crossfade erheen klaar is, anders EngineOptions.Interval. Tijdens een crossfade
    /// verandert de uitvoer continu en zou vaak bemonsteren veel verkeer geven.
    public TimeSpan IntervalAt(double t)
    {
        lock (_lock)
        {
            bool faded = _fadeDuration <= 0 || t >= _fadeStart + _fadeDuration;
            return faded && _to?.PreferredInterval is { } p ? p : Options.Interval;
        }
    }

    /// Draait de engine tot ct geannuleerd wordt. Elke lamp krijgt per interval (zie IntervalAt) één beurt; de
    /// lampen zijn gespreid binnen dat interval zodat er nooit alle requests tegelijk de lucht in gaan.
    /// clock levert t (seconden); localNow levert de lokale tijd voor de avondcurve.
    public async Task RunAsync(Stopwatch clock, Func<TimeOnly> localNow, CancellationToken ct)
    {
        if (_lamps.Length == 0) return;
        double due = clock.Elapsed.TotalMilliseconds;
        for (long k = 0; !ct.IsCancellationRequested; k++)
        {
            due += IntervalAt(clock.Elapsed.TotalSeconds).TotalMilliseconds / _lamps.Length;
            double wait = due - clock.Elapsed.TotalMilliseconds;
            if (wait >= 1)
                try { await Task.Delay((int)wait, ct); } catch (OperationCanceledException) { break; }

            int i = (int)(k % _lamps.Length);
            if (!_lamps[i].Active) continue;
            var color = ComputeStep(i, clock.Elapsed.TotalSeconds, localNow());
            _lamps[i].Output.Submit(color);
        }
    }
}
