using System.Text.Json;
using System.Text.Json.Serialization;

namespace Linksta.Sync;

public sealed class FileRecord
{
    public string Href { get; set; } = "";
    public long? RemoteSize { get; set; }
    public DateTimeOffset? RemoteModified { get; set; }
    public string? ETag { get; set; }
    public long LocalSize { get; set; }
    public DateTime LocalModifiedUtc { get; set; }
    public DateTimeOffset SyncedAt { get; set; }

    /// <summary>Datei existiert auf ILIAS nicht mehr, wurde lokal aber behalten.</summary>
    public bool Orphaned { get; set; }
}

public sealed class SyncState
{
    public int Version { get; set; } = 1;

    /// <summary>Schlüssel: relativer Pfad mit "/" als Trenner.</summary>
    public Dictionary<string, FileRecord> Files { get; set; } = new(StringComparer.Ordinal);

    [JsonIgnore]
    public string? FilePath { get; private set; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string MetaDir(string syncRoot) => Path.Combine(syncRoot, ".linksta");

    public static SyncState Load(string syncRoot, string courseId)
    {
        var path = Path.Combine(MetaDir(syncRoot), $"{courseId}.json");
        SyncState state;
        if (File.Exists(path))
        {
            state = JsonSerializer.Deserialize<SyncState>(File.ReadAllText(path), Json) ?? new SyncState();
            state.Files = new Dictionary<string, FileRecord>(state.Files, StringComparer.Ordinal);
        }
        else
        {
            state = new SyncState();
        }
        state.FilePath = path;
        return state;
    }

    public void Save()
    {
        if (FilePath is null)
            throw new InvalidOperationException("State has no path.");

        var dir = Path.GetDirectoryName(FilePath)!;
        if (!Directory.Exists(dir))
        {
            var info = Directory.CreateDirectory(dir);
            if (OperatingSystem.IsWindows())
                info.Attributes |= FileAttributes.Hidden;
        }

        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public static void Delete(string syncRoot, string courseId)
    {
        var path = Path.Combine(MetaDir(syncRoot), $"{courseId}.json");
        if (File.Exists(path))
            File.Delete(path);
    }
}

public sealed record HistoryEntry(DateTimeOffset Time, string Course, string Path, string Kind, long Size);

public static class History
{
    private static readonly object Lock = new();

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string FilePath(string syncRoot) => Path.Combine(SyncState.MetaDir(syncRoot), "history.jsonl");

    public static void Append(string syncRoot, IEnumerable<HistoryEntry> entries)
    {
        var lines = entries.Select(e => JsonSerializer.Serialize(e, Json)).ToList();
        if (lines.Count == 0)
            return;
        lock (Lock)
        {
            Directory.CreateDirectory(SyncState.MetaDir(syncRoot));
            File.AppendAllLines(FilePath(syncRoot), lines);
        }
    }

    public static IReadOnlyList<HistoryEntry> Read(string syncRoot)
    {
        var path = FilePath(syncRoot);
        if (!File.Exists(path))
            return [];

        var list = new List<HistoryEntry>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                if (JsonSerializer.Deserialize<HistoryEntry>(line, Json) is { } e)
                    list.Add(e);
            }
            catch (JsonException)
            {
                // kaputte Zeile überspringen
            }
        }
        return list;
    }
}
