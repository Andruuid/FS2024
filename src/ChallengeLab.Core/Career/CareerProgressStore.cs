using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChallengeLab.Core.Career;

public sealed class CareerProgressStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _lock = new();

    public CareerProgressStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChallengeLab",
            "career.json");
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
    }

    public string FilePath { get; }
    public string? RecoveryMessage { get; private set; }

    public CareerProgressState Load()
    {
        lock (_lock)
        {
            if (!File.Exists(FilePath)) return new CareerProgressState();
            try
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<CareerProgressState>(json, JsonOptions)
                       ?? throw new InvalidDataException("Career progress JSON was empty.");
            }
            catch (Exception ex)
            {
                var preserved = PreserveInvalidFile("corrupt");
                RecoveryMessage = $"Career progress was corrupt and has been reset. Preserved as {preserved}. ({ex.Message})";
                return new CareerProgressState();
            }
        }
    }

    public CareerProgressState ResetInvalidState(string reason)
    {
        lock (_lock)
        {
            var preserved = File.Exists(FilePath) ? PreserveInvalidFile("obsolete") : null;
            RecoveryMessage = preserved is null
                ? $"Career progress was reset: {reason}"
                : $"Career progress was incompatible and has been reset. Preserved as {preserved}. ({reason})";
            var fresh = new CareerProgressState();
            SaveCore(fresh);
            return fresh;
        }
    }

    public void Save(CareerProgressState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_lock) SaveCore(state);
    }

    private void SaveCore(CareerProgressState state)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(state, JsonOptions);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private string PreserveInvalidFile(string reason)
    {
        var directory = Path.GetDirectoryName(FilePath) ?? ".";
        var baseName = Path.GetFileNameWithoutExtension(FilePath);
        var extension = Path.GetExtension(FilePath);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        var destination = Path.Combine(directory, $"{baseName}.{reason}-{stamp}{extension}");
        File.Move(FilePath, destination);
        return destination;
    }
}
