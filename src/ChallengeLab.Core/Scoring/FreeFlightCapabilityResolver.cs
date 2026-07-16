using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Scoring;

public static class FreeFlightGateIds
{
    public const string ContactStability = "contact_stability";
    public const string Gear = "gear";
    public const string Flaps = "flaps";
    public const string Spoilers = "spoiler_deployment";
    public const string NoseGearImpact = "nose_gear_impact";
    public const string StallWarning = "stall_warning";
    public const string Automation = "automation";
    public const string ManualBraking = "manual_braking";
    public const string RolloutDistance = "rollout_distance";
    public const string ReverseThrust = "reverse_thrust";
    public const string PauseUsage = "pause_usage";
    public const string SimulationRate = "simulation_rate";
    public const string CockpitView = "cockpit_view";

    public static bool IsUniversal(string gateId) => gateId is
        StallWarning or RolloutDistance or PauseUsage or SimulationRate or CockpitView;
}

/// <summary>Resolves and freezes Free Flight gate applicability at runway lock/arm time.</summary>
public static class FreeFlightCapabilityResolver
{
    public static FreeFlightCapabilityContext Freeze(TelemetrySample sample, bool isWaterRunway)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var context = new FreeFlightCapabilityContext
        {
            FlapHandlePositionCount = sample.FlapsHandlePositionCount,
            SpoilersAvailable = sample.SpoilersAvailable,
            AutopilotAvailable = sample.AutopilotAvailable,
            ThrottleLowerLimitPercent = double.IsFinite(sample.ThrottleLowerLimitPercent ?? double.NaN)
                ? sample.ThrottleLowerLimitPercent
                : null,
            IsGearRetractable = sample.IsGearRetractable,
            IsGearWheels = sample.IsGearWheels,
            IsGearFloats = sample.IsGearFloats,
            IsTailDragger = sample.IsTailDragger,
            IsWaterRunway = isWaterRunway
        };

        var conventional = ResolveConventionalTricycle(context);
        Set(context, FreeFlightGateIds.ContactStability, conventional,
            "Independent conventional left/right main-gear mapping is available.",
            "Contact-stability scoring is not applicable without conventional independent-main gear mapping.");

        var gear = isWaterRunway
            ? FreeFlightGateApplicability.NotApplicable
            : context.IsGearWheels == false || context.IsGearRetractable == false
                ? FreeFlightGateApplicability.NotApplicable
                : context.IsGearWheels is null || context.IsGearRetractable is null
                    ? FreeFlightGateApplicability.Unknown
                    : FreeFlightGateApplicability.Applicable;
        Set(context, FreeFlightGateIds.Gear, gear,
            "Retractable wheeled gear is required for this land-runway attempt.",
            isWaterRunway
                ? "Gear gate is not applicable to a water-runway operation."
                : "Gear gate is not applicable to fixed or non-wheeled gear.");

        var flaps = context.FlapHandlePositionCount switch
        {
            >= 2 => FreeFlightGateApplicability.Applicable,
            <= 1 => FreeFlightGateApplicability.NotApplicable,
            _ => FreeFlightGateApplicability.Unknown
        };
        Set(context, FreeFlightGateIds.Flaps, flaps,
            $"Aircraft reports {context.FlapHandlePositionCount?.ToString() ?? "unknown"} flap handle positions.",
            "Aircraft has fewer than two flap handle positions.");

        SetBooleanCapability(context, FreeFlightGateIds.Spoilers, context.SpoilersAvailable,
            "Aircraft reports installed spoilers.", "Aircraft reports no installed spoilers.");

        var noseAndBrakes = isWaterRunway
            ? FreeFlightGateApplicability.NotApplicable
            : conventional;
        Set(context, FreeFlightGateIds.NoseGearImpact, noseAndBrakes,
            "Conventional tricycle wheeled gear is available on a land runway.",
            isWaterRunway
                ? "Nose-impact gate is not applicable to water operations."
                : "Nose-impact gate is not applicable to taildraggers, floats, or non-conventional gear.");
        Set(context, FreeFlightGateIds.ManualBraking, noseAndBrakes,
            "Conventional tricycle wheeled gear is available on a land runway.",
            isWaterRunway
                ? "Manual-braking gate is not applicable to water operations."
                : "Manual-braking gate is not applicable to taildraggers, floats, or non-conventional gear.");

        Set(context, FreeFlightGateIds.StallWarning, FreeFlightGateApplicability.Applicable,
            "Stall-warning gate applies to every armed Free Flight attempt.", "");
        SetBooleanCapability(context, FreeFlightGateIds.Automation, context.AutopilotAvailable,
            "Aircraft reports installed autopilot capability.", "Aircraft reports no installed autopilot.");
        Set(context, FreeFlightGateIds.RolloutDistance, FreeFlightGateApplicability.Applicable,
            "Remaining-runway gate applies to every detected runway.", "");

        var reverse = context.ThrottleLowerLimitPercent switch
        {
            < 0 => FreeFlightGateApplicability.Applicable,
            >= 0 => FreeFlightGateApplicability.NotApplicable,
            _ => FreeFlightGateApplicability.Unknown
        };
        Set(context, FreeFlightGateIds.ReverseThrust, reverse,
            $"Throttle lower limit {context.ThrottleLowerLimitPercent:0.##}% indicates reverse capability.",
            "Aircraft throttle lower limit indicates no reverse capability.");
        Set(context, FreeFlightGateIds.PauseUsage, FreeFlightGateApplicability.Applicable,
            "Pause-use gate applies to every armed Free Flight attempt.", "");
        Set(context, FreeFlightGateIds.SimulationRate, FreeFlightGateApplicability.Applicable,
            "Simulation-rate gate applies to every armed Free Flight attempt.", "");
        Set(context, FreeFlightGateIds.CockpitView, FreeFlightGateApplicability.Applicable,
            "Cockpit-view gate applies to every armed Free Flight attempt.", "");
        return context;
    }

    public static FreeFlightGateDecision ResolveDecision(
        FreeFlightCapabilityContext? context,
        string gateId)
    {
        if (context?.DecisionFor(gateId) is { } decision)
            return decision;
        return new FreeFlightGateDecision
        {
            Applicability = FreeFlightGateIds.IsUniversal(gateId)
                ? FreeFlightGateApplicability.Applicable
                : FreeFlightGateApplicability.Unknown,
            Reason = context is null
                ? "Capability context was not captured; the gate remains applicable with missing-data fallback."
                : "Capability decision was not captured; the gate remains applicable with missing-data fallback."
        };
    }

    private static FreeFlightGateApplicability ResolveConventionalTricycle(
        FreeFlightCapabilityContext context)
    {
        if (context.IsGearWheels == false || context.IsGearFloats == true || context.IsTailDragger == true)
            return FreeFlightGateApplicability.NotApplicable;
        if (context.IsGearWheels is null || context.IsGearFloats is null || context.IsTailDragger is null)
            return FreeFlightGateApplicability.Unknown;
        return FreeFlightGateApplicability.Applicable;
    }

    private static void SetBooleanCapability(
        FreeFlightCapabilityContext context,
        string id,
        bool? value,
        string applicableReason,
        string notApplicableReason) => Set(context, id, value switch
    {
        true => FreeFlightGateApplicability.Applicable,
        false => FreeFlightGateApplicability.NotApplicable,
        null => FreeFlightGateApplicability.Unknown
    }, applicableReason, notApplicableReason);

    private static void Set(
        FreeFlightCapabilityContext context,
        string id,
        FreeFlightGateApplicability applicability,
        string applicableReason,
        string notApplicableReason)
    {
        context.GateDecisions[id] = new FreeFlightGateDecision
        {
            Applicability = applicability,
            Reason = applicability switch
            {
                FreeFlightGateApplicability.Applicable => applicableReason,
                FreeFlightGateApplicability.NotApplicable => notApplicableReason,
                _ => "Aircraft capability telemetry was unavailable; missing-data fallback applies."
            }
        };
    }
}
