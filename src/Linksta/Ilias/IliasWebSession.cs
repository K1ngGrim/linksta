using System.Net;
using System.Text.RegularExpressions;

namespace Linksta.Ilias;

public sealed record MembershipItem(string RefPath, string Title, string Type);

/// <summary>
/// Meldet sich wie ein Browser über das ILIAS-Loginformular an und liest
/// „Meine Kurse und Gruppen“ aus dem Dashboard. WebDAV selbst kennt keine Mitgliedschaften.
/// </summary>
public sealed partial class IliasWebSession : Linksta.Sync.IFileSource, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _base;
    private readonly string _client;

    public IliasWebSession(string baseUrl, string clientId, HttpMessageHandler? handler = null)
    {
        _base = baseUrl.TrimEnd('/');
        _client = clientId;
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (linksta)");
    }

    public async Task LoginAsync(string user, string password, CancellationToken ct = default)
    {
        var loginUrl = $"{_base}/login.php?client_id={Uri.EscapeDataString(_client)}&lang=de";
        using var page = await _http.GetAsync(loginUrl, ct);
        var html = await page.Content.ReadAsStringAsync(ct);
        var pageUri = page.RequestMessage?.RequestUri ?? new Uri(loginUrl);

        var form = FindLoginForm(html)
                   ?? throw new IliasWebException("Kein Login-Formular gefunden. Meldet sich deine Hochschule nur über Shibboleth/SSO an? Dann nutze bitte linksta browse oder linksta add.");

        var content = new MultipartFormDataContent
        {
            { new StringContent(user), form.UserField },
            { new StringContent(password), form.PasswordField },
        };
        foreach (var (name, value) in form.Hidden)
            content.Add(new StringContent(value), name);
        if (form.SubmitName is not null)
            content.Add(new StringContent(form.SubmitValue ?? ""), form.SubmitName);

        using var res = await _http.PostAsync(new Uri(pageUri, WebUtility.HtmlDecode(form.Action)), content, ct);
        var after = await res.Content.ReadAsStringAsync(ct);

        if (FindLoginForm(after) is not null || !LooksLoggedIn(after))
            throw new IliasWebException("Web-Anmeldung bei ILIAS fehlgeschlagen (Benutzername/Passwort?).");
        IsLoggedIn = true;
    }

    public bool IsLoggedIn { get; private set; }

    /// <summary>Lädt eine ILIAS-Seite (mit Wiederholung bei Netzwerkfehlern).</summary>
    public async Task<string> GetPageAsync(Uri url, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var res = await _http.GetAsync(url, ct);
                if (!res.IsSuccessStatusCode)
                    throw new IliasWebException($"ILIAS antwortete mit {(int)res.StatusCode} für {url}");
                var html = await res.Content.ReadAsStringAsync(ct);
                if (FindLoginForm(html) is not null)
                    throw new IliasWebException("Die ILIAS-Sitzung ist abgelaufen oder die Anmeldung wurde abgelehnt.");
                DebugDump(url.ToString(), html);
                return html;
            }
            catch (HttpRequestException) when (attempt < 3)
            {
                await Task.Delay(500 * attempt, ct);
            }
        }
    }

    /// <summary>Titel eines Kurses/einer Gruppe aus dem Seitentitel.</summary>
    public async Task<string?> GetTitleAsync(Uri url, CancellationToken ct = default)
    {
        var html = await GetPageAsync(url, ct);
        return ParseTitle(html);
    }

    internal static string? ParseTitle(string html)
    {
        var h1 = Regex.Match(html, @"<h1 class=""il-page-content-header[^>]*>(?<t>.*?)</h1>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (h1.Success)
        {
            var text = Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(h1.Groups["t"].Value, "<[^>]+>", " ")), @"\s+", " ").Trim();
            if (text.Length > 0)
                return text;
        }

        // Fallback: "Inhalt: Kurstitel: ILIAS - Hochschule …"
        var m = Regex.Match(html, @"<title>(?<t>.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!m.Success)
            return null;
        var t = WebUtility.HtmlDecode(m.Groups["t"].Value).Trim();
        var cut = t.LastIndexOf(": ILIAS", StringComparison.Ordinal);
        if (cut > 0) t = t[..cut];
        var colon = t.IndexOf(": ", StringComparison.Ordinal);
        if (colon is > 0 and < 20) t = t[(colon + 2)..];
        return Regex.Replace(t, @"\s+", " ").Trim();
    }

    public async Task DownloadAsync(Uri file, Stream destination, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                destination.SetLength(0);
                destination.Position = 0;

                using var res = await _http.GetAsync(file, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!res.IsSuccessStatusCode)
                    throw new IliasWebException($"Download fehlgeschlagen ({(int)res.StatusCode}).");
                if (res.Content.Headers.ContentType?.MediaType == "text/html" && res.Content.Headers.ContentDisposition is null)
                    throw new IliasWebException("ILIAS hat statt der Datei eine Webseite geliefert (keine Berechtigung oder Sitzung abgelaufen).");

                await using var src = await res.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                    total += read;
                    progress?.Report(total);
                }
                return;
            }
            catch (Exception e) when (e is HttpRequestException or IOException && attempt < 3 && !ct.IsCancellationRequested)
            {
                await Task.Delay(500 * attempt, ct);
            }
        }
    }

    public async Task<IReadOnlyList<MembershipItem>> GetMembershipsAsync(CancellationToken ct = default)
    {
        var urls = new[]
        {
            $"{_base}/ilias.php?baseClass=ildashboardgui&cmd=jumpToMemberships",
            $"{_base}/ilias.php?baseClass=ilmembershipoverviewgui",
            $"{_base}/ilias.php?baseClass=ildashboardgui&cmd=show",
        };

        var result = new Dictionary<string, MembershipItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in urls)
        {
            string html;
            try
            {
                html = await _http.GetStringAsync(url, ct);
            }
            catch (HttpRequestException)
            {
                continue;
            }

            DebugDump(url, html);
            foreach (var item in ParseMemberships(html))
                result.TryAdd(item.RefPath, item);

            if (result.Count > 0)
                break;
        }

        return result.Values.OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ---------- Parsing ----------

    internal sealed record LoginForm(string Action, string UserField, string PasswordField, List<(string, string)> Hidden, string? SubmitName, string? SubmitValue);

    [GeneratedRegex(@"<form\b(?<attrs>[^>]*)>(?<body>.*?)</form>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex FormRegex();

    [GeneratedRegex(@"<(?:input|button)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex InputRegex();

    [GeneratedRegex(@"(?<name>[\w\-:]+)\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)')", RegexOptions.IgnoreCase)]
    private static partial Regex AttrRegex();

    private static Dictionary<string, string> Attrs(string tag) =>
        AttrRegex().Matches(tag)
            .GroupBy(m => m.Groups["name"].Value.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First().Groups["v"].Value);

    internal static LoginForm? FindLoginForm(string html)
    {
        foreach (Match f in FormRegex().Matches(html))
        {
            var inputs = InputRegex().Matches(f.Groups["body"].Value).Select(m => Attrs(m.Value)).ToList();
            var pw = inputs.FirstOrDefault(i => i.GetValueOrDefault("type")?.Equals("password", StringComparison.OrdinalIgnoreCase) == true);
            if (pw is null || !pw.ContainsKey("name"))
                continue;

            var userInput = inputs.FirstOrDefault(i =>
                i.ContainsKey("name") && (i.GetValueOrDefault("type") ?? "text").ToLowerInvariant() is "text" or "email");
            if (userInput is null)
                continue;

            var hidden = inputs
                .Where(i => i.GetValueOrDefault("type")?.Equals("hidden", StringComparison.OrdinalIgnoreCase) == true && i.ContainsKey("name"))
                .Select(i => (i["name"], WebUtility.HtmlDecode(i.GetValueOrDefault("value") ?? "")))
                .ToList();

            var submit = inputs.FirstOrDefault(i => i.GetValueOrDefault("type")?.Equals("submit", StringComparison.OrdinalIgnoreCase) == true && i.ContainsKey("name"));
            var formAttrs = Attrs(f.Groups["attrs"].Value);

            return new LoginForm(
                formAttrs.GetValueOrDefault("action") ?? "",
                WebUtility.HtmlDecode(userInput["name"]),
                WebUtility.HtmlDecode(pw["name"]),
                hidden,
                submit?.GetValueOrDefault("name"),
                submit?.GetValueOrDefault("value"));
        }
        return null;
    }

    private static bool LooksLoggedIn(string html) =>
        html.Contains("logout.php", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("cmd=logout", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("ildashboardgui", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"<a\b(?<attrs>[^>]*)>(?<text>.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex AnchorRegex();

    [GeneratedRegex(@"(?:target=|goto_[^_/]+_|/goto\.php/)(?<type>crs|grp)[_/](?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex CourseLinkRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    /// <summary>Alle Links auf Kurse/Gruppen mit Titel. Navigationsleiste („Zuletzt besucht“) wird ignoriert.</summary>
    internal static IEnumerable<MembershipItem> ParseMemberships(string html)
    {
        // Mainbar/Slates (Zuletzt besucht, Favoriten …) ausblenden
        var main = html;
        var start = html.IndexOf("il_center_col", StringComparison.OrdinalIgnoreCase);
        if (start < 0) start = html.IndexOf("<main", StringComparison.OrdinalIgnoreCase);
        if (start > 0) main = html[start..];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match a in AnchorRegex().Matches(main))
        {
            var attrs = Attrs(a.Groups["attrs"].Value);
            var href = WebUtility.HtmlDecode(attrs.GetValueOrDefault("href") ?? "");
            var m = CourseLinkRegex().Match(href);
            if (!m.Success)
                continue;

            var title = WebUtility.HtmlDecode(TagRegex().Replace(a.Groups["text"].Value, " "));
            title = Regex.Replace(title, @"\s+", " ").Trim();
            if (title.Length == 0)
                continue;

            var refPath = $"ref_{m.Groups["id"].Value}";
            if (seen.Add(refPath))
                yield return new MembershipItem(refPath, title, m.Groups["type"].Value.ToLowerInvariant());
        }
    }

    private static void DebugDump(string url, string html)
    {
        var dir = Environment.GetEnvironmentVariable("LINKSTA_DEBUG_DUMP");
        if (string.IsNullOrEmpty(dir))
            return;
        Directory.CreateDirectory(dir);
        var name = Regex.Replace(url, @"[^A-Za-z0-9]+", "_");
        File.WriteAllText(Path.Combine(dir, name[^Math.Min(80, name.Length)..] + ".html"), html);
    }

    public void Dispose() => _http.Dispose();
}

public sealed class IliasWebException(string message) : Exception(message);
