using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChallengeLab.Core.FlightLoading;

public sealed class FlightLoadReportStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _directory;

    public FlightLoadReportStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChallengeLab",
            "flight-load-tests");
        Directory.CreateDirectory(_directory);
    }

    public string DirectoryPath => _directory;

    public string Save(FlightLoadResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var stamp = result.RequestedUtc.ToString("yyyyMMdd_HHmmss_fff");
        var path = Path.Combine(_directory, $"{stamp}_{result.AttemptId:N}.json");
        var persisted = result with { ReportPath = path };
        WriteAtomic(path, persisted);
        return path;
    }

    public FlightLoadResult Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("Flight-load report not found.", path);
        return JsonSerializer.Deserialize<FlightLoadResult>(File.ReadAllText(path), JsonOptions)
               ?? throw new InvalidOperationException("Flight-load report is empty or invalid.");
    }

    private static void WriteAtomic(string path, FlightLoadResult result)
    {
        var json = JsonSerializer.Serialize(result, JsonOptions);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(
                       temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       bufferSize: 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
