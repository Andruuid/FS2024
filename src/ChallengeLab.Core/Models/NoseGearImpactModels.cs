namespace ChallengeLab.Core.Models;

public enum NoseGearImpactSeverity
{
    Pass = 0,
    Moderate = 1,
    Severe = 2
}

/// <summary>
/// One verified nose-gear contact or recontact. G values are aircraft-wide values
/// measured in a tight contact window; compression only corroborates the contact.
/// </summary>
public sealed class NoseGearImpactEvent
{
    public double ContactTimeSeconds { get; set; }
    public double? MedianPreContactG { get; set; }
    public double RawPeakG { get; set; }
    public double RobustPeakG { get; set; }
    public double DeltaG { get; set; }
    public int ValidPostContactSamples { get; set; }
    public bool TelemetryDegraded { get; set; }
    public string? DegradedReason { get; set; }
    public bool CompressionTelemetryAvailable { get; set; }
    public bool CompressionCorroborated { get; set; }
    public bool CompressionFallbackUsed { get; set; }
    public List<int> CorrelatedContactPointIndices { get; set; } = new();
    public double? PeakCompression { get; set; }
    public double? CompressionRise { get; set; }
    public NoseGearImpactSeverity Severity { get; set; }
    public double AppliedMultiplier { get; set; } = 1.0;
}

public sealed class NoseGearImpactAnalysis
{
    public bool CoverageSufficient { get; set; }
    public bool NoseGearContactCoverageAvailable { get; set; }
    public bool GForceCoverageAvailable { get; set; }
    public bool CompressionTelemetryCoverageAvailable { get; set; }
    public bool CompressionFallbackUsed { get; set; }
    public List<NoseGearImpactEvent> Events { get; set; } = new();
    public NoseGearImpactEvent? WorstEvent { get; set; }
    public string? DegradedReason { get; set; }
}
