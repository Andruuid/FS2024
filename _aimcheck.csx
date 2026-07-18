using ChallengeLab.Core.Scoring;

// Compare user Google-measure totals (often far edge of paint) vs formula
var measurements = new (string label, double measuredM)[]
{
    ("#1", 446.25), ("#2", 368.89), ("#3", 429.38), ("#4", 391.05),
    ("#5", 476.15), ("#6", 444.54), ("#7", 456.51), ("#8", 499.43),
    ("#9", 506.21), ("#10", 465.62), ("#11", 463.78), ("#12", 460.76),
    ("#13", 460.56), ("#14", 484.04), ("#15", 455.12), ("#16", 503.77),
    ("#17", 419.15), ("#18", 359.58), ("#19", 356.80),
};

// Reference estimates: US long, ICAO long, ICAO medium
var refs = new[]
{
    GrokAimingMarkerHelper.GetAimingMarkerStart("KMIA", "09", 3500),
    GrokAimingMarkerHelper.GetAimingMarkerStart("LSZH", "14", 3300),
    GrokAimingMarkerHelper.GetAimingMarkerStart("XXXX", "09", 2000),
};

Console.WriteLine("FORMULA REFERENCES");
foreach (var r in refs)
{
    var end = r.StartMetersFromThreshold + r.StripeLengthMeters;
    Console.WriteLine($"{r.Regime,-8} start={r.StartMetersFromThreshold,6:0.0} mid={r.MidMetersFromThreshold,6:0.0} end={end,6:0.0}  stripe={r.StripeLengthMeters:0.0}  ({r.Reason})");
}

Console.WriteLine();
Console.WriteLine("MEAS vs closest formula point (start/mid/end of FAA long, ICAO long, ICAO med)");

// Build candidate points
var candidates = new List<(string name, double m)>();
void Add(string name, AimingMarkerEstimate e)
{
    candidates.Add(($"{name}.start", e.StartMetersFromThreshold));
    candidates.Add(($"{name}.mid", e.MidMetersFromThreshold));
    candidates.Add(($"{name}.end", e.StartMetersFromThreshold + e.StripeLengthMeters));
}
Add("FAA", refs[0]);
Add("ICAO400", refs[1]);
// also ICAO with max stripe 60m end
candidates.Add(("ICAO400.end60", 400 + 60));
candidates.Add(("ICAO400.end45", 400 + 45));
candidates.Add(("ICAO300.end45", 300 + 45));
candidates.Add(("ICAO300.end60", 300 + 60));
candidates.Add(("FAA.end150ft", 311 + 45.72));
candidates.Add(("FAA.start1020", 311));

foreach (var (label, meas) in measurements)
{
    var best = candidates.OrderBy(c => Math.Abs(c.m - meas)).First();
    var err = meas - best.m;
    Console.WriteLine($"{label,4} meas={meas,7:0.00}m  best={best.name,-16} pred={best.m,7:0.00}  err={err,+7:0.0}m");
}

// Cluster analysis
Console.WriteLine();
Console.WriteLine("CLUSTER FIT: how many measurements within X of a model end/start");
void CountNear(string model, double target, double tol)
{
    var n = measurements.Count(m => Math.Abs(m.measuredM - target) <= tol);
    Console.WriteLine($"  within {tol}m of {model} ({target:0.0}m): {n}/{measurements.Length}");
}
CountNear("FAA end (311+45.7)", 311+45.72, 15);
CountNear("FAA start 311", 311, 15);
CountNear("ICAO start 400", 400, 15);
CountNear("ICAO end 400+52.5", 400+52.5, 15);
CountNear("ICAO end 400+45", 445, 15);
CountNear("ICAO end 400+60", 460, 20);
CountNear("ICAO mid 426", 426.25, 15);
