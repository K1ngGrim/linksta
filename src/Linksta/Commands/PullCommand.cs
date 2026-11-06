using System.ComponentModel;
using Linksta.Config;
using Linksta.Sync;
using Linksta.Util;
using Linksta.WebDav;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Linksta.Commands;

public class PullSettings : CommandSettings
{
    [CommandArgument(0, "[KURS]")]
    [Description("Nur diese Kurse (Name oder Teil davon). Standard: alle aktiven.")]
    public string[] Courses { get; init; } = [];

    [CommandOption("-n|--dry-run")]
    [Description("Nur anzeigen, was passieren würde.")]
    public bool DryRun { get; init; }

    [CommandOption("--prune")]
    [Description("Auf ILIAS gelöschte Dateien lokal in den Papierkorb (.linksta/trash) verschieben.")]
    public bool Prune { get; init; }

    [CommandOption("--overwrite")]
    [Description("Bei Konflikten lokale Änderungen überschreiben (statt eine Kopie zu behalten).")]
    public bool Overwrite { get; init; }

    [CommandOption("-j|--parallel <N>")]
    [Description("Gleichzeitige Downloads (Standard aus Config, sonst 4).")]
    public int? Parallel { get; init; }

    [CommandOption("-v|--verbose")]
    [Description("Alle Änderungen einzeln auflisten.")]
    public bool Verbose { get; init; }

    [CommandOption("-q|--quiet")]
    [Description("Nur Fehler und Zusammenfassung ausgeben (gut für Cronjobs).")]
    public bool Quiet { get; init; }
}

public class PullCommand : AsyncCommand<PullSettings>
{
    protected virtual bool ForceDryRun => false;

    public override async Task<int> ExecuteAsync(CommandContext context, PullSettings s, CancellationToken ct)
    {
        var cfg = Shared.RequireConfig();
        var courses = Shared.SelectCourses(cfg, s.Courses);
        if (courses.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Alle Kurse sind pausiert.[/] [grey]linksta resume <Kurs>[/]");
            return 0;
        }

        var dryRun = s.DryRun || ForceDryRun;
        var parallel = Math.Clamp(s.Parallel ?? cfg.Parallel, 1, 16);
        var maxBytes = cfg.MaxFileSizeMb > 0 ? cfg.MaxFileSizeMb * 1024L * 1024L : 0;

        using var dav = Shared.CreateClient(cfg);
        var davScanner = new RemoteScanner(dav, parallel);
        var davEngine = new SyncEngine(dav);

        // Web-Zugang nur, wenn ein Kurs ihn braucht (Login kostet einen Moment)
        using var web = new Linksta.Ilias.IliasWebSession(cfg.BaseUrl, cfg.ClientId);
        var webScanner = new WebScanner(web, parallel);
        var webEngine = new SyncEngine(web);

        var allNew = new List<(CourseConfig Course, PlanItem Item)>();
        var exit = 0;
        long totalBytes = 0;

        foreach (var course in courses)
        {
            ct.ThrowIfCancellationRequested();
            if (!s.Quiet)
                AnsiConsole.Write(new Rule($"[bold blue]{Markup.Escape(course.Name)}[/]").LeftJustified().RuleStyle("grey"));

            ICourseScanner scanner = course.IsWeb ? webScanner : davScanner;
            var engine = course.IsWeb ? webEngine : davEngine;

            ScanResult scan;
            try
            {
                if (course.IsWeb && !web.IsLoggedIn)
                    await AnsiConsole.Status().StartAsync("Melde mich bei der ILIAS-Weboberfläche an …", async _ =>
                        await web.LoginAsync(cfg.Username, Shared.GetPassword(cfg), ct));

                scan = await AnsiConsole.Status().Spinner(Spinner.Known.Dots).StartAsync("Lese Kursinhalt …", async ctx =>
                    await scanner.ScanAsync(cfg.CourseUri(course), folder => ctx.Status($"Lese [grey]{Markup.Escape(Trim(folder, 60))}[/]"), ct));
            }
            catch (WebDavException e)
            {
                AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(course.Name)}: {Markup.Escape(e.Message)}[/]");
                exit = 1;
                if (e.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    return exit;
                continue;
            }
            catch (Linksta.Ilias.IliasWebException e)
            {
                AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(course.Name)}: {Markup.Escape(e.Message)}[/]");
                exit = 1;
                continue;
            }

            if (scan.Errors.Count > 0 && scan.Errors.Any(e => e.RelPath.Length == 0))
            {
                AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(course.Name)}: {Markup.Escape(scan.Errors[0].Message)}[/]");
                exit = 1;
                if (scan.Errors[0].Message.Contains("401"))
                    return exit;
                continue;
            }

            var localRoot = cfg.CourseLocalPath(course);
            var state = SyncState.Load(cfg.SyncRoot, course.Id);
            var matcher = new GlobMatcher(cfg.Exclude.Concat(course.Exclude));
            var plan = SyncPlanner.Plan(scan, state, localRoot, matcher, maxBytes);

            foreach (var err in scan.Errors)
                AnsiConsole.MarkupLine($"[yellow]⚠ Ordner übersprungen:[/] {Markup.Escape(err.RelPath)} [grey]({Markup.Escape(err.Message)})[/]");

            if (dryRun)
            {
                PrintPlan(plan, s.Verbose || ForceDryRun);
                continue;
            }

            SyncOutcome outcome;
            var downloads = plan.Downloads.ToList();
            if (downloads.Count == 0)
            {
                outcome = await engine.ExecuteAsync(course.Name, cfg.SyncRoot, localRoot, state, plan,
                    new SyncOptions { Parallel = parallel, Prune = s.Prune, OverwriteLocal = s.Overwrite }, null, ct);
            }
            else
            {
                outcome = await AnsiConsole.Progress()
                    .AutoClear(true)
                    .HideCompleted(true)
                    .Columns(
                        new TaskDescriptionColumn { Alignment = Justify.Left },
                        new ProgressBarColumn(),
                        new PercentageColumn(),
                        new DownloadedColumn(),
                        new TransferSpeedColumn(),
                        new RemainingTimeColumn())
                    .StartAsync(async ctx =>
                    {
                        var reporter = new ProgressReporter(ctx, downloads.Count, plan.DownloadBytes);
                        return await engine.ExecuteAsync(course.Name, cfg.SyncRoot, localRoot, state, plan,
                            new SyncOptions { Parallel = parallel, Prune = s.Prune, OverwriteLocal = s.Overwrite }, reporter, ct);
                    });
            }

            course.LastSync = DateTimeOffset.UtcNow;
            ConfigStore.Save(cfg);
            totalBytes += outcome.Bytes;

            PrintOutcome(plan, outcome, s.Verbose, s.Quiet);
            allNew.AddRange(outcome.Downloaded.Where(i => i.Kind is ChangeKind.New or ChangeKind.Updated).Select(i => (course, i)));
            if (!outcome.Failed.IsEmpty)
                exit = 1;
        }

        if (!dryRun && courses.Count > 1 && !s.Quiet)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(allNew.Count == 0
                ? "[green]✓ Alles aktuell.[/]"
                : $"[green]✓ {allNew.Count} neue/aktualisierte Datei(en), {PathUtil.FormatBytes(totalBytes)} geladen.[/]");
        }
        else if (!dryRun && s.Quiet && allNew.Count > 0)
        {
            foreach (var (c, i) in allNew)
                AnsiConsole.WriteLine($"{(i.Kind == ChangeKind.New ? "+" : "~")} {c.Name}/{i.RelPath}");
        }

        return exit;
    }

    private static void PrintPlan(SyncPlan plan, bool verbose)
    {
        var rows = plan.Items.Where(i => i.Kind is not (ChangeKind.Unchanged or ChangeKind.Orphaned) && (verbose || i.Kind is not ChangeKind.Excluded)).ToList();
        if (rows.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]✓ Aktuell.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Simple).AddColumn("").AddColumn("Datei").AddColumn(new TableColumn("Größe").RightAligned());
        foreach (var i in rows.OrderBy(r => r.Kind).ThenBy(r => r.RelPath, StringComparer.Ordinal))
            table.AddRow(KindLabel(i.Kind), Markup.Escape(i.RelPath), i.Remote?.Size is { } sz ? PathUtil.FormatBytes(sz) : "");
        AnsiConsole.Write(table);

        var dl = plan.Downloads.Count();
        AnsiConsole.MarkupLine(dl > 0
            ? $"[blue]→ {dl} Datei(en) würden geladen ({PathUtil.FormatBytes(plan.DownloadBytes)}).[/]"
            : "[green]Nichts herunterzuladen.[/]");
    }

    private static void PrintOutcome(SyncPlan plan, SyncOutcome o, bool verbose, bool quiet)
    {
        foreach (var (item, msg) in o.Failed.OrderBy(f => f.Item.RelPath))
            AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(item.RelPath)}:[/] {Markup.Escape(msg)}");

        if (quiet)
            return;

        var downloaded = o.Downloaded.OrderBy(i => i.RelPath, StringComparer.Ordinal).ToList();
        var show = verbose ? downloaded : downloaded.Take(15).ToList();
        foreach (var i in show)
            AnsiConsole.MarkupLine($"  {KindLabel(i.Kind)} {Markup.Escape(i.RelPath)} [grey]{(i.Remote?.Size is { } sz ? PathUtil.FormatBytes(sz) : "")}[/]");
        if (downloaded.Count > show.Count)
            AnsiConsole.MarkupLine($"  [grey]… und {downloaded.Count - show.Count} weitere (mit -v alle anzeigen)[/]");

        foreach (var (item, kept) in o.ConflictCopies)
            AnsiConsole.MarkupLine($"  [yellow]⚠ Konflikt:[/] {Markup.Escape(item.RelPath)} – deine Version wurde als [bold]{Markup.Escape(kept)}[/] behalten.");

        foreach (var item in plan.Items.Where(i => i.Kind == ChangeKind.LocalModified))
            AnsiConsole.MarkupLine($"  [grey]✎ lokal geändert, nicht überschrieben: {Markup.Escape(item.RelPath)}[/]");

        foreach (var item in o.Pruned)
            AnsiConsole.MarkupLine($"  [red]－[/] {Markup.Escape(item.RelPath)} [grey](auf ILIAS gelöscht → Papierkorb)[/]");

        var deleted = plan.Items.Where(i => i.Kind == ChangeKind.RemoteDeleted).ToList();
        if (deleted.Count > 0 && o.Pruned.Count == 0)
        {
            foreach (var item in deleted.Take(10))
                AnsiConsole.MarkupLine($"  [grey]⌫ auf ILIAS gelöscht (lokal behalten): {Markup.Escape(item.RelPath)}[/]");
            AnsiConsole.MarkupLine("  [grey]Mit --prune in den Papierkorb verschieben.[/]");
        }

        var parts = new List<string>();
        if (plan.Count(ChangeKind.New) > 0) parts.Add($"[green]{plan.Count(ChangeKind.New)} neu[/]");
        if (plan.Count(ChangeKind.Updated) > 0) parts.Add($"[blue]{plan.Count(ChangeKind.Updated)} aktualisiert[/]");
        if (plan.Count(ChangeKind.Restore) > 0) parts.Add($"{plan.Count(ChangeKind.Restore)} wiederhergestellt");
        if (plan.Count(ChangeKind.Conflict) > 0) parts.Add($"[yellow]{plan.Count(ChangeKind.Conflict)} Konflikt(e)[/]");
        if (!o.Failed.IsEmpty) parts.Add($"[red]{o.Failed.Count} fehlgeschlagen[/]");

        var total = plan.Items.Count(i => i.Remote is not null);
        var info = new List<string> { $"{total} Dateien" };
        if (plan.Count(ChangeKind.TooLarge) > 0) info.Add($"{plan.Count(ChangeKind.TooLarge)} zu groß");
        if (plan.Count(ChangeKind.Excluded) > 0) info.Add($"{plan.Count(ChangeKind.Excluded)} ausgeschlossen");
        if (o.Bytes > 0) info.Add($"{PathUtil.FormatBytes(o.Bytes)} geladen");
        var infoText = $"[grey]({string.Join(", ", info)})[/]";

        AnsiConsole.MarkupLine(parts.Count == 0
            ? $"[green]✓ Aktuell[/] {infoText}"
            : $"{(o.Failed.IsEmpty ? "[green]✓[/]" : "[red]✗[/]")} {string.Join(", ", parts)} {infoText}");
    }

    internal static string KindLabel(ChangeKind k) => k switch
    {
        ChangeKind.New => "[green]＋[/]",
        ChangeKind.Updated => "[blue]↻[/]",
        ChangeKind.Restore => "[blue]⤓[/]",
        ChangeKind.Conflict => "[yellow]⚠[/]",
        ChangeKind.Adopt => "[grey]≡[/]",
        ChangeKind.LocalModified => "[grey]✎[/]",
        ChangeKind.RemoteDeleted => "[red]⌫[/]",
        ChangeKind.Excluded => "[grey]⊘[/]",
        ChangeKind.TooLarge => "[grey]⇲[/]",
        _ => " ",
    };

    private static string Trim(string s, int max) => s.Length <= max ? s : "…" + s[^(max - 1)..];

    private sealed class ProgressReporter : ISyncReporter
    {
        private readonly ProgressContext _ctx;
        private readonly ProgressTask _overall;
        private readonly Dictionary<PlanItem, ProgressTask> _tasks = new();
        private readonly object _lock = new();

        public ProgressReporter(ProgressContext ctx, int count, long bytes)
        {
            _ctx = ctx;
            _overall = ctx.AddTask($"[bold]Gesamt[/] [grey]({count} Dateien)[/]", maxValue: Math.Max(1, bytes));
        }

        public IProgress<long>? FileStarted(PlanItem item)
        {
            ProgressTask task;
            lock (_lock)
            {
                task = _ctx.AddTask(Markup.Escape(Trim(item.RelPath, 45)), maxValue: Math.Max(1, item.Remote?.Size ?? 1));
                _tasks[item] = task;
            }

            long last = 0;
            return new DirectProgress(bytes =>
            {
                var delta = bytes - last;
                last = bytes;
                task.Value = Math.Min(bytes, task.MaxValue);
                if (delta > 0)
                    _overall.Increment(delta);
            });
        }

        public void FileCompleted(PlanItem item) => Stop(item);

        public void FileFailed(PlanItem item, Exception error) => Stop(item);

        private void Stop(PlanItem item)
        {
            lock (_lock)
            {
                if (_tasks.Remove(item, out var task))
                {
                    task.Value = task.MaxValue;
                    task.StopTask();
                }
            }
        }

        private sealed class DirectProgress(Action<long> report) : IProgress<long>
        {
            public void Report(long value) => report(value);
        }
    }
}
