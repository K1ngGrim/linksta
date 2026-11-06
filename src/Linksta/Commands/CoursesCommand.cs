using System.ComponentModel;
using Linksta.Config;
using Linksta.Ilias;
using Linksta.WebDav;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Linksta.Commands;

/// <summary>Liest „Meine Kurse und Gruppen“ aus ILIAS und lässt sie zum Abonnieren auswählen.</summary>
public sealed class CoursesCommand : AsyncCommand<CoursesCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-l|--list")]
        [Description("Nur auflisten, nichts abonnieren.")]
        public bool List { get; init; }

        [CommandOption("-a|--all")]
        [Description("Alle Kurse ohne Nachfrage abonnieren.")]
        public bool All { get; init; }
    }

    private const string HiddenMessage = "hidden-by-title";

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var cfg = Shared.RequireConfig();
        var password = Shared.GetPassword(cfg);

        IReadOnlyList<MembershipItem> items;
        using var web = new IliasWebSession(cfg.BaseUrl, cfg.ClientId);
        {
            items = await AnsiConsole.Status().StartAsync("Melde mich bei ILIAS an …", async ctx =>
            {
                await web.LoginAsync(cfg.Username, password, ct);
                ctx.Status("Lade „Meine Kurse und Gruppen“ …");
                return await web.GetMembershipsAsync(ct);
            });
        }

        if (items.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Keine Kurse im Dashboard gefunden.[/] Nutze [blue]linksta browse[/] oder [blue]linksta add <Link>[/].");
            return 1;
        }

        // Vorab prüfen, welche Kurse per WebDAV erreichbar sind
        using var dav = new WebDavClient(cfg.Username, password, maxRetries: 1);
        var problems = new System.Collections.Concurrent.ConcurrentDictionary<MembershipItem, string>();
        await AnsiConsole.Status().StartAsync($"Prüfe {items.Count} Kurse auf WebDAV-Zugriff …", async _ =>
            await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (i, token) =>
            {
                try
                {
                    await dav.GetInfoAsync(IliasLink.Combine(cfg.WebDavRoot, i.RefPath), token);
                }
                catch (WebDavException e)
                {
                    problems[i] = e.HiddenByTitle ? HiddenMessage : e.Message;
                }
            }));

        // Vom Titel ausgeblendete Kurse gehen über die Weboberfläche – nur echte Fehler aussortieren
        var webOnly = items.Where(i => problems.TryGetValue(i, out var msg) && msg == HiddenMessage).ToHashSet();
        var unavailable = items.Where(i => problems.ContainsKey(i) && !webOnly.Contains(i)).ToList();
        items = items.Where(i => !unavailable.Contains(i)).ToList();

        if (webOnly.Count > 0)
            AnsiConsole.MarkupLine($"[grey]🌐 = {webOnly.Count} Kurs(e) mit „/“ im Titel; ILIAS blendet sie über WebDAV aus, linksta lädt sie über die Weboberfläche.[/]\n");

        if (unavailable.Count > 0)
        {
            AnsiConsole.MarkupLine($"[yellow]⚠ {unavailable.Count} Kurs(e) sind über WebDAV nicht erreichbar:[/]");
            foreach (var i in unavailable)
                AnsiConsole.MarkupLine($"  [grey]• {Markup.Escape(i.Title)}[/]");
            var first = problems[unavailable[0]];
            AnsiConsole.MarkupLine(unavailable.All(i => problems[i] == first)
                ? $"  [grey]Grund: {Markup.Escape(first)}[/]\n"
                : "  [grey]Details: linksta ls-remote <ref_…>[/]\n");
        }

        if (items.Count == 0)
            return 1;

        bool IsSubscribed(MembershipItem i) =>
            cfg.Courses.Any(c => c.RemotePath.Equals(i.RefPath, StringComparison.OrdinalIgnoreCase));

        if (s.List || !AnsiConsole.Profile.Capabilities.Interactive && !s.All)
        {
            var table = new Table().Border(TableBorder.Simple).AddColumn("").AddColumn("Kurs").AddColumn("Typ").AddColumn("Ref");
            foreach (var i in items)
                table.AddRow(IsSubscribed(i) ? "[green]✓[/]" : "", (webOnly.Contains(i) ? "🌐 " : "") + Markup.Escape(i.Title), i.Type == "grp" ? "Gruppe" : "Kurs", $"[grey]{i.RefPath}[/]");
            AnsiConsole.Write(table);
            return 0;
        }

        List<MembershipItem> chosen;
        if (s.All)
        {
            chosen = items.Where(i => !IsSubscribed(i)).ToList();
        }
        else
        {
            var prompt = new MultiSelectionPrompt<MembershipItem>()
                .Title($"[bold]Deine Kurse[/] [grey]({items.Count}) – Leertaste = auswählen, Enter = übernehmen[/]")
                .PageSize(20)
                .NotRequired()
                .InstructionsText("[grey](bereits abonnierte sind vorausgewählt)[/]")
                .UseConverter(i => (i.Type == "grp" ? "👥 " : "🎓 ") + Markup.Escape(i.Title) + (webOnly.Contains(i) ? " [grey]🌐[/]" : ""))
                .AddChoices(items);
            foreach (var i in items.Where(IsSubscribed))
                prompt.Select(i);

            var picked = AnsiConsole.Prompt(prompt);
            chosen = picked.Where(i => !IsSubscribed(i)).ToList();

            var unpicked = items.Where(i => IsSubscribed(i) && !picked.Contains(i)).ToList();
            foreach (var i in unpicked)
            {
                var c = cfg.Courses.First(c => c.RemotePath.Equals(i.RefPath, StringComparison.OrdinalIgnoreCase));
                if (AnsiConsole.Confirm($"Abo [bold]{Markup.Escape(c.Name)}[/] beenden? (Dateien bleiben erhalten)", false))
                {
                    cfg.Courses.Remove(c);
                    Sync.SyncState.Delete(cfg.SyncRoot, c.Id);
                    AnsiConsole.MarkupLine($"[red]－[/] {Markup.Escape(c.Name)}");
                }
            }
        }

        var added = 0;
        foreach (var i in chosen)
        {
            try
            {
                var c = await AddCommand.AddCourse(cfg, dav, i.RefPath, i.Title, ct, web);
                if (c is not null)
                {
                    added++;
                    AnsiConsole.MarkupLine($"[green]＋[/] {Markup.Escape(c.Name)}");
                }
            }
            catch (Exception e) when (e is WebDavException or IliasWebException)
            {
                AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(i.Title)}: {Markup.Escape(e.Message)}");
            }
        }

        ConfigStore.Save(cfg);
        AnsiConsole.MarkupLine(added > 0
            ? $"\n{added} Kurs(e) abonniert. Weiter mit [blue]linksta pull[/]."
            : "\n[grey]Keine neuen Kurse abonniert.[/]");
        return 0;
    }
}
