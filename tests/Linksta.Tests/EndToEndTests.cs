using System.Diagnostics;

namespace Linksta.Tests;

/// <summary>Startet die echte linksta-CLI gegen einen lokalen Fake-ILIAS-Server.</summary>
public sealed class EndToEndTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "linksta-e2e-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeIlias _ilias = new();
    private readonly FakeIliasServer _server;

    public EndToEndTests()
    {
        var c = _ilias.Course(4711, "Theoretische Informatik");
        c.File("Skript.pdf", "skript");
        c.Dir("Übungsblätter").File("Blatt 01.pdf", "b1");
        c.Dir("Aufzeichnungen").File("VL 01.mp4", "video");
        _server = new FakeIliasServer(_ilias);
    }

    public void Dispose()
    {
        _server.Dispose();
        if (Directory.Exists(_home))
            Directory.Delete(_home, true);
    }

    private (int Code, string Output) Run(params string[] args)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "linksta.dll");
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(dll);
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment["LINKSTA_HOME"] = Path.Combine(_home, "config");
        psi.Environment["LINKSTA_PASSWORD"] = FakeIlias.Password;
        psi.Environment["COLUMNS"] = "200";
        psi.Environment["NO_COLOR"] = "1";

        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60_000))
        {
            p.Kill();
            throw new TimeoutException(string.Join(' ', args));
        }
        var output = stdout.Result + stderr.Result;
        if (Environment.GetEnvironmentVariable("LINKSTA_TEST_DUMP") is { Length: > 0 } dump)
            File.AppendAllText(dump, $"$ linksta {string.Join(' ', args)}\n{output}\n");
        return (p.ExitCode, output);
    }

    [Fact]
    public void FullWorkflow()
    {
        var sync = Path.Combine(_home, "ILIAS");

        var init = Run("init", "--url", $"{_server.BaseUrl}/goto_{FakeIlias.Client}_crs_4711.html", "--user", FakeIlias.User, "--dir", sync);
        Assert.True(init.Code == 0, init.Output);
        Assert.Contains("Verbindung erfolgreich", init.Output);

        var add = Run("add", $"{_server.BaseUrl}/goto.php?target=crs_4711");
        Assert.True(add.Code == 0, add.Output);
        Assert.Contains("Theoretische Informatik", add.Output);

        var ex = Run("exclude", "add", "*.mp4");
        Assert.Equal(0, ex.Code);

        var status = Run("status");
        Assert.True(status.Code == 0, status.Output);
        Assert.Contains("Skript.pdf", status.Output);
        Assert.False(File.Exists(Path.Combine(sync, "Theoretische Informatik", "Skript.pdf")));

        var pull = Run("pull");
        Assert.True(pull.Code == 0, pull.Output);
        Assert.Equal("skript", File.ReadAllText(Path.Combine(sync, "Theoretische Informatik", "Skript.pdf")));
        Assert.Equal("b1", File.ReadAllText(Path.Combine(sync, "Theoretische Informatik", "Übungsblätter", "Blatt 01.pdf")));
        Assert.False(File.Exists(Path.Combine(sync, "Theoretische Informatik", "Aufzeichnungen", "VL 01.mp4")));

        var again = Run("pull");
        Assert.Contains("Aktuell", again.Output);

        var list = Run("list");
        Assert.Contains("Theoretische", list.Output);

        var recent = Run("recent");
        Assert.Contains("Blatt 01.pdf", recent.Output);

        var search = Run("search", "blatt");
        Assert.True(search.Code == 0, search.Output);
        Assert.Contains("Blatt 01.pdf", search.Output);

        var pause = Run("pause", "Theoretische");
        Assert.Equal(0, pause.Code);
        Assert.Contains("pausiert", Run("pull").Output);
    }

    [Fact]
    public void WrongPasswordIsReported()
    {
        var sync = Path.Combine(_home, "ILIAS");
        var init = Run("init", "--url", _server.BaseUrl, "--client", FakeIlias.Client, "--user", "jemand", "--dir", sync);
        Assert.NotEqual(0, init.Code);
        Assert.Contains("401", init.Output);
        Assert.False(File.Exists(Path.Combine(_home, "config", "config.json")));
    }
}
