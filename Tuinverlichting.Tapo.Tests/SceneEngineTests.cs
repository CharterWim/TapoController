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
    public void Fire_is_deterministic_in_range_and_correlated_between_neighbours()
    {
        var fire = new FireEffect();
        double diffNeighbours = 0, diffFar = 0;
        for (double t = 0; t < 200; t += 1)
        {
            var a = fire.Evaluate(new LampPoint("A", 0, 9.5), t);
            Assert.Equal(a, fire.Evaluate(new LampPoint("A", 0, 9.5), t));
            Assert.InRange(a.Hue, 8, 35);
            Assert.InRange(a.Brightness, 25, 55);
            diffNeighbours += Math.Abs(a.Hue - fire.Evaluate(new LampPoint("B", 1, 9.5), t).Hue);
            diffFar += Math.Abs(a.Hue - fire.Evaluate(new LampPoint("Z", 14, 9.5), t).Hue);
        }
        Assert.True(diffNeighbours < diffFar, $"buren {diffNeighbours:0} vs ver weg {diffFar:0}");
    }

    [Fact]
    public void Fire_changes_slowly()
    {
        var fire = new FireEffect();
        for (double t = 0; t < 120; t += 0.7)
        {
            var a = fire.Evaluate(new LampPoint("A", 4, 9.5), t);
            var b = fire.Evaluate(new LampPoint("A", 4, 9.5), t + 0.7);
            Assert.True(Math.Abs(b.Hue - a.Hue) < 6, $"tint sprong {a.Hue:0.0} -> {b.Hue:0.0} op t={t}");
        }
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
