using System.Collections.Concurrent;
using Linksta.Util;
using Linksta.WebDav;

namespace Linksta.Sync;

/// <param name="ExactSize">false, wenn die Größe nur aus der Web-Anzeige geschätzt ist ("662.11 KB").</param>
public sealed record RemoteFile(string RelPath, Uri Href, long? Size, DateTimeOffset? LastModified, string? ETag, bool ExactSize = true);

/// <summary>Etwas, von dem man Dateien herunterladen kann (WebDAV oder ILIAS-Weboberfläche).</summary>
public interface IFileSource
{
    Task DownloadAsync(Uri file, Stream destination, IProgress<long>? progress = null, CancellationToken ct = default);
}

/// <summary>Liest den Dateibaum eines Kurses.</summary>
public interface ICourseScanner
{
    Task<ScanResult> ScanAsync(Uri root, Action<string>? onFolder = null, CancellationToken ct = default);
}

public sealed record ScanError(string RelPath, string Message);

public sealed class ScanResult
{
    public List<RemoteFile> Files { get; } = [];
    public List<ScanError> Errors { get; } = [];
    public int FolderCount { get; set; }
}

/// <summary>Läuft rekursiv durch eine ILIAS-WebDAV-Collection.</summary>
public sealed class RemoteScanner(WebDavClient client, int parallelism = 4) : ICourseScanner
{
    public async Task<ScanResult> ScanAsync(Uri root, Action<string>? onFolder = null, CancellationToken ct = default)
    {
        var result = new ScanResult();
        var files = new ConcurrentBag<RemoteFile>();
        var errors = new ConcurrentBag<ScanError>();
        var folders = 0;
        using var gate = new SemaphoreSlim(Math.Max(1, parallelism));

        async Task Walk(Uri uri, string relPath, int depth)
        {
            if (depth > 40)
            {
                errors.Add(new ScanError(relPath, "Zu tief verschachtelt – übersprungen."));
                return;
            }

            IReadOnlyList<DavItem> items;
            await gate.WaitAsync(ct);
            try
            {
                onFolder?.Invoke(relPath.Length == 0 ? "/" : relPath);
                items = await client.ListAsync(uri, ct);
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

            // Stabile Reihenfolge → stabile Namen bei Duplikaten
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var children = new List<Task>();
            foreach (var item in items.OrderBy(i => i.IsCollection ? 0 : 1).ThenBy(i => i.Name, StringComparer.Ordinal).ThenBy(i => i.Href.AbsoluteUri, StringComparer.Ordinal))
            {
                var safe = PathUtil.MakeUnique(PathUtil.SanitizeSegment(item.Name), taken, !item.IsCollection);
                var childRel = relPath.Length == 0 ? safe : $"{relPath}/{safe}";

                if (item.IsCollection)
                    children.Add(Walk(item.Href, childRel, depth + 1));
                else
                    files.Add(new RemoteFile(childRel, item.Href, item.Size, item.LastModified, item.ETag));
            }

            await Task.WhenAll(children);
        }

        await Walk(WebDavClient.EnsureTrailingSlash(root), "", 0);

        result.Files.AddRange(files.OrderBy(f => f.RelPath, StringComparer.Ordinal));
        result.Errors.AddRange(errors.OrderBy(e => e.RelPath, StringComparer.Ordinal));
        result.FolderCount = folders;
        return result;
    }
}
