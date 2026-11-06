using Linksta.Util;

namespace Linksta.Sync;

public enum ChangeKind
{
    /// <summary>Neu auf ILIAS.</summary>
    New,
    /// <summary>Auf ILIAS geändert, lokal unverändert.</summary>
    Updated,
    /// <summary>Lokal gelöscht, wird wiederhergestellt.</summary>
    Restore,
    /// <summary>Auf ILIAS und lokal geändert → Remote laden, lokale Version wird umbenannt.</summary>
    Conflict,
    /// <summary>Datei existiert lokal schon (z. B. manuell kopiert) und ist gleich groß → nur übernehmen.</summary>
    Adopt,
    /// <summary>Lokal bearbeitet, auf ILIAS unverändert → bleibt wie sie ist.</summary>
    LocalModified,
    /// <summary>Auf ILIAS gelöscht.</summary>
    RemoteDeleted,
    /// <summary>Früher schon als gelöscht gemeldet.</summary>
    Orphaned,
    /// <summary>Durch Ausschlussmuster ausgeschlossen.</summary>
    Excluded,
    /// <summary>Größer als das Limit.</summary>
    TooLarge,
    Unchanged,
}

public sealed record PlanItem(ChangeKind Kind, string RelPath, RemoteFile? Remote, FileRecord? Record)
{
    public bool NeedsDownload => Kind is ChangeKind.New or ChangeKind.Updated or ChangeKind.Restore or ChangeKind.Conflict;
}

public sealed class SyncPlan
{
    public List<PlanItem> Items { get; } = [];

    public IEnumerable<PlanItem> Downloads => Items.Where(i => i.NeedsDownload);

    public long DownloadBytes => Downloads.Sum(i => i.Remote?.Size ?? 0);

    public int Count(ChangeKind k) => Items.Count(i => i.Kind == k);

    public bool HasChanges => Items.Any(i => i.Kind is not (ChangeKind.Unchanged or ChangeKind.Excluded or ChangeKind.Orphaned or ChangeKind.LocalModified));
}

public static class SyncPlanner
{
    public static SyncPlan Plan(
        ScanResult scan,
        SyncState state,
        string localRoot,
        GlobMatcher exclude,
        long maxFileSize)
    {
        var plan = new SyncPlan();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var remote in scan.Files)
        {
            seen.Add(remote.RelPath);
            state.Files.TryGetValue(remote.RelPath, out var rec);

            if (exclude.IsMatch(remote.RelPath))
            {
                plan.Items.Add(new PlanItem(ChangeKind.Excluded, remote.RelPath, remote, rec));
                continue;
            }

            if (maxFileSize > 0 && remote.Size > maxFileSize)
            {
                plan.Items.Add(new PlanItem(ChangeKind.TooLarge, remote.RelPath, remote, rec));
                continue;
            }

            var local = new FileInfo(PathUtil.ToLocal(localRoot, remote.RelPath));

            if (rec is null)
            {
                if (!local.Exists)
                    plan.Items.Add(new PlanItem(ChangeKind.New, remote.RelPath, remote, null));
                else if (remote.ExactSize && remote.Size is { } sz && local.Length == sz)
                    plan.Items.Add(new PlanItem(ChangeKind.Adopt, remote.RelPath, remote, null));
                else
                    plan.Items.Add(new PlanItem(ChangeKind.Conflict, remote.RelPath, remote, null));
                continue;
            }

            var remoteChanged = RemoteChanged(rec, remote) || rec.Orphaned;

            if (!local.Exists)
            {
                plan.Items.Add(new PlanItem(remoteChanged ? ChangeKind.Updated : ChangeKind.Restore, remote.RelPath, remote, rec));
                continue;
            }

            var localChanged = LocalChanged(rec, local);
            var kind = (remoteChanged, localChanged) switch
            {
                (false, false) => ChangeKind.Unchanged,
                (true, false) => ChangeKind.Updated,
                (false, true) => ChangeKind.LocalModified,
                (true, true) => ChangeKind.Conflict,
            };
            plan.Items.Add(new PlanItem(kind, remote.RelPath, remote, rec));
        }

        // Dateien im State, die auf ILIAS fehlen. Nicht melden, wenn der Ordner nicht gelesen werden konnte.
        var failedPrefixes = scan.Errors.Select(e => e.RelPath).ToList();
        foreach (var (rel, rec) in state.Files)
        {
            if (seen.Contains(rel))
                continue;
            if (failedPrefixes.Any(p => p.Length == 0 || rel.StartsWith(p + "/", StringComparison.Ordinal)))
                continue;

            plan.Items.Add(new PlanItem(rec.Orphaned ? ChangeKind.Orphaned : ChangeKind.RemoteDeleted, rel, null, rec));
        }

        return plan;
    }

    internal static bool RemoteChanged(FileRecord rec, RemoteFile remote)
    {
        if (rec.ETag is not null && remote.ETag is not null)
            return rec.ETag != remote.ETag;

        if (rec.RemoteSize != remote.Size)
            return true;

        if (rec.RemoteModified is { } a && remote.LastModified is { } b)
            return Math.Abs((a - b).TotalSeconds) > 1;

        return false;
    }

    internal static bool LocalChanged(FileRecord rec, FileInfo local) =>
        local.Length != rec.LocalSize ||
        Math.Abs((local.LastWriteTimeUtc - rec.LocalModifiedUtc).TotalSeconds) > 2;
}
