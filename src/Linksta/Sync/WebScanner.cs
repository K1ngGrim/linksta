using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Linksta.Ilias;
using Linksta.Util;

namespace Linksta.Sync;

public enum WebItemKind { File, Folder }

public sealed record WebItem(WebItemKind Kind, string RefId, string Title, Uri Href, string? Extension, long? ApproxSize, string? Version, DateTimeOffset? Date, string Fingerprint);

/// <summary>
/// Liest einen Kurs über die ILIAS-Weboberfläche (für Kurse, die WebDAV ausblendet).
/// Berücksichtigt Dateien, Ordner und Gruppen; Tests, Foren, Links usw. werden übersprungen.
/// </summary>
public sealed partial class WebScanner(IliasWebSession web, int parallelism = 4) : ICourseScanner
{
    public async Task<ScanResult> ScanAsync(Uri root, Action<string>? onFolder = null, CancellationToken ct = default)
    {
        var files = new ConcurrentBag<RemoteFile>();
        var errors = new ConcurrentBag<ScanError>();
        var visited = new ConcurrentDictionary<string, bool>();
        var folders = 0;
        using var gate = new SemaphoreSlim(Math.Max(1, parallelism));

        async Task Walk(Uri page, string refId, string relPath, int depth)
        {
            if (depth > 30 || !visited.TryAdd(refId, true))
                return;

            string html;
            await gate.WaitAsync(ct);
            try
            {
                onFolder?.Invoke(relPath.Length == 0 ? "/" : relPath);
                html = await web.GetPageAsync(page, ct);
                Interlocked.Increment(ref folders);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                errors.Add(new ScanError(relPath, e.Message));
                return;
            }
            finally
            {
                gate.Release();
            }

            var items = ParseItems(html, page);
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var children = new List<Task>();

            foreach (var item in items.OrderBy(i => i.Kind).ThenBy(i => i.Title, StringComparer.Ordinal).ThenBy(i => i.RefId, StringComparer.Ordinal))
            {
                var isFile = item.Kind == WebItemKind.File;
                var name = isFile ? FileName(item) : item.Title;
                var safe = PathUtil.MakeUnique(PathUtil.SanitizeSegment(name), taken, isFile);
                var childRel = relPath.Length == 0 ? safe : $"{relPath}/{safe}";

                if (isFile)
                    files.Add(new RemoteFile(childRel, item.Href, item.ApproxSize, item.Date, item.Fingerprint, ExactSize: false));
                else
                    children.Add(Walk(item.Href, item.RefId, childRel, depth + 1));
            }

            await Task.WhenAll(children);
        }

        var rootRef = RefIdRegex().Match(root.Query) is { Success: true } m ? m.Groups["id"].Value : root.AbsoluteUri;
        await Walk(root, rootRef, "", 0);

        var result = new ScanResult { FolderCount = folders };
        result.Files.AddRange(files.OrderBy(f => f.RelPath, StringComparer.Ordinal));
        result.Errors.AddRange(errors.OrderBy(e => e.RelPath, StringComparer.Ordinal));
        return result;
    }

    internal static string FileName(WebItem item)
    {
        var title = item.Title;
        if (string.IsNullOrEmpty(item.Extension))
            return title;
        return title.EndsWith("." + item.Extension, StringComparison.OrdinalIgnoreCase) ? title : $"{title}.{item.Extension}";
    }

    // ---------- Parsing der Container-Seite ----------

    [GeneratedRegex(@"<div\s+id=""item_row_", RegexOptions.IgnoreCase)]
    private static partial Regex RowSplitRegex();

    [GeneratedRegex(@"<a\s+href=""(?<href>[^""]*)""\s+class=""il_ContainerItemTitle""[^>]*>(?<title>.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TitleLinkRegex();

    [GeneratedRegex(@"class=""il_ItemProperty"">(?<p>.*?)</span>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex PropertyRegex();

    [GeneratedRegex(@"cmd=sendfile.*?ref_id=(?<id>\d+)|ref_id=(?<id>\d+).*?cmd=sendfile|goto\.php/file/(?<id>\d+)|target=file_(?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex FileLinkRegex();

    [GeneratedRegex(@"goto\.php/(?:fold|grp)/(?<id>\d+)|target=(?:fold|grp)_(?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ContainerLinkRegex();

    [GeneratedRegex(@"[?&]ref_id=(?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex RefIdRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"^(?<n>[\d.,]+)\s*(?<u>Bytes?|B|KB|MB|GB|TB)$", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"^Version:?\s*(?<v>\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();

    internal static List<WebItem> ParseItems(string html, Uri pageUri)
    {
        var items = new List<WebItem>();
        var seen = new HashSet<string>();

        foreach (var row in RowSplitRegex().Split(html).Skip(1))
        {
            var a = TitleLinkRegex().Match(row);
            if (!a.Success)
                continue;

            var href = WebUtility.HtmlDecode(a.Groups["href"].Value);
            var title = Clean(a.Groups["title"].Value);
            if (title.Length == 0 || !Uri.TryCreate(pageUri, href, out var uri))
                continue;

            var props = PropertyRegex().Matches(row).Select(p => Clean(p.Groups["p"].Value)).Where(p => p.Length > 0).ToList();

            if (FileLinkRegex().Match(href) is { Success: true } f)
            {
                var id = f.Groups["id"].Value;
                if (!seen.Add("f" + id))
                    continue;

                string? ext = null, version = null;
                long? size = null;
                DateTimeOffset? date = null;
                foreach (var p in props)
                {
                    if (ext is null && p.Length <= 10 && Regex.IsMatch(p, @"^[A-Za-z0-9]+$"))
                        ext = p.ToLowerInvariant();
                    else if (size is null && SizeRegex().Match(p) is { Success: true } sm)
                        size = ParseSize(sm.Groups["n"].Value, sm.Groups["u"].Value);
                    else if (version is null && VersionRegex().Match(p) is { Success: true } vm)
                        version = vm.Groups["v"].Value;
                    else if (date is null && ParseIliasDate(p) is { } d)
                        date = d;
                }

                // Fingerabdruck für Änderungserkennung (Web liefert keine ETags)
                var fp = $"web:{ext}|{size}|v{version}|{date?.ToUnixTimeSeconds()}";
                items.Add(new WebItem(WebItemKind.File, id, title, uri, ext, size, version, date, fp));
            }
            else if (ContainerLinkRegex().Match(href) is { Success: true } c)
            {
                var id = c.Groups["id"].Value;
                if (!seen.Add("c" + id))
                    continue;
                var folderUri = new Uri(pageUri, $"ilias.php?baseClass=ilrepositorygui&ref_id={id}");
                items.Add(new WebItem(WebItemKind.Folder, id, title, folderUri, null, null, null, null, ""));
            }
        }

        return items;
    }

    private static string Clean(string s) =>
        Regex.Replace(WebUtility.HtmlDecode(TagRegex().Replace(s, " ")), @"\s+", " ").Trim();

    internal static long ParseSize(string number, string unit)
    {
        // ILIAS zeigt je nach Sprache "662.11 KB" oder "662,11 KB" (Basis 1000)
        var n = number.Replace(',', '.');
        if (n.Count(ch => ch == '.') > 1)
            n = n.Replace(".", "");
        var value = double.Parse(n, CultureInfo.InvariantCulture);
        var factor = unit.ToUpperInvariant() switch
        {
            "KB" => 1e3,
            "MB" => 1e6,
            "GB" => 1e9,
            "TB" => 1e12,
            _ => 1,
        };
        return (long)Math.Round(value * factor);
    }

    private static readonly string[] GermanMonths = ["Jan", "Feb", "Mär", "Apr", "Mai", "Jun", "Jul", "Aug", "Sep", "Okt", "Nov", "Dez"];
    private static readonly string[] EnglishMonths = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    private static readonly TimeZoneInfo Berlin = FindBerlin();

    private static TimeZoneInfo FindBerlin()
    {
        foreach (var id in new[] { "Europe/Berlin", "W. Europe Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { /* nächster */ }
        }
        return TimeZoneInfo.Local;
    }

    /// <summary>"20. Aug 2026, 12:20", "Heute, 12:20", "Gestern, 09:01" → Zeitpunkt.</summary>
    internal static DateTimeOffset? ParseIliasDate(string text, DateTime? today = null)
    {
        var t = text.Trim();
        var now = today ?? TimeZoneInfo.ConvertTime(DateTime.UtcNow, Berlin).Date;

        var rel = Regex.Match(t, @"^(?<d>Heute|Gestern|Morgen|Today|Yesterday|Tomorrow),\s*(?<h>\d{1,2}):(?<m>\d{2})$", RegexOptions.IgnoreCase);
        if (rel.Success)
        {
            var offset = rel.Groups["d"].Value.ToLowerInvariant() switch
            {
                "gestern" or "yesterday" => -1,
                "morgen" or "tomorrow" => 1,
                _ => 0,
            };
            return ToOffset(now.AddDays(offset).AddHours(int.Parse(rel.Groups["h"].Value)).AddMinutes(int.Parse(rel.Groups["m"].Value)));
        }

        var abs = Regex.Match(t, @"^(?<d>\d{1,2})\.\s*(?<mon>[A-Za-zÄäÖöÜü]{3})[a-zäöü.]*\s+(?<y>\d{4}),?\s*(?<h>\d{1,2}):(?<m>\d{2})$");
        if (!abs.Success)
            return null;

        var monText = abs.Groups["mon"].Value;
        var month = Array.FindIndex(GermanMonths, x => x.Equals(monText, StringComparison.OrdinalIgnoreCase));
        if (month < 0)
            month = Array.FindIndex(EnglishMonths, x => x.Equals(monText, StringComparison.OrdinalIgnoreCase));
        if (month < 0)
            return null;

        try
        {
            var local = new DateTime(int.Parse(abs.Groups["y"].Value), month + 1, int.Parse(abs.Groups["d"].Value),
                int.Parse(abs.Groups["h"].Value), int.Parse(abs.Groups["m"].Value), 0, DateTimeKind.Unspecified);
            return ToOffset(local);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static DateTimeOffset ToOffset(DateTime local)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, Berlin.GetUtcOffset(local)).ToUniversalTime();
    }
}
