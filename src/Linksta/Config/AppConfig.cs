using System.Text.Json;
using System.Text.Json.Serialization;
using Linksta.Ilias;

namespace Linksta.Config;

public sealed class CourseConfig
{
    /// <summary>Stabile ID (für die Sync-Statusdatei), bleibt bei Umbenennungen gleich.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>Anzeigename / Kurzname (eindeutig).</summary>
    public string Name { get; set; } = "";

    /// <summary>WebDAV-Pfad relativ zur Client-Root, z. B. <c>ref_12345</c>.</summary>
    public string RemotePath { get; set; } = "";

    /// <summary>Ordner relativ zum Sync-Verzeichnis.</summary>
    public string LocalFolder { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>"webdav" (Standard) oder "web" (ILIAS-Weboberfläche, wenn WebDAV den Kurs ausblendet).</summary>
    public string Source { get; set; } = CourseSource.WebDav;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsWeb => Source == CourseSource.Web;

    /// <summary>Zusätzliche Ausschlussmuster nur für diesen Kurs.</summary>
    public List<string> Exclude { get; set; } = [];

    public DateTimeOffset? LastSync { get; set; }
}

public static class CourseSource
{
    public const string WebDav = "webdav";
    public const string Web = "web";
}

public sealed class AppConfig
{
    public string BaseUrl { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string Username { get; set; } = "";
    public string SyncRoot { get; set; } = "";

    /// <summary>Gleichzeitige Downloads.</summary>
    public int Parallel { get; set; } = 4;

    /// <summary>Dateien größer als dieser Wert (MB) werden übersprungen. 0 = unbegrenzt.</summary>
    public int MaxFileSizeMb { get; set; }

    /// <summary>Glob-Muster, die nie heruntergeladen werden (z. B. *.mp4).</summary>
    public List<string> Exclude { get; set; } = [];

    public List<CourseConfig> Courses { get; set; } = [];

    [JsonIgnore]
    public bool IsInitialized =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(SyncRoot);

    [JsonIgnore]
    public Uri WebDavRoot => IliasLink.WebDavRoot(BaseUrl, ClientId);

    public Uri CourseUri(CourseConfig c) => c.IsWeb
        ? IliasLink.RepositoryUrl(BaseUrl, c.RemotePath)
        : IliasLink.Combine(WebDavRoot, c.RemotePath);

    public string CourseLocalPath(CourseConfig c) => Path.Combine(SyncRoot, c.LocalFolder);

    public CourseConfig? FindCourse(string nameOrPath) =>
        Courses.FirstOrDefault(c => c.Name.Equals(nameOrPath, StringComparison.OrdinalIgnoreCase))
        ?? Courses.FirstOrDefault(c => c.RemotePath.Equals(nameOrPath, StringComparison.OrdinalIgnoreCase))
        ?? Courses.FirstOrDefault(c => c.LocalFolder.Equals(nameOrPath, StringComparison.OrdinalIgnoreCase));
}

public static class ConfigStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// ~/.config/linksta (Linux/macOS) bzw. %APPDATA%\linksta (Windows).
    /// Überschreibbar mit LINKSTA_HOME.
    /// </summary>
    public static string ConfigDir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("LINKSTA_HOME");
            if (!string.IsNullOrWhiteSpace(env))
                return env;

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(appData))
                appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(appData, "linksta");
        }
    }

    public static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    public static AppConfig Load()
    {
        if (!File.Exists(ConfigPath))
            return new AppConfig();

        return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), Json) ?? new AppConfig();
    }

    public static void Save(AppConfig config)
    {
        Directory.CreateDirectory(ConfigDir);
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, Json));
        File.Move(tmp, ConfigPath, overwrite: true);
    }

    public static string DefaultSyncRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ILIAS");
}
