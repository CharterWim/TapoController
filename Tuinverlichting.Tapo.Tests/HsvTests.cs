namespace Tuinverlichting.Tapo.Tests;

public class HsvTests
{
    [Theory]
    [InlineData(359.6, 0)]
    [InlineData(-10, 350)]
    [InlineData(720.2, 0)]
    [InlineData(212.5, 212)]   // banker's rounding van Math.Round
    public void Hue_is_normalised_and_rounded(double hue, int expected) =>
        Assert.Equal(expected, new Hsv(hue, 50, 50).H);

    [Fact]
    public void Saturation_and_brightness_are_clamped()
    {
        var c = new Hsv(0, 120, 0);
        Assert.Equal(100, c.S);
        Assert.Equal(1, c.B);   // 0 bestaat niet bij set_device_info
    }

    [Fact]
    public void Lerp_takes_shortest_way_around_360()
    {
        var mid = Hsv.Lerp(new Hsv(350, 0, 0), new Hsv(10, 100, 100), 0.5);
        Assert.Equal(0, mid.H);
        Assert.Equal(50, mid.S);
        Assert.Equal(50, mid.B);

        Assert.Equal(330, Hsv.Lerp(new Hsv(10, 0, 0), new Hsv(290, 0, 0), 0.5).H);
    }

    [Fact]
    public void SameOnLamp_compares_rounded_values()
    {
        Assert.True(new Hsv(210.2, 70.4, 45.1).SameOnLamp(new Hsv(209.8, 69.6, 44.9)));
        Assert.False(new Hsv(210, 70, 45).SameOnLamp(new Hsv(211, 70, 45)));
    }
}
