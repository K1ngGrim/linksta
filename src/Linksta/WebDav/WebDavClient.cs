using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace Linksta.WebDav;

/// <summary>Ein Eintrag aus einem PROPFIND-Listing.</summary>
public sealed record DavItem(
    Uri Href,
    string Name,
    bool IsCollection,
    long? Size,
    DateTimeOffset? LastModified,
    string? ETag,
    string? ContentType);

public sealed class WebDavException : Exception
{
    public HttpStatusCode? StatusCode { get; }

    /// <summary>ILIAS blendet das Objekt wegen seines Titels (z. B. "/") über WebDAV aus → Web-Fallback möglich.</summary>
    public bool HiddenByTitle { get; init; }

    public WebDavException(string message, HttpStatusCode? statusCode = null, Exception? inner = null)
        : base(message, inner) => StatusCode = statusCode;
}

/// <summary>
/// Minimaler WebDAV-Client (PROPFIND + GET) – mehr braucht ein Pull-Sync nicht.
/// </summary>
public sealed class WebDavClient : Linksta.Sync.IFileSource, IDisposable
{
    private static readonly XNamespace Dav = "DAV:";

    private const string PropfindBody =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <D:propfind xmlns:D="DAV:">
          <D:prop>
            <D:displayname/>
            <D:resourcetype/>
            <D:getcontentlength/>
            <D:getlastmodified/>
            <D:getetag/>
            <D:getcontenttype/>
          </D:prop>
        </D:propfind>
        """;

    private readonly HttpClient _http;
    private readonly int _maxRetries;

    public WebDavClient(string username, string password, HttpMessageHandler? handler = null, int maxRetries = 3)
    {
        _maxRetries = maxRetries;
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = 16,
            })
            : new HttpClient(handler);

        _http.Timeout = Timeout.InfiniteTimeSpan; // große Dateien – Abbruch läuft über CancellationToken
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"linksta/{typeof(WebDavClient).Assembly.GetName().Version?.ToString(3) ?? "1.0"}");
    }

    /// <summary>Listet den direkten Inhalt einer Collection (Depth: 1), ohne die Collection selbst.</summary>
    public async Task<IReadOnlyList<DavItem>> ListAsync(Uri collection, CancellationToken ct = default)
    {
        collection = EnsureTrailingSlash(collection);

        var xml = await WithRetry(async () =>
        {
            using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), collection)
            {
                Content = new StringContent(PropfindBody, Encoding.UTF8, "application/xml"),
            };
            req.Headers.Add("Depth", "1");

            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            await EnsureSuccess(res, collection, ct);
            return await res.Content.ReadAsStringAsync(ct);
        }, ct);

        return ParseMultiStatus(xml, collection);
    }

    /// <summary>Liest die Eigenschaften einer Ressource selbst (Depth: 0), z. B. den Kurstitel.</summary>
    public async Task<DavItem?> GetInfoAsync(Uri resource, CancellationToken ct = default)
    {
        var xml = await WithRetry(async () =>
        {
            using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), resource)
            {
                Content = new StringContent(PropfindBody, Encoding.UTF8, "application/xml"),
            };
            req.Headers.Add("Depth", "0");

            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            await EnsureSuccess(res, resource, ct);
            return await res.Content.ReadAsStringAsync(ct);
        }, ct);

        // Mit einer Dummy-Basis parsen, damit der Eintrag nicht als "self" verworfen wird
        return ParseMultiStatus(xml, new Uri(resource, "/__linksta_none__/")).FirstOrDefault();
    }

    /// <summary>Lädt eine Datei herunter und meldet den Fortschritt in Bytes.</summary>
    public async Task DownloadAsync(Uri file, Stream destination, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        await WithRetry(async () =>
        {
            destination.SetLength(0);
            destination.Position = 0;

            using var res = await _http.GetAsync(file, HttpCompletionOption.ResponseHeadersRead, ct);
            await EnsureSuccess(res, file, ct);

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
            return true;
        }, ct);
    }

    internal static IReadOnlyList<DavItem> ParseMultiStatus(string xml, Uri requestUri)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (Exception e)
        {
            throw new WebDavException("Der Server hat keine gültige WebDAV-Antwort geliefert (ist die URL korrekt?).", null, e);
        }

        var self = NormalizePath(requestUri);
        var items = new List<DavItem>();

        foreach (var response in doc.Descendants(Dav + "response"))
        {
            var hrefText = response.Element(Dav + "href")?.Value?.Trim();
            if (string.IsNullOrEmpty(hrefText))
                continue;

            var href = new Uri(requestUri, hrefText);
            if (NormalizePath(href) == self)
                continue; // die Collection selbst

            // Nur Properties mit Status 200 berücksichtigen
            var props = response.Elements(Dav + "propstat")
                .Where(ps => (ps.Element(Dav + "status")?.Value ?? "200").Contains(" 200", StringComparison.Ordinal)
                             || ps.Element(Dav + "status") is null)
                .Select(ps => ps.Element(Dav + "prop"))
                .Where(p => p is not null)
                .SelectMany(p => p!.Elements())
                .ToList();

            XElement? Prop(string name) => props.FirstOrDefault(e => e.Name == Dav + name);

            var isCollection = Prop("resourcetype")?.Element(Dav + "collection") is not null;

            long? size = long.TryParse(Prop("getcontentlength")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : null;

            var lastModified = ParseHttpDate(Prop("getlastmodified")?.Value);

            var etag = Prop("getetag")?.Value?.Trim();
            var displayName = Prop("displayname")?.Value?.Trim();
            var segment = Uri.UnescapeDataString(href.AbsolutePath.TrimEnd('/').Split('/').Last());
            var name = string.IsNullOrWhiteSpace(displayName) ? segment : displayName;

            if (isCollection)
                href = EnsureTrailingSlash(href);

            items.Add(new DavItem(href, name, isCollection, size, lastModified,
                string.IsNullOrEmpty(etag) ? null : etag,
                Prop("getcontenttype")?.Value));
        }

        return items;
    }

    /// <summary>RFC 1123 ("Thu, 01 Oct 2026 12:00:00 GMT"), tolerant gegenüber falschem Wochentag.</summary>
    internal static DateTimeOffset? ParseHttpDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var v = value.Trim();
        var comma = v.IndexOf(',');
        if (comma is > 0 and <= 10)
            v = v[(comma + 1)..].Trim();

        string[] formats = ["dd MMM yyyy HH:mm:ss 'GMT'", "d MMM yyyy HH:mm:ss 'GMT'", "dd-MMM-yy HH:mm:ss 'GMT'"];
        if (DateTimeOffset.TryParseExact(v, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var exact))
            return exact.ToUniversalTime();

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var any)
            ? any.ToUniversalTime()
            : null;
    }

    private static string NormalizePath(Uri uri) =>
        Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/');

    public static Uri EnsureTrailingSlash(Uri uri) =>
        uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");

    private static async Task EnsureSuccess(HttpResponseMessage res, Uri uri, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode || (int)res.StatusCode == 207)
            return;

        // ILIAS (sabre/dav) liefert Details als XML: <s:exception>…</s:exception><s:message>…</s:message>
        string body = "";
        try { body = await res.Content.ReadAsStringAsync(ct); } catch { /* egal */ }

        if (body.Contains("title is invalid or hidden", StringComparison.OrdinalIgnoreCase))
            throw new WebDavException(
                "ILIAS blendet dieses Objekt über WebDAV aus, weil sein Titel ein unzulässiges Zeichen enthält " +
                "(meist \"/\", z. B. \"WS 2026/27\"). Das lässt sich nur in ILIAS selbst ändern (Titel ohne \"/\").",
                HttpStatusCode.UnprocessableEntity) { HiddenByTitle = true };

        if (body.Contains("NotDavable", StringComparison.OrdinalIgnoreCase))
            throw new WebDavException(
                "Dieses ILIAS-Objekt ist nicht per WebDAV verfügbar (z. B. Magazin-Wurzel, Kategorie oder Lernmodul). " +
                "Nimm direkt den Link eines Kurses, einer Gruppe oder eines Ordners.",
                HttpStatusCode.UnprocessableEntity);

        var msg = res.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Anmeldung fehlgeschlagen (401). Benutzername/Passwort prüfen – bei Shibboleth-Login ggf. ein lokales ILIAS-/WebDAV-Passwort setzen.",
            HttpStatusCode.Forbidden => $"Kein Zugriff (403) auf {uri} – bist du in diesem Kurs Mitglied?",
            HttpStatusCode.NotFound => $"Nicht gefunden (404): {uri}",
            HttpStatusCode.MethodNotAllowed => "Der Server erlaubt kein PROPFIND (405) – ist WebDAV in diesem ILIAS aktiviert?",
            _ => $"Server antwortete mit {(int)res.StatusCode} {res.ReasonPhrase} für {uri}",
        };

        var detail = System.Text.RegularExpressions.Regex.Match(body, "<s:message>(?<m>[^<]+)</s:message>");
        if (detail.Success && res.StatusCode != HttpStatusCode.Unauthorized)
            msg += $" ({detail.Groups["m"].Value.Trim()})";

        throw new WebDavException(msg, res.StatusCode);
    }

    private async Task<T> WithRetry<T>(Func<Task<T>> action, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception e) when (attempt < _maxRetries && IsTransient(e) && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1)), ct);
            }
        }
    }

    private static bool IsTransient(Exception e) => e switch
    {
        HttpRequestException => true,
        IOException => true,
        TaskCanceledException => false,
        WebDavException w => w.StatusCode is null
                             || (int)w.StatusCode >= 500
                             || w.StatusCode == HttpStatusCode.RequestTimeout
                             || w.StatusCode == HttpStatusCode.TooManyRequests,
        _ => false,
    };

    public void Dispose() => _http.Dispose();
}
