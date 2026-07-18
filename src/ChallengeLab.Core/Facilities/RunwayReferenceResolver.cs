using ChallengeLab.Core.Config;
using ChallengeLab.Core.Scoring;

namespace ChallengeLab.Core.Facilities;

/// <summary>Applies CSV-first runway geometry and freezes derived aiming data into RunwayConfig.</summary>
public sealed class RunwayReferenceResolver
{
    private readonly OurAirportsRunwayCatalog _catalog;

    public RunwayReferenceResolver(OurAirportsRunwayCatalog? catalog = null) =>
        _catalog = catalog ?? OurAirportsRunwayCatalog.Default;

    public OurAirportsRunwayCatalog Catalog => _catalog;

    public bool TryApplyCsv(RunwayConfig runway)
    {
        ArgumentNullException.ThrowIfNull(runway);
        if (!_catalog.TryGetRunwayEnd(runway.AirportIcao, runway.RunwayId, out var reference))
            return false;

        runway.CountryCode = reference.CountryCode;
        runway.ThresholdLatitude = reference.UsableThresholdLatitude;
        runway.ThresholdLongitude = reference.UsableThresholdLongitude;
        runway.HeadingTrueDeg = reference.HeadingTrueDeg;
        runway.ElevationFeet = reference.ElevationFeet;
        runway.LengthM = reference.FullLengthFeet * RunwayPathGeometry.MetersPerFoot;
        if (reference.WidthFeet > 0)
            runway.WidthM = reference.WidthFeet * RunwayPathGeometry.MetersPerFoot;
        runway.DisplacedThresholdM = reference.DisplacedThresholdFeet * RunwayPathGeometry.MetersPerFoot;
        runway.LandingDistanceAvailableM = reference.LandingDistanceAvailableFeet * RunwayPathGeometry.MetersPerFoot;
        runway.RunwayDataSource = "OurAirports CSV";
        runway.RunwayDataSnapshotId = reference.SnapshotId;
        ApplyAimingPoint(runway, "OurAirports CSV", "Dataset");
        return true;
    }

    public static void ApplyAimingPoint(
        RunwayConfig runway,
        string source,
        string confidence)
    {
        ArgumentNullException.ThrowIfNull(runway);
        var displacementM = IsNonNegativeFinite(runway.DisplacedThresholdM)
            ? runway.DisplacedThresholdM!.Value
            : 0.0;
        var ldaM = IsPositiveFinite(runway.LandingDistanceAvailableM)
            ? runway.LandingDistanceAvailableM!.Value
            : runway.LengthM - displacementM;
        if (!double.IsFinite(ldaM) || ldaM <= 0)
            return;

        var ldaFeet = ldaM / RunwayPathGeometry.MetersPerFoot;
        var startFeet = AimingPointCalculator.CalculateExpectedDistanceFromThresholdFeet(
            runway.CountryCode,
            ldaFeet);
        var markerLengthFeet = AimingPointCalculator.EstimateMarkerLengthFeet(
            runway.CountryCode,
            ldaFeet);
        runway.LandingDistanceAvailableM = ldaM;
        runway.AimingMarkerStartM = startFeet * RunwayPathGeometry.MetersPerFoot;
        runway.AimingMarkerFromPavementEndM = displacementM
                                                + startFeet * RunwayPathGeometry.MetersPerFoot;
        runway.AimingMarkerLengthM = markerLengthFeet * RunwayPathGeometry.MetersPerFoot;
        runway.AimingMarkerCenterM = (startFeet + markerLengthFeet / 2.0)
                                      * RunwayPathGeometry.MetersPerFoot;
        // Retain the midpoint for v1/v2 visualization and old tape compatibility only.
        runway.IdealTouchdownDistanceM = (startFeet + 400) * RunwayPathGeometry.MetersPerFoot;
        runway.AimingMarkerSource = source;
        runway.AimingMarkerConfidence = confidence;
    }

    private static bool IsPositiveFinite(double? value) => value is { } v && double.IsFinite(v) && v > 0;
    private static bool IsNonNegativeFinite(double? value) => value is { } v && double.IsFinite(v) && v >= 0;
}
