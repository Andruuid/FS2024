using System.Reflection;
using System.IO;
using ChallengeLab.SimConnect;

namespace ChallengeLab.App.Tests;

public sealed class OperationalTelemetryTests
{
    [Fact]
    public void ManualBrakeNormalization_IgnoresStandardAutobrakePressure()
    {
        var method = typeof(SimConnectClient).GetMethod(
            "ResolveManualBrakePosition",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        double Resolve(double standard, double pedal, bool autobrake) =>
            Assert.IsType<double>(method!.Invoke(null, new object[] { standard, pedal, autobrake }));

        // Autobrake active: stock pressure and animated pedals are not pilot manual input.
        Assert.Equal(0, Resolve(32768, 0, autobrake: true), 6);
        Assert.Equal(0, Resolve(32768, 0.2, autobrake: true), 6);
        Assert.Equal(1, Resolve(32768, 0, autobrake: false), 6);
        Assert.Equal(0.2, Resolve(0, 0.2, autobrake: false), 6);
    }

    [Fact]
    public void SimConnectDefinition_IncludesAirlinerAutobrakeAndPedalLVars()
    {
        var root = FindRepositoryRoot();
        var client = File.ReadAllText(Path.Combine(
            root, "src", "ChallengeLab.SimConnect", "SimConnectClient.cs"));

        Assert.Contains("AUTOBRAKES ACTIVE", client, StringComparison.Ordinal);
        Assert.Contains("L:A32NX_AUTOBRAKES_ACTIVE", client, StringComparison.Ordinal);
        Assert.Contains("L:INI_AUTOBRAKE_BRAKING", client, StringComparison.Ordinal);
        Assert.Contains("L:INI_AUTOBRAKE_ENGAGED", client, StringComparison.Ordinal);
        Assert.Contains("L:A32NX_LEFT_BRAKE_PEDAL_INPUT", client, StringComparison.Ordinal);
        Assert.Contains("L:A32NX_RIGHT_BRAKE_PEDAL_INPUT", client, StringComparison.Ordinal);
        Assert.Contains("L:LEFT_BRAKE_PEDAL_INPUT", client, StringComparison.Ordinal);
        Assert.Contains("L:INI_BRAKE_AXIS_LEFT", client, StringComparison.Ordinal);
    }

    [Fact]
    public void SimConnectDefinition_ProbesCapabilitiesAndEnablesApplicableFreeNoseTelemetry()
    {
        var root = FindRepositoryRoot();
        var client = File.ReadAllText(Path.Combine(
            root, "src", "ChallengeLab.SimConnect", "SimConnectClient.cs"));
        var trace = File.ReadAllText(Path.Combine(
            root, "src", "ChallengeLab.Core", "Highscores", "LandingTraceStore.cs"));
        var viewModel = File.ReadAllText(Path.Combine(
            root, "src", "ChallengeLab.App", "ViewModels", "MainViewModel.cs"));

        Assert.Contains("CONTACT POINT IS ON GROUND:", client, StringComparison.Ordinal);
        Assert.Contains("CONTACT POINT COMPRESSION:", client, StringComparison.Ordinal);
        Assert.Contains("ContactPointTelemetryAvailable", client, StringComparison.Ordinal);
        Assert.Contains("SIMCONNECT_PERIOD.NEVER", client, StringComparison.Ordinal);
        Assert.Contains("FLAPS NUM HANDLE POSITIONS", client, StringComparison.Ordinal);
        Assert.Contains("SPOILER AVAILABLE", client, StringComparison.Ordinal);
        Assert.Contains("AUTOPILOT AVAILABLE", client, StringComparison.Ordinal);
        Assert.Contains("THROTTLE LOWER LIMIT", client, StringComparison.Ordinal);
        Assert.Contains("noseImpactApplicable", viewModel, StringComparison.Ordinal);
        Assert.Contains("OperationalGates.NoseGearImpact is not null", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("ContactPointCompressionByIndex", trace, StringComparison.Ordinal);
        Assert.DoesNotContain("ContactPointOnGroundByIndex", trace, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ChallengeLab.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("repository root not found");
    }
}
