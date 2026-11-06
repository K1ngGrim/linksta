using System.Text;
using Linksta.Commands;
using Spectre.Console;
using Spectre.Console.Cli;

Console.OutputEncoding = Encoding.UTF8;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    if (cts.IsCancellationRequested)
        return; // zweites Strg+C → hart beenden
    e.Cancel = true;
    cts.Cancel();
    AnsiConsole.MarkupLine("\n[yellow]Abbruch … bisheriger Stand wird gespeichert.[/]");
};

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("linksta");
    config.SetApplicationVersion(typeof(PullCommand).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
    config.CancellationExitCode(130);

    config.AddCommand<InitCommand>("init")
        .WithDescription("Einrichten: ILIAS-URL, Benutzer, Sync-Ordner.");
    config.AddCommand<LoginCommand>("login")
        .WithDescription("Passwort (neu) speichern.");
    config.AddCommand<LogoutCommand>("logout")
        .WithDescription("Gespeichertes Passwort löschen.");

    config.AddCommand<AddCommand>("add")
        .WithDescription("Kurs per Link oder Ref-ID abonnieren.")
        .WithExample("add", "https://ilias.uni.de/goto.php?target=crs_123456")
        .WithExample("add", "123456", "--name", "Analysis");
    config.AddCommand<CoursesCommand>("courses")
        .WithAlias("mine")
        .WithDescription("Deine Kurse aus „Meine Kurse und Gruppen“ auswählen und abonnieren.");
    config.AddCommand<BrowseCommand>("browse")
        .WithDescription("ILIAS-Magazin interaktiv durchsuchen und Kurse abonnieren.");
    config.AddCommand<ListCommand>("list")
        .WithAlias("ls")
        .WithDescription("Abonnierte Kurse anzeigen.");
    config.AddCommand<RemoveCommand>("remove")
        .WithAlias("rm")
        .WithDescription("Kurs-Abo entfernen.");
    config.AddCommand<PauseCommand>("pause")
        .WithDescription("Kurs beim Pull überspringen.");
    config.AddCommand<ResumeCommand>("resume")
        .WithDescription("Pausierten Kurs wieder synchronisieren.");

    config.AddCommand<PullCommand>("pull")
        .WithAlias("sync")
        .WithDescription("Neue und geänderte Dateien herunterladen.")
        .WithExample("pull")
        .WithExample("pull", "Analysis", "--prune")
        .WithExample("pull", "--dry-run");
    config.AddCommand<StatusCommand>("status")
        .WithAlias("st")
        .WithDescription("Zeigen, was sich auf ILIAS geändert hat (ohne Download).");
    config.AddCommand<RecentCommand>("recent")
        .WithAlias("new")
        .WithDescription("Zuletzt heruntergeladene Dateien anzeigen.");
    config.AddCommand<SearchCommand>("search")
        .WithAlias("find")
        .WithDescription("Offline in den synchronisierten Dateien suchen.")
        .WithExample("search", "Übungsblatt")
        .WithExample("search", "*.pdf", "-c", "Analysis", "--open");
    config.AddCommand<OpenCommand>("open")
        .WithDescription("Sync-Ordner oder Kursordner im Dateimanager öffnen.");

    config.AddCommand<LsRemoteCommand>("ls-remote")
        .WithDescription("Diagnose: rohes WebDAV-Listing anzeigen.");

    config.AddBranch("config", cfg =>
    {
        cfg.SetDescription("Einstellungen anzeigen und ändern.");
        cfg.AddCommand<ConfigShowCommand>("show").WithDescription("Einstellungen anzeigen.");
        cfg.AddCommand<ConfigSetCommand>("set").WithDescription("Einstellung ändern.")
            .WithExample("config", "set", "max-size", "200")
            .WithExample("config", "set", "parallel", "8");
    });
    config.AddBranch("exclude", ex =>
    {
        ex.SetDescription("Dateien/Ordner vom Download ausschließen.");
        ex.AddCommand<ExcludeAddCommand>("add").WithDescription("Ausschlussmuster hinzufügen.")
            .WithExample("exclude", "add", "*.mp4")
            .WithExample("exclude", "add", "Aufzeichnungen", "-c", "Analysis");
        ex.AddCommand<ExcludeRemoveCommand>("remove").WithAlias("rm").WithDescription("Ausschlussmuster entfernen.");
    });

    config.SetExceptionHandler((ex, _) =>
    {
        switch (ex)
        {
            case UserException u:
                AnsiConsole.MarkupLine($"[red]✗[/] {u.Message}");
                return 1;
            case OperationCanceledException:
                AnsiConsole.MarkupLine("[yellow]Abgebrochen.[/]");
                return 130;
            case CommandParseException or CommandRuntimeException:
                AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(ex.Message)}");
                return 2;
            case Linksta.WebDav.WebDavException or HttpRequestException or Linksta.Ilias.IliasWebException:
                AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(ex.Message)}");
                return 1;
            default:
                AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
                return 99;
        }
    });
});

return await app.RunAsync(args, cts.Token);
