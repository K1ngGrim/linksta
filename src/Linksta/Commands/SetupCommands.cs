using System.ComponentModel;
using Linksta.Config;
using Linksta.Ilias;
using Linksta.Security;
using Linksta.WebDav;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Linksta.Commands;

public sealed class InitCommand : AsyncCommand<InitCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-u|--url <URL>")]
        [Description("Irgendeine URL deines ILIAS (z. B. die Startseite oder ein Kurs-Link).")]
        public string? Url { get; init; }

        [CommandOption("-c|--client <CLIENT>")]
        [Description("ILIAS-Client-ID (steht oft in Links: goto_[italic]CLIENT[/]_crs_123.html).")]
        public string? Client { get; init; }

        [CommandOption("--user <NAME>")]
        [Description("ILIAS-Benutzername.")]
        public string? User { get; init; }

        [CommandOption("-d|--dir <PFAD>")]
        [Description("Lokaler Ordner für die Kurse.")]
        public string? Dir { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var cfg = ConfigStore.Load();

        AnsiConsole.Write(new FigletText("linksta").Color(Color.DodgerBlue1));
        AnsiConsole.MarkupLine("[grey]ILIAS-Kurse per WebDAV offline verfügbar machen.[/]\n");

        var interactive = AnsiConsole.Profile.Capabilities.Interactive;
        string Need(string what) => throw new UserException($"{what} fehlt (Option angeben oder interaktiv ausführen).");

        var url = s.Url ?? (!interactive ? Need("--url") : AnsiConsole.Prompt(
            new TextPrompt<string>("URL deines ILIAS [grey](z. B. https://ilias.uni-beispiel.de)[/]:")
                .DefaultValue(cfg.BaseUrl)
                .ShowDefaultValue(!string.IsNullOrEmpty(cfg.BaseUrl))
                .Validate(v => Uri.TryCreate(v, UriKind.Absolute, out _) ? ValidationResult.Success() : ValidationResult.Error("Bitte eine vollständige URL mit https:// eingeben."))));

        var (baseUrl, guessedClient) = IliasLink.GuessServer(url);
        if (baseUrl is null)
            throw new UserException("Ungültige URL.");

        if (guessedClient is null && s.Client is null)
            guessedClient = await AnsiConsole.Status().StartAsync("Ermittle Client-ID …", async _ => await IliasLink.DetectClientIdAsync(baseUrl, ct));

        var defaultClient = guessedClient ?? cfg.ClientId;
        var client = s.Client ?? (!interactive
            ? (string.IsNullOrEmpty(defaultClient) ? Need("--client") : defaultClient)
            : AnsiConsole.Prompt(
                new TextPrompt<string>("ILIAS-Client-ID [grey](steht in Links wie goto_[bold]CLIENT[/]_crs_123.html oder ?client_id=…)[/]:")
                    .DefaultValue(defaultClient)
                    .ShowDefaultValue(!string.IsNullOrEmpty(defaultClient))));

        var user = s.User ?? (!interactive
            ? (string.IsNullOrEmpty(cfg.Username) ? Need("--user") : cfg.Username)
            : AnsiConsole.Prompt(
                new TextPrompt<string>("Benutzername:")
                    .DefaultValue(cfg.Username)
                    .ShowDefaultValue(!string.IsNullOrEmpty(cfg.Username))));

        var password = CredentialStore.EnvPassword
                       ?? (!interactive ? Need("Passwort (LINKSTA_PASSWORD)") : AnsiConsole.Prompt(new TextPrompt<string>("Passwort:").Secret()));

        var defaultDir = string.IsNullOrEmpty(cfg.SyncRoot) ? ConfigStore.DefaultSyncRoot() : cfg.SyncRoot;
        var dir = s.Dir ?? (!interactive ? defaultDir : AnsiConsole.Prompt(
            new TextPrompt<string>("Lokaler Sync-Ordner:").DefaultValue(defaultDir)));
        dir = Path.GetFullPath(ExpandHome(dir));

        cfg.BaseUrl = baseUrl;
        cfg.ClientId = client.Trim();
        cfg.Username = user.Trim();
        cfg.SyncRoot = dir;

        // Verbindung testen – bei falschem Passwort bis zu 3 Versuche
        (bool Ok, string? Error, bool Unauthorized) result = default;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            result = await AnsiConsole.Status().StartAsync("Teste Verbindung …", async _ => await TestConnection(cfg, password, ct));
            if (result.Ok)
                break;

            AnsiConsole.MarkupLine($"[red]✗ Verbindung fehlgeschlagen:[/] {Markup.Escape(result.Error ?? "")}");
            if (!result.Unauthorized || !interactive || CredentialStore.EnvPassword is not null || attempt == 3)
                break;

            AnsiConsole.MarkupLine("[grey]Tipp: Es ist dasselbe Passwort wie beim ILIAS-Login im Browser. Die Sternchen zeigen, wie viele Zeichen angekommen sind.[/]");
            password = AnsiConsole.Prompt(new TextPrompt<string>($"Passwort für [blue]{Markup.Escape(cfg.Username)}[/] erneut eingeben:").Secret());
        }

        if (!result.Ok)
        {
            AnsiConsole.MarkupLine($"[grey]Getestete URL: {Markup.Escape(cfg.WebDavRoot.ToString())}[/]");
            if (result.Unauthorized || !interactive || !AnsiConsole.Confirm("Trotzdem speichern?", false))
                return 1;
        }
        else
        {
            AnsiConsole.MarkupLine("[green]✓ Verbindung erfolgreich.[/]");
        }

        Directory.CreateDirectory(cfg.SyncRoot);
        ConfigStore.Save(cfg);

        if (CredentialStore.EnvPassword is null && AnsiConsole.Profile.Capabilities.Interactive && AnsiConsole.Confirm($"Passwort speichern ({CredentialStore.BackendName})?"))
        {
            CredentialStore.Set(cfg.Username, password);
            AnsiConsole.MarkupLine("[green]✓ Passwort gespeichert.[/]");
        }

        AnsiConsole.MarkupLine($"\nKonfiguration: [grey]{Markup.Escape(ConfigStore.ConfigPath)}[/]");
        AnsiConsole.MarkupLine("""

            [bold]Nächste Schritte:[/]
              [blue]linksta add[/] [grey]<Kurs-Link>[/]   Kurs per Link oder Ref-ID hinzufügen
              [blue]linksta browse[/]             ILIAS-Magazin durchsuchen und Kurse auswählen
              [blue]linksta pull[/]               Alles herunterladen / aktualisieren
            """);
        return 0;
    }

    private static async Task<(bool Ok, string? Error, bool Unauthorized)> TestConnection(AppConfig cfg, string password, CancellationToken ct)
    {
        using var dav = new WebDavClient(cfg.Username, password, maxRetries: 1);
        foreach (var probe in new[] { cfg.WebDavRoot, IliasLink.Combine(cfg.WebDavRoot, "ref_1") })
        {
            try
            {
                await dav.ListAsync(probe, ct);
                return (true, null, false);
            }
            catch (WebDavException e) when (e.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return (false, e.Message, true);
            }
            catch (Exception e) when (e is WebDavException or HttpRequestException)
            {
                if (probe != cfg.WebDavRoot)
                    return (false, e.Message, false);
            }
        }
        return (false, "Unbekannter Fehler", false);
    }

    internal static string ExpandHome(string path) =>
        path.StartsWith('~')
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..].TrimStart('/', '\\'))
            : path;
}

public sealed class LoginCommand : AsyncCommand<EmptyCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EmptyCommandSettings settings, CancellationToken ct)
    {
        var cfg = Shared.RequireConfig();
        var pw = AnsiConsole.Prompt(new TextPrompt<string>($"Passwort für [blue]{Markup.Escape(cfg.Username)}[/]:").Secret());

        using var dav = new WebDavClient(cfg.Username, pw);
        try
        {
            await AnsiConsole.Status().StartAsync("Prüfe Zugangsdaten …", _ =>
                cfg.Courses.Count > 0 ? dav.GetInfoAsync(cfg.CourseUri(cfg.Courses[0]), ct) : dav.ListAsync(cfg.WebDavRoot, ct));
        }
        catch (WebDavException e) when (e.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new UserException(e.Message);
        }

        CredentialStore.Set(cfg.Username, pw);
        AnsiConsole.MarkupLine($"[green]✓ Passwort gespeichert ({Markup.Escape(CredentialStore.BackendName)}).[/]");
        return 0;
    }
}

public sealed class LogoutCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings, CancellationToken ct)
    {
        var cfg = Shared.RequireConfig();
        CredentialStore.Delete(cfg.Username);
        AnsiConsole.MarkupLine("[green]✓ Gespeichertes Passwort entfernt.[/]");
        return 0;
    }
}
