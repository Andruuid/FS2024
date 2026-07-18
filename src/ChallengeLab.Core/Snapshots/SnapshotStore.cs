using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChallengeLab.Core.Snapshots;

/// <summary>
/// Flight-state snapshots for the STORE tab.
/// Files live under %LocalAppData%\ChallengeLab\snapshots\ as
/// {yyyyMMdd_HHmmss}_{safeName}_{guid:N}.json (same scheme as flight tapes).
/// Writes are atomic (temp file + move) so a crash cannot corrupt a snapshot.
/// </summary>
public sealed class SnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _directory;

    public SnapshotStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChallengeLab",
            "snapshots");
        Directory.CreateDirectory(_directory);
    }

    public string DirectoryPath => _directory;

    /// <summary>Write a snapshot; returns the file path.</summary>
    public string Save(FlightStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Id == Guid.Empty) snapshot.Id = Guid.NewGuid();
        if (snapshot.CreatedUtc == default) snapshot.CreatedUtc = DateTimeOffset.UtcNow;

        var path = BuildPath(snapshot);
        WriteAtomic(path, snapshot);
        return path;
    }

    public FlightStateSnapshot Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("Snapshot not found.", path);

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<FlightStateSnapshot>(json, JsonOptions)
               ?? throw new InvalidOperationException("Snapshot file is empty or invalid.");
    }

    public IReadOnlyList<SnapshotListItem> List()
    {
        if (!Directory.Exists(_directory))
            return Array.Empty<SnapshotListItem>();

        var items = new List<SnapshotListItem>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json")
                     .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                items.Add(SnapshotListItem.From(Load(path), path));
            }
            catch
            {
                // Skip unreadable files so one bad snapshot does not break the list.
            }
        }

        return items;
    }

    /// <summary>
    /// Rename: updates the JSON Name and moves the file to a filename derived from the
    /// new name while keeping the original timestamp + guid. Returns the new path.
    /// </summary>
    public string Rename(string path, string newName)
    {
        var snapshot = Load(path);
        snapshot.Name = (newName ?? "").Trim();

        var newPath = BuildPath(snapshot);
        WriteAtomic(newPath, snapshot);
        if (!string.Equals(newPath, path, StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            File.Delete(path);
        return newPath;
    }

    public void Delete(string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            File.Delete(path);
    }

    /// <summary>Filename-safe version of a name part (invalid chars → "_").</summary>
    public static string SanitizeFileNamePart(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) return "snapshot";
        var joined = string.Join("_", trimmed.Split(Path.GetInvalidFileNameChars()));
        joined = string.Join("_", joined.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return joined.Length > 80 ? joined[..80] : joined;
    }

    private string BuildPath(FlightStateSnapshot snapshot)
    {
        var stamp = snapshot.CreatedUtc.ToString("yyyyMMdd_HHmmss");
        var safeName = SanitizeFileNamePart(snapshot.Name);
        return Path.Combine(_directory, $"{stamp}_{safeName}_{snapshot.Id:N}.json");
    }

    private static void WriteAtomic(string path, FlightStateSnapshot snapshot)
    {
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
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

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
