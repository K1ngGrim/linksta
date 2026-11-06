using System.ComponentModel;
using Linksta.Config;
using Linksta.Security;
using Linksta.Sync;
using Linksta.Util;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Linksta.Commands;

/// <summary><c>linksta status</c> = Trockenlauf mit Detailliste.</summary>
public sealed class StatusCommand : PullCommand
{
    protected override bool ForceDryRun => true;
}

public sealed class RecentCommand : Command<RecentCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-d|--days <TAGE>")]
        [Description("Zeitraum in Tagen (Standard: 7).")]
        public int Days { get; init; } = 7;

        [CommandOption("-c|--course <KURS>")]
        public string? Course { get; init; }
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        var cfg = Shared.RequireConfig();
        var since = DateTimeOffset.UtcNow.AddDays(-s.Days);
        var entries = History.Read(cfg.SyncRoot)
            .Where(e => e.Time >= since)
            .Where(e => s.Course is null || e.Course.Contains(s.Course, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Time)
            .ToList();

        if (entries.Count == 0)
        {
            AnsiConsole.MarkupLine($"[grey]In den letzten {s.Days} Tagen wurde nichts Neues geladen.[/]");
            return 0;
        }

        var root = new Tree($"[bold]Neu in den letzten {s.Days} Tagen[/]");
        foreach (var group in entries.GroupBy(e => e.Course))
        {
            var node = root.AddNode($"[blue]{Markup.Escape(group.Key)}[/] [grey]({group.Count()})[/]");
            foreach (var e in group.DistinctBy(e => e.Path))
            {
                var mark = e.Kind == "new" ? "[green]＋[/]" : "[blue]↻[/]";
                node.AddNode($"{mark} {Markup.Escape(e.Path)} [grey]{e.Time.LocalDateTime:dd.MM. HH:mm} · {PathUtil.FormatBytes(e.Size)}[/]");
            }
        }
        AnsiConsole.Write(root);
        return 0;
    }
}

public sealed class SearchCommand : Command<SearchCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<SUCHE>")]
        [Description("Teil des Dateinamens oder Glob-Muster (z. B. \"*blatt*.pdf\").")]
        public string Query { get; init; } = "";

        [CommandOption("-c|--course <KURS>")]
        public string? Course { get; init; }

        [CommandOption("-o|--open")]
        [Description("Treffer auswählen und öffnen.")]
        public bool Open { get; init; }
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        var cfg = Shared.RequireConfig();
        var courses = s.Course is null ? cfg.Courses : Shared.SelectCourses(cfg, [s.Course], true);

        var isGlob = s.Query.IndexOfAny(['*', '?']) >= 0;
        var glob = isGlob ? new GlobMatcher([s.Query]) : null;

        var hits = new List<(CourseConfig Course, string Rel, FileInfo Info)>();
        foreach (var c in courses)
        {
            var root = cfg.CourseLocalPath(c);
            if (!Directory.Exists(root))
                continue;

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (rel.EndsWith(".linksta-part", StringComparison.Ordinal))
                    continue;
                var name = Path.GetFileName(file);
                var match = glob?.IsMatch(name) ?? rel.Contains(s.Query, StringComparison.CurrentCultureIgnoreCase);
                if (match)
                    hits.Add((c, rel, new FileInfo(file)));
            }
        }

        if (hits.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]Keine Treffer.[/]");
            return 1;
        }

        if (s.Open && AnsiConsole.Profile.Capabilities.Interactive)
        {
            var pick = AnsiConsole.Prompt(new SelectionPrompt<(CourseConfig Course, string Rel, FileInfo Info)>()
                .Title($"{hits.Count} Treffer – öffnen:")
                .PageSize(15)
                .EnableSearch()
                .UseConverter(h => $"[blue]{Markup.Escape(h.Course.Name)}[/] {Markup.Escape(h.Rel)}")
                .AddChoices(hits));
            Shared.OpenInFileManager(pick.Info.FullName);
            return 0;
        }

        var table = new Table().Border(TableBorder.Simple)
            .AddColumn("Kurs").AddColumn("Datei").AddColumn(new TableColumn("Größe").RightAligned()).AddColumn("Geändert");
        foreach (var h in hits.OrderBy(h => h.Course.Name).ThenBy(h => h.Rel))
        {
            var link = new Uri(h.Info.FullName).AbsoluteUri;
            table.AddRow(
                Markup.Escape(h.Course.Name),
                $"[link={Markup.Escape(link)}]{Markup.Escape(h.Rel)}[/]",
                PathUtil.FormatBytes(h.Info.Length),
                h.Info.LastWriteTime.ToString("dd.MM.yyyy"));
        }
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]{hits.Count} Treffer[/]");
        return 0;
    }
}

public sealed class OpenCommand : Command<OpenCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[KURS]")]
        [Description("Kurs, dessen Ordner geöffnet werden soll (Standard: Sync-Ordner).")]
        public string? Course { get; init; }
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        var cfg = Shared.RequireConfig();
        var path = s.Course is null ? cfg.SyncRoot : cfg.CourseLocalPath(Shared.SelectCourses(cfg, [s.Course], true)[0]);
        Directory.CreateDirectory(path);
        Shared.OpenInFileManager(path);
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(path)}[/]");
        return 0;
    }
}

public sealed class ConfigShowCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings, CancellationToken ct)
    {
        var cfg = ConfigStore.Load();
        var grid = new Grid().AddColumn(new GridColumn().PadRight(4)).AddColumn();
        void Row(string k, string v) => grid.AddRow($"[grey]{k}[/]", v);

        Row("Config-Datei", Markup.Escape(ConfigStore.ConfigPath));
        Row("url", Markup.Escape(cfg.BaseUrl));
        Row("client", Markup.Escape(cfg.ClientId));
        Row("user", Markup.Escape(cfg.Username));
        Row("dir", Markup.Escape(cfg.SyncRoot));
        Row("parallel", cfg.Parallel.ToString());
        Row("max-size", cfg.MaxFileSizeMb > 0 ? $"{cfg.MaxFileSizeMb} MB" : "[grey]unbegrenzt[/]");
        Row("exclude", cfg.Exclude.Count > 0 ? Markup.Escape(string.Join(", ", cfg.Exclude)) : "[grey]–[/]");
        Row("WebDAV", cfg.IsInitialized ? Markup.Escape(cfg.WebDavRoot.ToString()) : "[grey]–[/]");
        Row("Passwort", cfg.IsInitialized && CredentialStore.Get(cfg.Username) is not null
            ? $"[green]gespeichert[/] [grey]({Markup.Escape(CredentialStore.EnvPassword is not null ? "LINKSTA_PASSWORD" : CredentialStore.BackendName)})[/]"
            : "[yellow]nicht gespeichert[/]");

        AnsiConsole.Write(new Panel(grid).Header("linksta-Konfiguration").Border(BoxBorder.Rounded));

        foreach (var c in cfg.Courses.Where(c => c.Exclude.Count > 0))
            AnsiConsole.MarkupLine($"[grey]exclude ({Markup.Escape(c.Name)}):[/] {Markup.Escape(string.Join(", ", c.Exclude))}");
        return 0;
    }
}

public sealed class ConfigSetCommand : Command<ConfigSetCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<SCHLÜSSEL>")]
        [Description("url | client | user | dir | parallel | max-size")]
        public string Key { get; init; } = "";

        [CommandArgument(1, "<WERT>")]
        public string Value { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        var cfg = ConfigStore.Load();
        switch (s.Key.ToLowerInvariant())
        {
            case "url": cfg.BaseUrl = s.Value.TrimEnd('/'); break;
            case "client": cfg.ClientId = s.Value; break;
            case "user": cfg.Username = s.Value; break;
            case "dir":
                var newDir = Path.GetFullPath(InitCommand.ExpandHome(s.Value));
                if (Directory.Exists(cfg.SyncRoot) && cfg.SyncRoot != newDir)
                    AnsiConsole.MarkupLine($"[yellow]Hinweis:[/] Bestehende Dateien werden nicht verschoben. Verschiebe [grey]{Markup.Escape(cfg.SyncRoot)}[/] (inkl. .linksta) selbst nach [grey]{Markup.Escape(newDir)}[/].");
                cfg.SyncRoot = newDir;
                break;
            case "parallel":
                cfg.Parallel = int.TryParse(s.Value, out var p) && p is >= 1 and <= 16 ? p : throw new UserException("parallel muss zwischen 1 und 16 liegen.");
                break;
            case "max-size":
                cfg.MaxFileSizeMb = int.TryParse(s.Value.ToLowerInvariant().Replace("mb", "").Trim(), out var m) && m >= 0 ? m : throw new UserException("max-size in MB angeben (0 = unbegrenzt).");
                break;
            default:
                throw new UserException($"Unbekannter Schlüssel [bold]{Markup.Escape(s.Key)}[/].");
        }
        ConfigStore.Save(cfg);
        AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(s.Key)} = {Markup.Escape(s.Value)}");
        return 0;
    }
}

public sealed class ExcludeSettings : CommandSettings
{
    [CommandArgument(0, "<MUSTER>")]
    [Description("Glob-Muster, z. B. \"*.mp4\", \"Videos\" oder \"Übungen/**/Lösung*\".")]
    public string[] Patterns { get; init; } = [];

    [CommandOption("-c|--course <KURS>")]
    [Description("Nur für diesen Kurs.")]
    public string? Course { get; init; }
}

public sealed class ExcludeAddCommand : Command<ExcludeSettings>
{
    public override int Execute(CommandContext context, ExcludeSettings s, CancellationToken ct)
    {
        var cfg = Shared.RequireConfig();
        var list = s.Course is null ? cfg.Exclude : Shared.SelectCourses(cfg, [s.Course], true)[0].Exclude;
        foreach (var p in s.Patterns.Where(p => !list.Contains(p)))
        {
            list.Add(p);
            AnsiConsole.MarkupLine($"[green]＋[/] {Markup.Escape(p)}");
        }
        ConfigStore.Save(cfg);
        return 0;
    }
}

public sealed class ExcludeRemoveCommand : Command<ExcludeSettings>
{
    public override int Execute(CommandContext context, ExcludeSettings s, CancellationToken ct)
    {
        var cfg = Shared.RequireConfig();
        var list = s.Course is null ? cfg.Exclude : Shared.SelectCourses(cfg, [s.Course], true)[0].Exclude;
        foreach (var p in s.Patterns)
            AnsiConsole.MarkupLine(list.Remove(p) ? $"[red]－[/] {Markup.Escape(p)}" : $"[grey]{Markup.Escape(p)} war nicht eingetragen[/]");
        ConfigStore.Save(cfg);
        return 0;
    }
}

/// <summary>Diagnose: zeigt das rohe WebDAV-Listing eines Pfads.</summary>
public sealed class LsRemoteCommand : AsyncCommand<LsRemoteCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[PFAD]")]
        [Description("WebDAV-Pfad, Ref-ID oder Link (Standard: Client-Wurzel).")]
        public string? Path { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var cfg = Shared.RequireConfig();
        var path = "";
        if (!string.IsNullOrWhiteSpace(s.Path) && !Linksta.Ilias.IliasLink.TryGetWebDavPath(s.Path, out path))
            path = s.Path.Trim('/');

        using var dav = Shared.CreateClient(cfg);
        var uri = path.Length == 0 ? cfg.WebDavRoot : Linksta.Ilias.IliasLink.Combine(cfg.WebDavRoot, path);
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(uri.ToString())}[/]");

        var items = await dav.ListAsync(uri, ct);
        var table = new Table().Border(TableBorder.Simple).AddColumn("").AddColumn("Name").AddColumn("Href").AddColumn(new TableColumn("Größe").RightAligned());
        foreach (var i in items)
            table.AddRow(i.IsCollection ? "📁" : "📄", Markup.Escape(i.Name), $"[grey]{Markup.Escape(Uri.UnescapeDataString(i.Href.AbsolutePath))}[/]", i.Size is { } sz ? PathUtil.FormatBytes(sz) : "");
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]{items.Count} Einträge[/]");
        return 0;
    }
}
