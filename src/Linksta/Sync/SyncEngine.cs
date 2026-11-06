using System.Collections.Concurrent;
using Linksta.Util;
using Linksta.WebDav;

namespace Linksta.Sync;

public interface ISyncReporter
{
    IProgress<long>? FileStarted(PlanItem item);
    void FileCompleted(PlanItem item);
    void FileFailed(PlanItem item, Exception error);
}

public sealed class SyncOptions
{
    public int Parallel { get; init; } = 4;

    /// <summary>Auf ILIAS gelöschte Dateien in den Papierkorb (.linksta/trash) verschieben.</summary>
    public bool Prune { get; init; }

    /// <summary>Bei Konflikten die lokale Version überschreiben statt sie umzubenennen.</summary>
    public bool OverwriteLocal { get; init; }
}

public sealed class SyncOutcome
{
    public ConcurrentBag<PlanItem> Downloaded { get; } = [];
    public ConcurrentBag<(PlanItem Item, string Message)> Failed { get; } = [];
    public ConcurrentBag<(PlanItem Item, string KeptAs)> ConflictCopies { get; } = [];
    public List<PlanItem> Pruned { get; } = [];
    public List<PlanItem> Adopted { get; } = [];
    public long Bytes;
}

public sealed class SyncEngine(IFileSource client)
{
    public async Task<SyncOutcome> ExecuteAsync(
        string courseName,
        string syncRoot,
        string localRoot,
        SyncState state,
        SyncPlan plan,
        SyncOptions options,
        ISyncReporter? reporter = null,
        CancellationToken ct = default)
    {
        var outcome = new SyncOutcome();
        var stateLock = new object();
        var history = new ConcurrentBag<HistoryEntry>();

        try
        {
            // 1) Bereits vorhandene Dateien übernehmen
            foreach (var item in plan.Items.Where(i => i.Kind == ChangeKind.Adopt))
            {
                var local = new FileInfo(PathUtil.ToLocal(localRoot, item.RelPath));
                state.Files[item.RelPath] = MakeRecord(item.Remote!, local);
                outcome.Adopted.Add(item);
            }

            // 2) Downloads (parallel)
            await Parallel.ForEachAsync(
                plan.Downloads.OrderBy(i => i.RelPath, StringComparer.Ordinal),
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Parallel), CancellationToken = ct },
                async (item, token) =>
                {
                    var progress = reporter?.FileStarted(item);
                    try
                    {
                        var (record, keptAs) = await DownloadOne(item, localRoot, options, progress, token);
                        lock (stateLock)
                            state.Files[item.RelPath] = record;

                        outcome.Downloaded.Add(item);
                        Interlocked.Add(ref outcome.Bytes, record.LocalSize);
                        if (keptAs is not null)
                            outcome.ConflictCopies.Add((item, keptAs));

                        history.Add(new HistoryEntry(DateTimeOffset.UtcNow, courseName, item.RelPath,
                            item.Kind == ChangeKind.New ? "new" : item.Kind.ToString().ToLowerInvariant(), record.LocalSize));
                        reporter?.FileCompleted(item);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        outcome.Failed.Add((item, e.Message));
                        reporter?.FileFailed(item, e);
                    }
                });

            // 3) Auf ILIAS gelöschte Dateien
            foreach (var item in plan.Items.Where(i => i.Kind is ChangeKind.RemoteDeleted or ChangeKind.Orphaned))
            {
                if (options.Prune)
                {
                    MoveToTrash(syncRoot, localRoot, item.RelPath);
                    state.Files.Remove(item.RelPath);
                    outcome.Pruned.Add(item);
                }
                else if (item.Record is not null)
                {
                    item.Record.Orphaned = true;
                }
            }

            if (options.Prune)
                DeleteEmptyDirectories(localRoot);
        }
        finally
        {
            // Auch bei Abbruch (Strg+C) den bisherigen Stand sichern
            state.Save();
            History.Append(syncRoot, history.OrderBy(h => h.Path, StringComparer.Ordinal));
        }

        return outcome;
    }

    private async Task<(FileRecord Record, string? KeptAs)> DownloadOne(
        PlanItem item, string localRoot, SyncOptions options, IProgress<long>? progress, CancellationToken ct)
    {
        var remote = item.Remote!;
        var target = PathUtil.ToLocal(localRoot, item.RelPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var part = target + ".linksta-part";
        try
        {
            await using (var fs = new FileStream(part, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true))
            {
                await client.DownloadAsync(remote.Href, fs, progress, ct);
            }

            string? keptAs = null;
            if (item.Kind == ChangeKind.Conflict && File.Exists(target) && !options.OverwriteLocal)
            {
                keptAs = ConflictName(target);
                File.Move(target, keptAs);
            }

            File.Move(part, target, overwrite: true);

            var info = new FileInfo(target);
            if (remote.LastModified is { } lm)
                info.LastWriteTimeUtc = lm.UtcDateTime;
            info.Refresh();

            return (MakeRecord(remote, info), keptAs is null ? null : Path.GetFileName(keptAs));
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    private static FileRecord MakeRecord(RemoteFile remote, FileInfo local) => new()
    {
        Href = remote.Href.AbsoluteUri,
        RemoteSize = remote.Size,
        RemoteModified = remote.LastModified,
        ETag = remote.ETag,
        LocalSize = local.Length,
        LocalModifiedUtc = local.LastWriteTimeUtc,
        SyncedAt = DateTimeOffset.UtcNow,
    };

    internal static string ConflictName(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm");
        var candidate = Path.Combine(dir, $"{stem} (lokal {stamp}){ext}");
        for (var i = 2; File.Exists(candidate); i++)
            candidate = Path.Combine(dir, $"{stem} (lokal {stamp} {i}){ext}");
        return candidate;
    }

    private static void MoveToTrash(string syncRoot, string localRoot, string relPath)
    {
        var src = PathUtil.ToLocal(localRoot, relPath);
        if (!File.Exists(src))
            return;

        var trashRoot = Path.Combine(SyncState.MetaDir(syncRoot), "trash", DateTime.Now.ToString("yyyy-MM-dd"));
        var courseFolder = Path.GetFileName(localRoot.TrimEnd(Path.DirectorySeparatorChar));
        var dest = PathUtil.ToLocal(Path.Combine(trashRoot, courseFolder), relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        if (File.Exists(dest))
            dest = ConflictName(dest);
        File.Move(src, dest);
    }

    private static void DeleteEmptyDirectories(string root)
    {
        if (!Directory.Exists(root))
            return;
        foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (IOException)
            {
                // in Benutzung – egal
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // egal
        }
    }
}
