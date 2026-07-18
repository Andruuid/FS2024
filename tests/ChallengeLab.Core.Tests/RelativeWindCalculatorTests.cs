using ChallengeLab.Core.Scoring;

namespace ChallengeLab.Core.Tests;

public sealed class RelativeWindCalculatorTests
{
    [Theory]
    [InlineData(0, 0, 20, 0, 20, 0)]
    [InlineData(180, 0, 20, 180, -20, 0)]
    [InlineData(90, 0, 20, 90, 0, 20)]
    [InlineData(270, 0, 20, -90, 0, -20)]
    [InlineData(100, 10, 12, 90, 0, 12)]
    public void Components_AreAircraftRelativeAndSignedFromTheWindSide(
        double windDirection,
        double aircraftHeading,
        double speed,
        double expectedRelativeFrom,
        double expectedLongitudinal,
        double expectedCrosswind)
    {
        var reading = RelativeWindCalculator.Calculate(
            windDirection,
            speed,
            aircraftHeading);

        Assert.True(reading.IsAvailable);
        Assert.True(reading.HasWind);
        Assert.Equal(expectedRelativeFrom, reading.RelativeFromAngleDeg, 8);
        Assert.Equal(expectedLongitudinal, reading.LongitudinalKts, 8);
        Assert.Equal(expectedCrosswind, reading.CrosswindKts, 8);
    }

    [Fact]
    public void Calm_IsAvailableButNotMarkedAsActiveWind()
    {
        var reading = RelativeWindCalculator.Calculate(310, 0.4, 300);

        Assert.True(reading.IsAvailable);
        Assert.False(reading.HasWind);
        Assert.Equal(0.4, reading.WindSpeedKts);
    }

    [Theory]
    [InlineData(double.NaN, 10, 0, true)]
    [InlineData(0, double.NaN, 0, true)]
    [InlineData(0, -1, 0, true)]
    [InlineData(0, 10, double.PositiveInfinity, true)]
    [InlineData(0, 10, 0, false)]
    public void InvalidOrDisconnectedWind_IsUnavailable(
        double direction,
        double speed,
        double heading,
        bool connected)
    {
        var reading = RelativeWindCalculator.Calculate(direction, speed, heading, connected);

        Assert.False(reading.IsAvailable);
        Assert.False(reading.HasWind);
    }
}
