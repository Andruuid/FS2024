using ChallengeLab.Core.Scoring;

namespace ChallengeLab.Core.Tests;

public sealed class GlideslopeAngleResolverTests
{
    [Fact]
    public void EmptyCandidates_FallBackToThreeDegrees()
    {
        var r = GlideslopeAngleResolver.Resolve(null);
        Assert.Equal(3.0, r.Degrees);
        Assert.Equal(GlideslopeAngleResolver.SourceDefault, r.Source);
    }

    [Fact]
    public void ValidVasi_IsPreferred()
    {
        var r = GlideslopeAngleResolver.ResolveEnd(5.5, null);
        Assert.Equal(5.5, r.Degrees);
        Assert.Equal(GlideslopeAngleResolver.SourceVasi, r.Source);
    }

    [Fact]
    public void PrefersLeftThenRight()
    {
        var r = GlideslopeAngleResolver.ResolveEnd(null, 6.65);
        Assert.Equal(6.65, r.Degrees);
        Assert.Equal(GlideslopeAngleResolver.SourceVasi, r.Source);
    }

    [Fact]
    public void OutOfRangeAngles_AreIgnored()
    {
        var r = GlideslopeAngleResolver.Resolve(new double?[] { 0.0, 12.0, null, 3.0 });
        Assert.Equal(3.0, r.Degrees);
        Assert.Equal(GlideslopeAngleResolver.SourceVasi, r.Source);
    }

    [Fact]
    public void FeetPerNm_ScalesWithAngle()
    {
        var three = RunwayPathGeometry.FeetPerNauticalMileForAngle(3.0);
        var steep = RunwayPathGeometry.FeetPerNauticalMileForAngle(5.5);
        Assert.InRange(three, 310, 330);
        Assert.True(steep > three * 1.5);
    }
}
