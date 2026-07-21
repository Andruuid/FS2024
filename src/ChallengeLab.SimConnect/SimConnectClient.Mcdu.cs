using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;
using MsfsSc = Microsoft.FlightSimulator.SimConnect.SimConnect;

namespace ChallengeLab.SimConnect;

public sealed partial class SimConnectClient
{
    public const string McduIlsAircraftTitle = "A320neo V2";
    private readonly SemaphoreSlim _mcduKeyLock = new(1, 1);
    private bool _mcduKeyStructRegistered;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct McduKeyStruct
    {
        public double Value;
    }

    /// <inheritdoc />
    public async Task SetMcduIlsAsync(
        decimal frequencyMhz,
        int? courseDegrees = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (frequencyMhz is < 108.10m or > 111.95m)
            throw new ArgumentOutOfRangeException(nameof(frequencyMhz),
                "ILS frequency must be between 108.10 and 111.95 MHz.");
        if (decimal.Round(frequencyMhz, 2) != frequencyMhz)
            throw new ArgumentOutOfRangeException(nameof(frequencyMhz),
                "ILS frequency can contain at most two decimal places.");
        if (courseDegrees is < 1 or > 360)
            throw new ArgumentOutOfRangeException(nameof(courseDegrees),
                "ILS course must be between 001 and 360 degrees.");
        if (!IsConnected || _sim is null)
            throw new InvalidOperationException("Not connected to the simulator.");

        await _mcduKeyLock.WaitAsync(ct);
        try
        {
            progress?.Report("Checking aircraft for MCDU ILS control…");
            var actualTitle = await RequestAircraftTitleAsync(ct);
            if (!IsMcduIlsAircraft(actualTitle))
            {
                Log($"MCDU ILS blocked — TITLE='{actualTitle ?? "(unknown)"}', expected '{McduIlsAircraftTitle}'.");
                throw new AircraftMismatchException(actualTitle ?? "(unknown)", [McduIlsAircraftTitle]);
            }

            var commands = McduIlsCommandBuilder.Build(frequencyMhz, courseDegrees);
            progress?.Report("Opening the MCDU RAD NAV page…");
            foreach (var command in commands)
            {
                ct.ThrowIfCancellationRequested();
                if (command.Stage == McduKeyStage.Frequency)
                    progress?.Report($"Entering /{frequencyMhz:0.00} in ILS/FREQ…");
                else if (command.Stage == McduKeyStage.Course)
                    progress?.Report($"Entering course {courseDegrees:000}…");

                PressMcduKey(command.LVar);
                await Task.Delay(command.DelayAfterMs, ct);
            }

            var detail = courseDegrees is null
                ? $"/{frequencyMhz:0.00}"
                : $"/{frequencyMhz:0.00} · CRS {courseDegrees:000}";
            Log($"MCDU ILS key sequence sent: {detail}.");
            progress?.Report($"MCDU commands sent: {detail}. Verify RAD NAV in the aircraft.");
        }
        finally
        {
            _mcduKeyLock.Release();
        }
    }

    internal static bool IsMcduIlsAircraft(string? title) =>
        AircraftTitleMatches(title, [McduIlsAircraftTitle]);

    private void PressMcduKey(string lvar)
    {
        var sim = _sim ?? throw new InvalidOperationException("Not connected to the simulator.");
        var definitionAdded = false;
        try
        {
            sim.AddToDataDefinition(
                Definitions.McduKey,
                lvar,
                "number",
                SIMCONNECT_DATATYPE.FLOAT64,
                0,
                MsfsSc.SIMCONNECT_UNUSED);
            definitionAdded = true;
            if (!_mcduKeyStructRegistered)
            {
                sim.RegisterDataDefineStruct<McduKeyStruct>(Definitions.McduKey);
                _mcduKeyStructRegistered = true;
            }

            sim.SetDataOnSimObject(
                Definitions.McduKey,
                MsfsSc.SIMCONNECT_OBJECT_ID_USER,
                SIMCONNECT_DATA_SET_FLAG.DEFAULT,
                new McduKeyStruct { Value = 1.0 });
        }
        finally
        {
            if (definitionAdded)
                sim.ClearDataDefinition(Definitions.McduKey);
        }
    }
}

internal sealed record McduKeyCommand(
    string LVar,
    int DelayAfterMs,
    McduKeyStage Stage = McduKeyStage.None);

internal enum McduKeyStage
{
    None,
    Frequency,
    Course
}

/// <summary>Pure sequence builder kept separate so every cockpit key can be regression-tested.</summary>
internal static class McduIlsCommandBuilder
{
    private const int KeyDelayMs = 150;
    private const int PageDelayMs = 300;

    internal static IReadOnlyList<McduKeyCommand> Build(decimal frequencyMhz, int? courseDegrees)
    {
        var commands = new List<McduKeyCommand>
        {
            new("L:INI_MCDU1_RADNAV", PageDelayMs),
            new("L:INI_MCDU1_SLASH", KeyDelayMs, McduKeyStage.Frequency)
        };

        foreach (var character in frequencyMhz.ToString("0.00", CultureInfo.InvariantCulture))
            commands.Add(new McduKeyCommand(KeyLVar(character), KeyDelayMs));
        commands.Add(new McduKeyCommand("L:INI_MCDU1_LSK3L", PageDelayMs));

        if (courseDegrees is not null)
        {
            var course = courseDegrees.Value.ToString("000", CultureInfo.InvariantCulture);
            commands.Add(new McduKeyCommand(KeyLVar(course[0]), KeyDelayMs, McduKeyStage.Course));
            foreach (var character in course.Skip(1))
                commands.Add(new McduKeyCommand(KeyLVar(character), KeyDelayMs));
            commands.Add(new McduKeyCommand("L:INI_MCDU1_LSK4L", PageDelayMs));
        }

        return commands;
    }

    internal static IReadOnlyList<string> EffectiveLVars(decimal frequencyMhz, int? courseDegrees) =>
        Build(frequencyMhz, courseDegrees).Select(command => command.LVar).ToArray();

    private static string KeyLVar(char character) => character switch
    {
        >= '0' and <= '9' => $"L:INI_MCDU1_{character}",
        '.' => "L:INI_MCDU1_DECIMAL",
        '/' => "L:INI_MCDU1_SLASH",
        _ => throw new InvalidOperationException($"Unsupported MCDU character '{character}'.")
    };
}
