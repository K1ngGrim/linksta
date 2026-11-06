using System.Net;
using System.Text;
using Linksta.Ilias;
using Linksta.Sync;
using Linksta.Util;
using Linksta.WebDav;

namespace Linksta.Tests;

public sealed class SyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "linksta-test-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeIlias _ilias = new();
    private readonly FakeIlias.Folder _course;
    private readonly WebDavClient _dav;
    private readonly Uri _courseUri;
    private string LocalRoot => Path.Combine(_root, "Analysis");

    public SyncTests()
    {
        _course = _ilias.Course(100, "Analysis I");
        _course.File("Skript.pdf", "skript v1");
        var uebung = _course.Dir("Übungen");
        uebung.File("Blatt 1.pdf", "blatt1");
        uebung.Dir("Lösungen").File("Lösung 1.pdf", "lsg1");
        _course.Dir("Videos").File("VL1.mp4", new string('x', 5000));

        _dav = new WebDavClient(FakeIlias.User, FakeIlias.Password, new FakeIliasHandler(_ilias), maxRetries: 1);
        _courseUri = IliasLink.Combine(IliasLink.WebDavRoot("https://ilias.test", FakeIlias.Client), "ref_100");
    }

    public void Dispose()
    {
        _dav.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private async Task<(SyncPlan Plan, SyncOutcome Outcome)> Pull(bool prune = false, string[]? exclude = null, long maxSize = 0, bool dryRun = false)
    {
        var scan = await new RemoteScanner(_dav).ScanAsync(_courseUri);
        Assert.Empty(scan.Errors);
        var state = SyncState.Load(_root, "c1");
        var plan = SyncPlanner.Plan(scan, state, LocalRoot, new GlobMatcher(exclude ?? []), maxSize);
        if (dryRun)
            return (plan, new SyncOutcome());
        var outcome = await new SyncEngine(_dav).ExecuteAsync("Analysis", _root, LocalRoot, state, plan, new SyncOptions { Prune = prune });
        return (plan, outcome);
    }

    private string Local(string rel) => PathUtil.ToLocal(LocalRoot, rel);

    [Fact]
    public async Task FirstPullDownloadsEverything()
    {
        var (plan, outcome) = await Pull();

        Assert.Equal(4, plan.Count(ChangeKind.New));
        Assert.Equal(4, outcome.Downloaded.Count);
        Assert.Equal("skript v1", File.ReadAllText(Local("Skript.pdf")));
        Assert.Equal("lsg1", File.ReadAllText(Local("Übungen/Lösungen/Lösung 1.pdf")));
        Assert.Equal(new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(Local("Skript.pdf")));
        Assert.Empty(Directory.GetFiles(LocalRoot, "*.linksta-part", SearchOption.AllDirectories));
        Assert.Equal(4, History.Read(_root).Count);
    }

    [Fact]
    public async Task SecondPullIsNoop()
    {
        await Pull();
        var requestsBefore = _ilias.Requests;
        var (plan, outcome) = await Pull();

        Assert.False(plan.HasChanges);
        Assert.Empty(outcome.Downloaded);
        Assert.Equal(4, plan.Count(ChangeKind.Unchanged));
        Assert.Equal(4, _ilias.Requests - requestsBefore); // nur PROPFINDs, keine GETs
    }

    [Fact]
    public async Task RemoteUpdateIsDownloaded()
    {
        await Pull();
        var f = (FakeIlias.FileNode)_course.Children.First(c => c.Name == "Skript.pdf");
        f.Content = Encoding.UTF8.GetBytes("skript v2 – mit Korrekturen");
        f.Modified = f.Modified.AddDays(3);

        var (plan, outcome) = await Pull();

        Assert.Equal(1, plan.Count(ChangeKind.Updated));
        Assert.Single(outcome.Downloaded);
        Assert.Equal("skript v2 – mit Korrekturen", File.ReadAllText(Local("Skript.pdf")));
    }

    [Fact]
    public async Task LocalEditIsKeptWhenRemoteUnchanged()
    {
        await Pull();
        File.WriteAllText(Local("Skript.pdf"), "meine Notizen");

        var (plan, outcome) = await Pull();

        Assert.Equal(1, plan.Count(ChangeKind.LocalModified));
        Assert.Empty(outcome.Downloaded);
        Assert.Equal("meine Notizen", File.ReadAllText(Local("Skript.pdf")));
    }

    [Fact]
    public async Task ConflictKeepsLocalCopy()
    {
        await Pull();
        File.WriteAllText(Local("Skript.pdf"), "meine Notizen");
        var f = (FakeIlias.FileNode)_course.Children.First(c => c.Name == "Skript.pdf");
        f.Content = "skript v2"u8.ToArray();
        f.Modified = f.Modified.AddDays(1);

        var (plan, outcome) = await Pull();

        Assert.Equal(1, plan.Count(ChangeKind.Conflict));
        Assert.Equal("skript v2", File.ReadAllText(Local("Skript.pdf")));
        var copy = Assert.Single(Directory.GetFiles(LocalRoot, "Skript (lokal *).pdf"));
        Assert.Equal("meine Notizen", File.ReadAllText(copy));
        Assert.Single(outcome.ConflictCopies);
    }

    [Fact]
    public async Task DeletedLocallyIsRestored()
    {
        await Pull();
        File.Delete(Local("Übungen/Blatt 1.pdf"));

        var (plan, _) = await Pull();

        Assert.Equal(1, plan.Count(ChangeKind.Restore));
        Assert.True(File.Exists(Local("Übungen/Blatt 1.pdf")));
    }

    [Fact]
    public async Task RemoteDeleteIsKeptByDefaultAndPrunedOnRequest()
    {
        await Pull();
        _course.Children.RemoveAll(c => c.Name == "Skript.pdf");

        var (plan, _) = await Pull();
        Assert.Equal(1, plan.Count(ChangeKind.RemoteDeleted));
        Assert.True(File.Exists(Local("Skript.pdf")));

        // Beim zweiten Mal nur noch "orphaned", nicht erneut gemeldet
        var (plan2, _) = await Pull(dryRun: true);
        Assert.Equal(1, plan2.Count(ChangeKind.Orphaned));
        Assert.False(plan2.HasChanges);

        var (_, outcome) = await Pull(prune: true);
        Assert.Single(outcome.Pruned);
        Assert.False(File.Exists(Local("Skript.pdf")));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, ".linksta", "trash"), "Skript.pdf", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ExcludesAndSizeLimit()
    {
        var (plan, outcome) = await Pull(exclude: ["Lösungen"], maxSize: 1000);

        Assert.Equal(1, plan.Count(ChangeKind.Excluded));
        Assert.Equal(1, plan.Count(ChangeKind.TooLarge));
        Assert.Equal(2, outcome.Downloaded.Count);
        Assert.False(File.Exists(Local("Videos/VL1.mp4")));
        Assert.False(File.Exists(Local("Übungen/Lösungen/Lösung 1.pdf")));
    }

    [Fact]
    public async Task DuplicateAndInvalidNamesAreMadeSafe()
    {
        _course.File("Folien: Teil 1.pdf", "a");
        _course.File("Folien: Teil 1.pdf", "b"); // ILIAS erlaubt gleiche Titel

        await Pull();

        Assert.True(File.Exists(Local("Folien_ Teil 1.pdf")));
        Assert.True(File.Exists(Local("Folien_ Teil 1 (2).pdf")));
    }

    [Fact]
    public async Task FailedDownloadDoesNotStopOthers()
    {
        ((FakeIlias.FileNode)_course.Children.First(c => c.Name == "Skript.pdf")).Fail = true;

        var (_, outcome) = await Pull();

        Assert.Single(outcome.Failed);
        Assert.Equal(3, outcome.Downloaded.Count);
        Assert.False(File.Exists(Local("Skript.pdf")));

        // Beim nächsten Mal wird es erneut versucht
        ((FakeIlias.FileNode)_course.Children.First(c => c.Name == "Skript.pdf")).Fail = false;
        var (plan, _) = await Pull();
        Assert.Equal(1, plan.Count(ChangeKind.New));
    }

    [Fact]
    public async Task ExistingIdenticalFileIsAdopted()
    {
        Directory.CreateDirectory(LocalRoot);
        File.WriteAllText(Local("Skript.pdf"), "skript v1");

        var (plan, _) = await Pull();

        Assert.Equal(1, plan.Count(ChangeKind.Adopt));
        Assert.Equal(3, plan.Count(ChangeKind.New));
    }

    [Fact]
    public async Task WrongPasswordGives401()
    {
        using var dav = new WebDavClient(FakeIlias.User, "falsch", new FakeIliasHandler(_ilias));
        var ex = await Assert.ThrowsAsync<WebDavException>(() => dav.ListAsync(_courseUri));
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    [Fact]
    public async Task GetInfoReturnsCourseTitle()
    {
        var info = await _dav.GetInfoAsync(_courseUri);
        Assert.NotNull(info);
        Assert.Equal("Analysis I", info.Name);
        Assert.True(info.IsCollection);
    }
}
