using System.Diagnostics;
using Linksta.Config;
using Linksta.Security;
using Linksta.WebDav;
using Spectre.Console;

namespace Linksta.Commands;

public sealed class UserException(string message) : Exception(message);

internal static class Shared
{
    public static AppConfig RequireConfig()
    {
        var cfg = ConfigStore.Load();
        if (!cfg.IsInitialized)
            throw new UserException("linksta ist noch nicht eingerichtet. Starte mit [bold]linksta init[/].");
        return cfg;
    }

    public static string GetPassword(AppConfig cfg, bool offerSave = true)
    {
        var pw = CredentialStore.Get(cfg.Username);
        if (!string.IsNullOrEmpty(pw))
            return pw;

        if (!AnsiConsole.Profile.Capabilities.Interactive)
            throw new UserException("Kein Passwort gespeichert. Führe [bold]linksta login[/] aus oder setze LINKSTA_PASSWORD.");

        pw = AnsiConsole.Prompt(new TextPrompt<string>($"ILIAS-Passwort für [blue]{Markup.Escape(cfg.Username)}[/]:").Secret());
        if (offerSave && AnsiConsole.Confirm($"Passwort sicher speichern ({CredentialStore.BackendName})?"))
            CredentialStore.Set(cfg.Username, pw);
        return pw;
    }

    public static WebDavClient CreateClient(AppConfig cfg) => new(cfg.Username, GetPassword(cfg));

    public static List<CourseConfig> SelectCourses(AppConfig cfg, string[]? names, bool includeDisabled = false)
    {
        if (cfg.Courses.Count == 0)
            throw new UserException("Noch keine Kurse hinzugefügt. Nutze [bold]linksta add <Kurs-URL>[/] oder [bold]linksta browse[/].");

        if (names is null || names.Length == 0)
            return cfg.Courses.Where(c => includeDisabled || c.Enabled).ToList();

        var result = new List<CourseConfig>();
        foreach (var n in names)
        {
            var c = cfg.FindCourse(n)
                    ?? cfg.Courses.FirstOrDefault(x => x.Name.Contains(n, StringComparison.OrdinalIgnoreCase))
                    ?? throw new UserException($"Kurs [bold]{Markup.Escape(n)}[/] nicht gefunden. [grey]linksta list[/] zeigt alle Kurse.");
            if (!result.Contains(c))
                result.Add(c);
        }
        return result;
    }

    public static void OpenInFileManager(string path)
    {
        var psi = OperatingSystem.IsWindows() ? new ProcessStartInfo("explorer.exe", $"\"{path}\"")
            : OperatingSystem.IsMacOS() ? new ProcessStartInfo("open", [path])
            : new ProcessStartInfo("xdg-open", [path]);
        psi.UseShellExecute = false;
        Process.Start(psi);
    }

    public static string Ago(DateTimeOffset? t)
    {
        if (t is null) return "[grey]nie[/]";
        var d = DateTimeOffset.UtcNow - t.Value;
        return d.TotalMinutes < 1 ? "gerade eben"
            : d.TotalHours < 1 ? $"vor {(int)d.TotalMinutes} min"
            : d.TotalDays < 1 ? $"vor {(int)d.TotalHours} h"
            : d.TotalDays < 30 ? $"vor {(int)d.TotalDays} Tag(en)"
            : t.Value.LocalDateTime.ToString("dd.MM.yyyy");
    }
}
