using System.ComponentModel;
using Linksta.Config;
using Linksta.Ilias;
using Linksta.Sync;
using Linksta.Util;
using Linksta.WebDav;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Linksta.Commands;

public sealed class AddCommand : AsyncCommand<AddCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<LINK>")]
        [Description("Kurs-Link (goto.php?target=crs_123, ilias.php?ref_id=123, WebDAV-Link) oder Ref-ID. Mehrere möglich.")]
        public string[] Links { get; init; } = [];

        [CommandOption("-n|--name <NAME>")]
        [Description("Kurzname für den Kurs (Standard: Titel aus ILIAS).")]
        public string? Name { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var cfg = Shared.RequireConfig();
        using var dav = Shared.CreateClient(cfg);
        using var web = new IliasWebSession(cfg.BaseUrl, cfg.ClientId);
        var failures = 0;

        foreach (var link in s.Links)
        {
            if (!IliasLink.TryGetWebDavPath(link, out var path))
            {
                AnsiConsole.MarkupLine($"[red]✗[/] Kann mit [bold]{Markup.Escape(link)}[/] nichts anfangen. Erwartet: Kurs-Link oder Ref-ID.");
                failures++;
                continue;
            }

            try
            {
                var course = await AddCourse(cfg, dav, path, s.Links.Length == 1 ? s.Name : null, ct, web);
                if (course is not null)
                    AnsiConsole.MarkupLine($"[green]✓[/] [bold]{Markup.Escape(course.Name)}[/] hinzugefügt → [grey]{Markup.Escape(cfg.CourseLocalPath(course))}[/]");
            }
            catch (Exception e) when (e is WebDavException or IliasWebException)
            {
                AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(link)}: {Markup.Escape(e.Message)}");
                failures++;
            }
        }

        ConfigStore.Save(cfg);
        if (failures < s.Links.Length)
            AnsiConsole.MarkupLine("\n[grey]Jetzt[/] [blue]linksta pull[/] [grey]ausführen, um die Dateien zu laden.[/]");
        return failures == 0 ? 0 : 1;
    }

    internal static async Task<CourseConfig?> AddCourse(AppConfig cfg, WebDavClient dav, string path, string? name, CancellationToken ct, IliasWebSession? web = null)
    {
        if (cfg.Courses.FirstOrDefault(c => c.RemotePath.Equals(path, StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            AnsiConsole.MarkupLine($"[yellow]•[/] [bold]{Markup.Escape(existing.Name)}[/] ist schon abonniert.");
            return null;
        }

        var uri = IliasLink.Combine(cfg.WebDavRoot, path);
        var source = CourseSource.WebDav;
        string? title;
        try
        {
            var info = await AnsiConsole.Status().StartAsync($"Prüfe {Markup.Escape(path)} …", async _ => await dav.GetInfoAsync(uri, ct));
            if (info is { IsCollection: false })
                throw new WebDavException("Das ist eine Datei, kein Kurs/Ordner.");
            title = name ?? info?.Name;
        }
        catch (WebDavException e) when (e.HiddenByTitle && web is not null && path.StartsWith("ref_", StringComparison.OrdinalIgnoreCase) && !path.Contains('/'))
        {
            // WebDAV blendet den Kurs aus → über die Weboberfläche synchronisieren
            source = CourseSource.Web;
            title = await AnsiConsole.Status().StartAsync("WebDAV blendet den Kurs aus – prüfe Weboberfläche …", async _ =>
            {
                if (!web.IsLoggedIn)
                    await web.LoginAsync(cfg.Username, Shared.GetPassword(cfg), ct);
                var t = await web.GetTitleAsync(IliasLink.RepositoryUrl(cfg.BaseUrl, path), ct);
                return name ?? t;
            });
            AnsiConsole.MarkupLine("[grey]ℹ Der Titel enthält ein Zeichen, das ILIAS über WebDAV ausblendet – dieser Kurs wird über die Weboberfläche synchronisiert.[/]");
        }

        // WebDAV liefert bei manchen ILIAS-Installationen keinen Anzeigenamen → Titel von der Webseite holen
        if ((string.IsNullOrWhiteSpace(title) || title.StartsWith("ref_", StringComparison.OrdinalIgnoreCase))
            && web is not null && path.StartsWith("ref_", StringComparison.OrdinalIgnoreCase) && !path.Contains('/'))
        {
            try
            {
                title = await AnsiConsole.Status().StartAsync("Hole Kurstitel …", async _ =>
                {
                    if (!web.IsLoggedIn)
                        await web.LoginAsync(cfg.Username, Shared.GetPassword(cfg), ct);
                    return await web.GetTitleAsync(IliasLink.RepositoryUrl(cfg.BaseUrl, path), ct);
                });
            }
            catch (IliasWebException)
            {
                // dann eben fragen
            }
        }

        if (string.IsNullOrWhiteSpace(title) || title.StartsWith("ref_", StringComparison.OrdinalIgnoreCase))
        {
            title = AnsiConsole.Profile.Capabilities.Interactive
                ? AnsiConsole.Prompt(new TextPrompt<string>($"Name für [bold]{Markup.Escape(path)}[/]:").DefaultValue(path))
                : path;
        }

        title = UniqueName(cfg, title.Trim());
        var folder = UniqueFolder(cfg, PathUtil.SanitizeSegment(title));

        var course = new CourseConfig { Name = title, RemotePath = path, LocalFolder = folder, Source = source };
        cfg.Courses.Add(course);
        return course;
    }

    private static string UniqueName(AppConfig cfg, string name)
    {
        var n = name;
        for (var i = 2; cfg.Courses.Any(c => c.Name.Equals(n, StringComparison.OrdinalIgnoreCase)); i++)
            n = $"{name} ({i})";
        return n;
    }

    private static string UniqueFolder(AppConfig cfg, string folder)
    {
        var f = folder;
        for (var i = 2; cfg.Courses.Any(c => c.LocalFolder.Equals(f, StringComparison.OrdinalIgnoreCase)) || f == ".linksta"; i++)
            f = $"{folder} ({i})";
        return f;
    }
}

public sealed class BrowseCommand : AsyncCommand<BrowseCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[START]")]
        [Description("Startpunkt: Ref-ID oder Link eines Ordners (Standard: oberste Ebene des Magazins).")]
        public string? Start { get; init; }
    }

    private sealed record Choice(string Label, string Action, DavItem? Item = null);

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var cfg = Shared.RequireConfig();
        using var dav = Shared.CreateClient(cfg);

        var startPath = "";
        if (s.Start is not null && !IliasLink.TryGetWebDavPath(s.Start, out startPath))
            throw new UserException("Ungültiger Startpunkt.");

        // Pfad-Stack: (Anzeige, relativer WebDAV-Pfad)
        var stack = new Stack<(string Title, string Path)>();
        stack.Push((startPath.Length == 0 ? "ILIAS" : startPath, startPath));
        var added = 0;

        while (stack.Count > 0)
        {
            var (title, path) = stack.Peek();
            var uri = IliasLink.Combine(cfg.WebDavRoot, path);

            IReadOnlyList<DavItem> items;
            try
            {
                items = await AnsiConsole.Status().StartAsync("Lade …", async _ => await dav.ListAsync(uri, ct));
            }
            catch (WebDavException e)
            {
                AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(e.Message)}[/]");
                stack.Pop();
                continue;
            }

            var breadcrumb = string.Join(" › ", stack.Reverse().Select(x => x.Title));
            AnsiConsole.Write(new Rule($"[blue]{Markup.Escape(breadcrumb)}[/]").LeftJustified());

            var folders = items.Where(i => i.IsCollection).OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            var fileCount = items.Count - folders.Count;
            var subscribed = cfg.Courses.Any(c => c.RemotePath.Equals(path, StringComparison.OrdinalIgnoreCase));

            var choices = new List<Choice>();
            if (path.Length > 0)
                choices.Add(new Choice(subscribed ? "[grey]✓ bereits abonniert[/]" : "[green]＋ Diesen Ordner/Kurs abonnieren[/]", subscribed ? "noop" : "add"));
            if (stack.Count > 1)
                choices.Add(new Choice("[yellow]↑ Zurück[/]", "up"));
            choices.AddRange(folders.Select(f => new Choice("📁 " + Markup.Escape(f.Name), "open", f)));
            choices.Add(new Choice("[grey]✕ Fertig[/]", "quit"));

            var pick = AnsiConsole.Prompt(new SelectionPrompt<Choice>()
                .Title(fileCount > 0 ? $"[grey]{fileCount} Datei(en) in diesem Ordner[/]" : " ")
                .PageSize(18)
                .EnableSearch()
                .MoreChoicesText("[grey](↑/↓ für mehr, Tippen zum Suchen)[/]")
                .UseConverter(c => c.Label)
                .AddChoices(choices));

            switch (pick.Action)
            {
                case "open":
                    var segment = Uri.UnescapeDataString(pick.Item!.Href.AbsolutePath.TrimEnd('/').Split('/').Last());
                    stack.Push((pick.Item.Name, path.Length == 0 ? segment : path + "/" + segment));
                    break;
                case "up":
                    stack.Pop();
                    break;
                case "add":
                    var name = AnsiConsole.Prompt(new TextPrompt<string>("Name:").DefaultValue(title));
                    var c = await AddCourse(cfg, dav, path, name, ct);
                    if (c is not null)
                    {
                        ConfigStore.Save(cfg);
                        added++;
                        AnsiConsole.MarkupLine($"[green]✓[/] [bold]{Markup.Escape(c.Name)}[/] abonniert.");
                    }
                    break;
                case "quit":
                    stack.Clear();
                    break;
            }
        }

        if (added > 0)
            AnsiConsole.MarkupLine($"\n{added} Kurs(e) hinzugefügt. Weiter mit [blue]linksta pull[/].");
        return 0;
    }

    private static Task<CourseConfig?> AddCourse(AppConfig cfg, WebDavClient dav, string path, string name, CancellationToken ct) =>
        AddCommand.AddCourse(cfg, dav, path, name, ct);
}

public sealed class ListCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings, CancellationToken ct)
    {
        var cfg = Shared.RequireConfig();
        if (cfg.Courses.Count == 0)
        {
            AnsiConsole.MarkupLine("Noch keine Kurse. [blue]linksta add <Link>[/] oder [blue]linksta browse[/].");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded).Expand()
            .AddColumn("Kurs")
            .AddColumn("Dateien", c => c.RightAligned())
            .AddColumn("Größe", c => c.RightAligned())
            .AddColumn("Letzter Sync")
            .AddColumn("Ordner");

        foreach (var c in cfg.Courses)
        {
            var state = SyncState.Load(cfg.SyncRoot, c.Id);
            var live = state.Files.Values.Where(f => !f.Orphaned).ToList();
            table.AddRow(
                (c.Enabled ? "" : "[grey](pausiert)[/] ") + (c.IsWeb ? "🌐 " : "") + $"[bold]{Markup.Escape(c.Name)}[/]\n[grey]{Markup.Escape(c.RemotePath)}[/]",
                live.Count.ToString(),
                PathUtil.FormatBytes(live.Sum(f => f.LocalSize)),
                Shared.Ago(c.LastSync),
                $"[grey]{Markup.Escape(c.LocalFolder)}[/]");
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]Sync-Ordner: {Markup.Escape(cfg.SyncRoot)}[/]");
        return 0;
    }
}

public sealed class RemoveCommand : Command<RemoveCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<KURS>")]
        public string Course { get; init; } = "";

        [CommandOption("--delete-files")]
        [Description("Auch den lokalen Ordner löschen.")]
        public bool DeleteFiles { get; init; }

        [CommandOption("-y|--yes")]
        [Description("Nicht nachfragen.")]
        public bool Yes { get; init; }
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        var cfg = Shared.RequireConfig();
        var course = Shared.SelectCourses(cfg, [s.Course], includeDisabled: true)[0];
        var local = cfg.CourseLocalPath(course);

        var question = s.DeleteFiles
            ? $"[bold]{Markup.Escape(course.Name)}[/] entfernen und [red]{Markup.Escape(local)} löschen[/]?"
            : $"[bold]{Markup.Escape(course.Name)}[/] entfernen? (Dateien bleiben erhalten)";
        if (!s.Yes && !AnsiConsole.Confirm(question, false))
            return 1;

        cfg.Courses.Remove(course);
        ConfigStore.Save(cfg);
        SyncState.Delete(cfg.SyncRoot, course.Id);

        if (s.DeleteFiles && Directory.Exists(local))
            Directory.Delete(local, recursive: true);

        AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(course.Name)} entfernt.");
        return 0;
    }
}

public sealed class CourseNamesSettings : CommandSettings
{
    [CommandArgument(0, "<KURS>")]
    [Description("Name(n) der Kurse.")]
    public string[] Courses { get; init; } = [];
}

public sealed class PauseCommand : Command<CourseNamesSettings>
{
    public override int Execute(CommandContext context, CourseNamesSettings s, CancellationToken ct) => Toggle(s, false);

    internal static int Toggle(CourseNamesSettings s, bool enable)
    {
        var cfg = Shared.RequireConfig();
        foreach (var c in Shared.SelectCourses(cfg, s.Courses, includeDisabled: true))
        {
            c.Enabled = enable;
            AnsiConsole.MarkupLine($"{(enable ? "[green]▶[/]" : "[yellow]⏸[/]")} {Markup.Escape(c.Name)} {(enable ? "wird wieder synchronisiert" : "pausiert")}.");
        }
        ConfigStore.Save(cfg);
        return 0;
    }
}

public sealed class ResumeCommand : Command<CourseNamesSettings>
{
    public override int Execute(CommandContext context, CourseNamesSettings s, CancellationToken ct) => PauseCommand.Toggle(s, true);
}
