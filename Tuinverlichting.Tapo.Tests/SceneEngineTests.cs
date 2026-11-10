using System.Text.Json;
using Tuinverlichting.Scenes;

namespace Tuinverlichting.Tapo.Tests;

public class SceneEngineTests
{
    sealed class FakeOutput(string name) : ILightOutput
    {
        public string Name { get; } = name;
        public List<Hsv> Sent { get; } = [];
        public void Submit(Hsv color) => Sent.Add(color);
    }

    static readonly TimeOnly Afternoon = new(15, 0);

    static SceneEngine Engine(EngineOptions? options = null, params (string Name, double Pos, int Max)[] lamps)
    {
        if (lamps.Length == 0) lamps = [("A", 0, 100), ("B", 1, 100), ("C", 2, 100)];
        return new SceneEngine(
            lamps.Select(l => (new LampConfig(l.Name, "0.0.0.0", "z", l.Pos, l.Max), (ILightOutput)new FakeOutput(l.Name))),
            lanternPos: 1, options);
    }

    // zonder begrenzer, zodat effecten zelf getest kunnen worden
    static readonly EngineOptions NoLimits = new()
    {
        MaxHueSpeed = 1e9, MaxSaturationSpeed = 1e9, MaxBrightnessSpeed = 1e9, LookaheadS = 0,
    };

    [Fact]
    public void Wave_from_lantern_is_symmetric()
    {
        var wave = new WaveEffect { Direction = WaveDirection.FromLantern };
        var left = wave.Evaluate(new LampPoint("L", 7, 9.5), 12.3);
        var right = wave.Evaluate(new LampPoint("R", 12, 9.5), 12.3);
        Assert.Equal(left.Hue, right.Hue, 6);
        Assert.Equal(left.Brightness, right.Brightness, 6);
    }

    [Fact]
    public void Wave_stays_within_its_ranges()
    {
        var wave = new WaveEffect();
        for (double t = 0; t < 100; t += 0.7)
        {
            var c = wave.Evaluate(new LampPoint("A", 3, 9.5), t);
            Assert.InRange(c.Hue, 190 - 1e-9, 240 + 1e-9);
            Assert.InRange(c.Brightness, 35 - 1e-9, 60 + 1e-9);
        }
    }

    [Fact]
    public void Fire_is_deterministic_and_in_range()
    {
        var fire = new FireEffect();
        for (double t = 0; t < 200; t += 1)
        {
            var a = fire.Evaluate(new LampPoint("A", 3, 9.5), t);
            Assert.Equal(a, fire.Evaluate(new LampPoint("A", 3, 9.5), t));
            Assert.InRange(a.Hue, fire.HueMin, fire.HueMax);
            Assert.InRange(a.Brightness, fire.BrightnessMin, fire.BrightnessMax);
        }
    }

    static double Correlation(double[] a, double[] b)
    {
        double ma = a.Average(), mb = b.Average();
        double cov = a.Zip(b).Sum(p => (p.First - ma) * (p.Second - mb));
        return cov / Math.Sqrt(a.Sum(x => (x - ma) * (x - ma)) * b.Sum(x => (x - mb) * (x - mb)));
    }

    [Fact]
    public void Fire_lamps_move_independently_and_not_as_a_wave()
    {
        // Wim zag bij de vorige versie een golf: buren liepen te gelijk op en patronen schoven langs de schutting.
        var fire = new FireEffect();
        double[] Series(double pos, double shift) =>
            Enumerable.Range(0, 2000).Select(i => fire.Evaluate(new LampPoint("L", pos, 9.5), i * 0.7 + shift).Brightness).ToArray();

        var a = Series(4, 0);
        var b = Series(5, 0);
        Assert.True(Correlation(a, b) < 0.5, $"buren te gelijk: r={Correlation(a, b):0.00}");

        // golf = buur loopt met een vertraging gelijk; dat mag voor geen enkele vertraging gelden
        for (double lag = 0.7; lag <= 7; lag += 0.7)
        {
            var bShift = Series(5, lag);
            Assert.True(Correlation(a, bShift) < 0.5, $"golfpatroon bij vertraging {lag:0.0} s: r={Correlation(a, bShift):0.00}");
        }
    }

    [Fact]
    public void Fire_flickers_at_lamp_fade_pace_with_own_rhythm_per_lamp()
    {
        // Elke lamp: nieuwe loting om de ~0,5–2 × FlickerS (lampperiode 0,7–1,4× en per flakkering 0,77–1,43×
        // door de tijdvervorming), daartussen constant; elke lamp een ander ritme en geen vaste maat per lamp.
        var fire = new FireEffect();
        var periods = new List<double>();
        foreach (var pos in new[] { 0.0, 1, 2, 3, 4, 5, 6, 7, 8 })
        {
            var changes = new List<double>();
            Hsv? prev = null;
            for (double t = 0; t < 120; t += 0.01)
            {
                var c = fire.Evaluate(new LampPoint("L", pos, 9.5), t);
                if (prev is { } p && c != p) changes.Add(t);
                prev = c;
            }
            var gaps = changes.Zip(changes.Skip(1), (a, b) => b - a).ToList();
            Assert.All(gaps, g => Assert.InRange(g, 0.7 * 0.77 * fire.FlickerS - 0.02, 1.4 * 1.43 * fire.FlickerS + 0.02));
            double mean = gaps.Average(), sd = Math.Sqrt(gaps.Average(g => (g - mean) * (g - mean)));
            Assert.True(sd > 0.05 * fire.FlickerS, $"lamp {pos}: flakkeringen te regelmatig (sd {sd:0.000} s)");
            periods.Add(mean);
        }
        Assert.True(periods.Max() - periods.Min() > 0.2 * fire.FlickerS, $"ritmes te gelijk: {string.Join(", ", periods.Select(p => p.ToString("0.00")))}");
    }

    [Fact]
    public void Engine_samples_fire_more_often_but_only_after_crossfade()
    {
        var e = Engine();
        e.SetEffect(new WaveEffect(), TimeSpan.Zero, now: 0);
        Assert.Equal(e.Options.Interval, e.IntervalAt(5));

        e.SetEffect(new FireEffect(), TimeSpan.FromSeconds(10), now: 5);
        Assert.Equal(e.Options.Interval, e.IntervalAt(10));                 // midden in de crossfade
        Assert.Equal(TimeSpan.FromMilliseconds(200), e.IntervalAt(15.1));   // daarna: vuur bepaalt
    }

    [Fact]
    public void Engine_skips_rate_limiter_for_lamp_fade_effects_after_crossfade()
    {
        var opts = new EngineOptions { LookaheadS = 0, MaxBrightnessSpeed = 4 };
        var e = Engine(opts);
        e.SetInitialState("A", new Hsv(10, 100, 30));
        var fire = new FireEffect();
        e.SetEffect(fire, TimeSpan.FromSeconds(2), now: 0);

        // tijdens de crossfade wel begrensd
        e.ComputeStep(0, 0, Afternoon);
        var during = e.ComputeStep(0, 1, Afternoon);
        Assert.InRange(during.Brightness, 26 - 1e-9, 34 + 1e-9);

        // na de crossfade: precies wat het vuur vraagt, ook bij grote sprongen
        for (double t = 3; t < 30; t += 0.7)
        {
            var want = fire.Evaluate(new LampPoint("A", 0, 1), t);
            var got = e.ComputeStep(0, t, Afternoon);
            Assert.Equal(want.Hue, got.Hue, 6);
            Assert.Equal(want.Brightness, got.Brightness, 6);
        }
    }

    [Fact]
    public void Fire_reaches_deep_red_bright_orange_and_dim_embers()
    {
        // Over 5 minuten en alle lampplaatsen moeten de uitersten echt voorkomen, niet alleen het midden.
        var fire = new FireEffect();
        var samples = (from x in Enumerable.Range(0, 15)
                       from i in Enumerable.Range(0, 430)
                       select fire.Evaluate(new LampPoint("L", x, 9.5), i * 0.7)).ToList();
        double Frac(Func<Hsv, bool> f) => samples.Count(f) / (double)samples.Count;

        // "uiterst" = buitenste kwart van het bereik
        double hLow = fire.HueMin + 0.25 * (fire.HueMax - fire.HueMin), hHigh = fire.HueMax - 0.25 * (fire.HueMax - fire.HueMin);
        double bLow = fire.BrightnessMin + 0.25 * (fire.BrightnessMax - fire.BrightnessMin);
        double bHigh = fire.BrightnessMax - 0.25 * (fire.BrightnessMax - fire.BrightnessMin);
        Assert.True(Frac(c => c.Hue < hLow) > 0.10, $"diep rood (<{hLow}°) {Frac(c => c.Hue < hLow):P0}");
        Assert.True(Frac(c => c.Hue > hHigh) > 0.10, $"oranje (>{hHigh}°) {Frac(c => c.Hue > hHigh):P0}");
        Assert.True(Frac(c => c.Brightness < bLow) > 0.10, $"gloeiend laag (<{bLow}%) {Frac(c => c.Brightness < bLow):P0}");
        Assert.True(Frac(c => c.Brightness > bHigh) > 0.10, $"opflakkerend (>{bHigh}%) {Frac(c => c.Brightness > bHigh):P0}");
    }

    [Fact]
    public void Crossfade_starts_at_initial_state_and_ends_at_effect()
    {
        var e = Engine(NoLimits);
        foreach (var n in new[] { "A", "B", "C" }) e.SetInitialState(n, new Hsv(0, 0, 50));
        e.SetEffect(new StaticEffect(100, 100, 50), TimeSpan.FromSeconds(10), now: 0);

        Assert.Equal(new Hsv(0, 0, 50), e.ComputeStep(0, 0, Afternoon));
        var mid = e.ComputeStep(0, 5, Afternoon);
        Assert.Equal(50, mid.H);
        Assert.Equal(new Hsv(100, 100, 50), e.ComputeStep(0, 10, Afternoon));
    }

    [Fact]
    public void Crossfade_between_two_effects()
    {
        var e = Engine(NoLimits);
        e.SetEffect(new StaticEffect(200, 50, 50), TimeSpan.Zero, now: 0);
        e.SetEffect(new StaticEffect(220, 50, 50), TimeSpan.FromSeconds(20), now: 0);
        Assert.Equal(200, e.ComputeStep(1, 0, Afternoon).H);
        Assert.Equal(210, e.ComputeStep(1, 10, Afternoon).H);
        Assert.Equal(220, e.ComputeStep(1, 20, Afternoon).H);
    }

    [Fact]
    public void Zone_ceiling_caps_brightness()
    {
        var e = Engine(NoLimits, ("A", 0, 50), ("B", 1, 100));
        e.SetEffect(new StaticEffect(200, 50, 80), TimeSpan.Zero, now: 0);
        var frame = e.ComputeFrame(0, Afternoon);
        Assert.Equal(50, frame[0].Color.B);
        Assert.Equal(80, frame[1].Color.B);
    }

    [Theory]
    [InlineData(15, 0, 1.0)]
    [InlineData(22, 0, 1.0)]
    [InlineData(22, 30, 0.8)]
    [InlineData(23, 30, 0.6)]
    [InlineData(3, 0, 0.6)]
    [InlineData(6, 0, 1.0)]
    public void Evening_curve(int h, int m, double expected) =>
        Assert.Equal(expected, Engine().EveningFactor(new TimeOnly(h, m)), 6);

    [Fact]
    public void Rate_limiter_caps_change_per_second()
    {
        var e = Engine(new EngineOptions { LookaheadS = 0, MaxHueSpeed = 6, MaxBrightnessSpeed = 4 });
        e.SetInitialState("A", new Hsv(200, 70, 40));
        e.SetEffect(new StaticEffect(240, 70, 60), TimeSpan.Zero, now: 0);

        e.ComputeStep(0, 0, Afternoon);                 // eerste stap: dt = 0 -> blijft staan
        var after1s = e.ComputeStep(0, 1, Afternoon);
        Assert.Equal(206, after1s.H);
        Assert.Equal(44, after1s.B);
    }

    [Fact]
    public void Rate_limiter_takes_shortest_way_around_360()
    {
        var e = Engine(new EngineOptions { LookaheadS = 0, MaxHueSpeed = 6 });
        e.SetInitialState("A", new Hsv(355, 70, 40));
        e.SetEffect(new StaticEffect(20, 70, 40), TimeSpan.Zero, now: 0);
        e.ComputeStep(0, 0, Afternoon);
        Assert.Equal(1, e.ComputeStep(0, 1, Afternoon).H);   // 355 + 6 = 361 -> 1
    }

    [Fact]
    public void Effects_round_trip_as_json()
    {
        Effect[] effects =
        [
            new WaveEffect { PeriodS = 40, Direction = WaveDirection.FromLantern },
            new FireEffect { Seed = 3 },
            new StaticEffect(30, 20, 40),
        ];
        var json = JsonSerializer.Serialize(effects);
        Assert.Contains("\"type\":\"golf\"", json);
        var back = JsonSerializer.Deserialize<Effect[]>(json)!;
        Assert.Equal(effects, back);
    }
}
