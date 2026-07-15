using System.IO;
using System.Text.Json;

namespace ChallengeLab.App.Views;

public sealed record SecondaryHudPosition(double Left, double Top);

/// <summary>Persists only the monitor position; visibility deliberately remains launch-local.</summary>
public sealed class SecondaryHudPositionStore
{
    private readonly string _path;

    public SecondaryHudPositionStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChallengeLab",
            "hud-settings.json");
    }

    public SecondaryHudPosition? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var settings = JsonSerializer.Deserialize<PositionSettings>(File.ReadAllText(_path));
            if (settings is null || !double.IsFinite(settings.SecondaryLeft) || !double.IsFinite(settings.SecondaryTop))
                return null;
            return new SecondaryHudPosition(settings.SecondaryLeft, settings.SecondaryTop);
        }
        catch
        {
            return null;
        }
    }

    public void Save(double left, double top)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top))
            return;

        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(
                new PositionSettings { SecondaryLeft = left, SecondaryTop = top },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
        }
        catch
        {
            // Position persistence must never prevent the HUD from opening or closing.
        }
    }

    private sealed class PositionSettings
    {
        public double SecondaryLeft { get; set; }
        public double SecondaryTop { get; set; }
    }
}
