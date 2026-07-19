using ChallengeLab.Core.Scoring;

namespace ChallengeLab.App.Controls;

/// <summary>Shared live IAS presentation relative to VAPP for both approach HUDs.</summary>
internal static class ApproachSpeedPresentation
{
    internal const double GreenBelowVappKts = 8;
    internal const double GreenAboveVappKts = 5;
    internal const double OrangeBelowVappKts = 16;
    internal const double OrangeAboveVappKts = 10;

    public static ApproachSpeedReading Calculate(double? iasKts, double? vappKts)
    {
        if (iasKts is not { } ias
            || vappKts is not { } vapp
            || !double.IsFinite(ias)
            || !double.IsFinite(vapp)
            || vapp <= 0)
        {
            return default;
        }

        var delta = ias - vapp;
        var status = delta >= -GreenBelowVappKts && delta <= GreenAboveVappKts
            ? LandingMonitorStatus.Green
            : delta >= -OrangeBelowVappKts && delta <= OrangeAboveVappKts
                ? LandingMonitorStatus.Orange
                : LandingMonitorStatus.Red;

        return new ApproachSpeedReading(vapp, delta, status);
    }
}

internal readonly record struct ApproachSpeedReading(
    double? VappKts,
    double? DeltaKts,
    LandingMonitorStatus Status);
