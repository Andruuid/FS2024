using System.Reflection;
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

        Assert.Equal(0, Resolve(32768, 0, autobrake: true), 6);
        Assert.Equal(0.2, Resolve(32768, 0.2, autobrake: true), 6);
        Assert.Equal(1, Resolve(32768, 0, autobrake: false), 6);
    }
}
