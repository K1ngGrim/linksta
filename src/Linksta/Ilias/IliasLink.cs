using System.Text.RegularExpressions;

namespace Linksta.Ilias;

/// <summary>
/// Versteht die verschiedenen Link-Formate von ILIAS und macht daraus einen WebDAV-Pfad
/// relativ zu <c>webdav.php/&lt;client&gt;/</c>.
/// </summary>
public static partial class IliasLink
{
    // goto.php?target=crs_12345 | goto.php/crs/12345 | goto_client_crs_12345.html | ilias.php?ref_id=12345 | ref_12345
    [GeneratedRegex(@"(?:target=|goto_[^_/]+_|/goto\.php/)(?<type>crs|grp|fold|cat)[_/](?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex GotoRegex();

    [GeneratedRegex(@"[?&]ref_id=(?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex RefIdQueryRegex();

    [GeneratedRegex(@"^(?:ref_)?(?<id>\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex BareRefRegex();

    [GeneratedRegex(@"/webdav\.php/(?<client>[^/]+)/(?<path>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex WebDavRegex();

    [GeneratedRegex(@"(?:[?&]client_id=|goto_)(?<client>[A-Za-z0-9_\-]+?)(?:&|_(?:crs|grp|fold|cat|file|root)_|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ClientRegex();

    /// <summary>
    /// Wandelt Eingaben wie eine Kurs-URL, eine Ref-ID oder eine WebDAV-URL in einen
    /// WebDAV-Pfad (z. B. <c>ref_12345</c>) um.
    /// </summary>
    public static bool TryGetWebDavPath(string input, out string path)
    {
        path = "";
        input = input.Trim();
        if (input.Length == 0)
            return false;

        var m = BareRefRegex().Match(input);
        if (m.Success)
        {
            path = $"ref_{m.Groups["id"].Value}";
            return true;
        }

        m = WebDavRegex().Match(input);
        if (m.Success)
        {
            path = Uri.UnescapeDataString(m.Groups["path"].Value).Trim('/');
            return path.Length > 0;
        }

        m = GotoRegex().Match(input);
        if (m.Success)
        {
            path = $"ref_{m.Groups["id"].Value}";
            return true;
        }

        m = RefIdQueryRegex().Match(input);
        if (m.Success)
        {
            path = $"ref_{m.Groups["id"].Value}";
            return true;
        }

        return false;
    }

    /// <summary>Versucht, Server-Basis-URL und Client-ID aus einer beliebigen ILIAS-URL zu erraten.</summary>
    public static (string? BaseUrl, string? ClientId) GuessServer(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            return (null, null);

        var full = uri.AbsoluteUri;
        string? client = null;

        var dav = WebDavRegex().Match(full);
        if (dav.Success)
            client = dav.Groups["client"].Value;
        else
        {
            var c = ClientRegex().Match(full);
            if (c.Success)
                client = c.Groups["client"].Value;
        }

        // Basis = alles vor dem ersten bekannten ILIAS-Skript
        var path = uri.AbsolutePath;
        var markers = new[] { "/webdav.php", "/goto.php", "/goto_", "/ilias.php", "/login.php", "/index.php", "/shib_login.php" };
        var cut = markers
            .Select(mk => path.IndexOf(mk, StringComparison.OrdinalIgnoreCase))
            .Where(i => i >= 0)
            .DefaultIfEmpty(-1)
            .Min();
        var basePath = cut >= 0 ? path[..cut] : path.TrimEnd('/');

        return ($"{uri.Scheme}://{uri.Authority}{basePath}".TrimEnd('/'), client);
    }

    /// <summary>
    /// Fragt ILIAS nach einem nicht existierenden Client. ILIAS antwortet dann mit
    /// "Client does not exist … index.php?client_id=ECHTER_CLIENT", daraus lesen wir den Namen.
    /// </summary>
    public static async Task<string?> DetectClientIdAsync(string baseUrl, CancellationToken ct = default, HttpMessageHandler? handler = null)
    {
        try
        {
            using var http = handler is null ? new HttpClient() : new HttpClient(handler);
            http.Timeout = TimeSpan.FromSeconds(15);
            foreach (var probe in new[] { $"{baseUrl.TrimEnd('/')}/webdav.php/__linksta_probe__/", $"{baseUrl.TrimEnd('/')}/login.php" })
            {
                using var res = await http.GetAsync(probe, ct);
                var body = await res.Content.ReadAsStringAsync(ct);
                var m = ClientIdInHtmlRegex().Match(body + " " + res.RequestMessage?.RequestUri);
                if (m.Success && !m.Groups["client"].Value.Equals("__linksta_probe__", StringComparison.Ordinal))
                    return m.Groups["client"].Value;
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // Erkennung ist nur ein Komfort-Feature
        }
        return null;
    }

    [GeneratedRegex(@"client_id=(?<client>[A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ClientIdInHtmlRegex();

    /// <summary>Baut die WebDAV-Root-URL: https://host/ilias/webdav.php/client/</summary>
    public static Uri WebDavRoot(string baseUrl, string clientId) =>
        new($"{baseUrl.TrimEnd('/')}/webdav.php/{Uri.EscapeDataString(clientId)}/");

    /// <summary>Web-URL eines Repository-Objekts, z. B. ref_123 → ilias.php?baseClass=ilrepositorygui&amp;ref_id=123</summary>
    public static Uri RepositoryUrl(string baseUrl, string refPath)
    {
        var m = BareRefRegex().Match(refPath);
        if (!m.Success)
            throw new ArgumentException($"Web-Zugriff braucht eine Ref-ID, nicht \"{refPath}\".");
        return new Uri($"{baseUrl.TrimEnd('/')}/ilias.php?baseClass=ilrepositorygui&ref_id={m.Groups["id"].Value}");
    }

    /// <summary>Hängt einen (unescaped) relativen Pfad an eine Basis-URL an.</summary>
    public static Uri Combine(Uri root, string relativePath)
    {
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString);
        var rel = string.Join('/', segments);
        return rel.Length == 0 ? root : new Uri(root.AbsoluteUri.TrimEnd('/') + "/" + rel + "/");
    }
}
