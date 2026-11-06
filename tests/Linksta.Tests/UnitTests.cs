using Linksta.Ilias;
using Linksta.Util;
using Linksta.WebDav;

namespace Linksta.Tests;

public class IliasLinkTests
{
    [Theory]
    [InlineData("12345", "ref_12345")]
    [InlineData("ref_12345", "ref_12345")]
    [InlineData("https://ilias.uni.de/goto.php?target=crs_12345&client_id=uni", "ref_12345")]
    [InlineData("https://ilias.uni.de/goto.php?target=grp_777", "ref_777")]
    [InlineData("https://ilias.uni.de/goto.php/crs/4711", "ref_4711")]
    [InlineData("https://ilias.uni.de/goto_uniclient_crs_98765.html", "ref_98765")]
    [InlineData("https://ilias.uni.de/ilias.php?baseClass=ilrepositorygui&ref_id=555", "ref_555")]
    [InlineData("https://ilias.uni.de/webdav.php/uni/ref_42/", "ref_42")]
    [InlineData("https://ilias.uni.de/webdav.php/uni/ref_1/Fakult%C3%A4t/Kurs", "ref_1/Fakultät/Kurs")]
    public void ParsesLinks(string input, string expected)
    {
        Assert.True(IliasLink.TryGetWebDavPath(input, out var path));
        Assert.Equal(expected, path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hallo welt")]
    [InlineData("https://example.com/")]
    public void RejectsGarbage(string input) => Assert.False(IliasLink.TryGetWebDavPath(input, out _));

    [Theory]
    [InlineData("https://ilias.uni.de/goto_myuni_crs_123.html", "https://ilias.uni.de", "myuni")]
    [InlineData("https://ilias.uni.de/ilias3/ilias.php?ref_id=1&client_id=abc", "https://ilias.uni.de/ilias3", "abc")]
    [InlineData("https://ilias.uni.de/webdav.php/kit/ref_5/", "https://ilias.uni.de", "kit")]
    [InlineData("https://ilias.uni.de/", "https://ilias.uni.de", null)]
    public void GuessesServer(string url, string baseUrl, string? client)
    {
        var (b, c) = IliasLink.GuessServer(url);
        Assert.Equal(baseUrl, b);
        Assert.Equal(client, c);
    }

    [Fact]
    public void CombineEscapesSegments()
    {
        var uri = IliasLink.Combine(new Uri("https://x.de/webdav.php/c/"), "ref_1/Mathe & Info/Ü 1");
        Assert.Equal("https://x.de/webdav.php/c/ref_1/Mathe%20%26%20Info/%C3%9C%201/", uri.AbsoluteUri);
    }
}

public class PathUtilTests
{
    [Theory]
    [InlineData("Übung 1: Lösung?.pdf", "Übung 1_ Lösung_.pdf")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("Ordner. ", "Ordner")]
    [InlineData("a/b\\c", "a_b_c")]
    [InlineData("..", "_")]
    public void Sanitizes(string input, string expected) => Assert.Equal(expected, PathUtil.SanitizeSegment(input));

    [Fact]
    public void MakesUnique()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal("a.pdf", PathUtil.MakeUnique("a.pdf", taken, true));
        Assert.Equal("A (2).pdf", PathUtil.MakeUnique("A.pdf", taken, true));
        Assert.Equal("a (3).pdf", PathUtil.MakeUnique("a.pdf", taken, true));
    }

    [Theory]
    [InlineData("*.mp4", "Vorlesung/Aufzeichnung 1.MP4", true)]
    [InlineData("*.mp4", "Vorlesung/folien.pdf", false)]
    [InlineData("Videos", "Videos/a.pdf", true)]
    [InlineData("Videos", "Kurs/Videos/sub/a.pdf", true)]
    [InlineData("Übungen/**/Lösung*", "Übungen/Blatt 1/Lösung.pdf", true)]
    [InlineData("Übungen/**/Lösung*", "Übungen/Lösung.pdf", true)]
    [InlineData("Übungen/*.pdf", "Übungen/Blatt 1/x.pdf", false)]
    [InlineData("Übungen", "Übungen/Blatt 1/x.pdf", true)]
    [InlineData("Uebung?.pdf", "Uebung3.pdf", true)]
    public void GlobMatches(string pattern, string path, bool expected) =>
        Assert.Equal(expected, new GlobMatcher([pattern]).IsMatch(path));
}

public class MultiStatusTests
{
    [Fact]
    public void ParsesIliasStyleResponse()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <d:multistatus xmlns:d="DAV:">
              <d:response><d:href>/webdav.php/c/ref_5/</d:href>
                <d:propstat><d:prop><d:displayname>Analysis I</d:displayname><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
              <d:response><d:href>/webdav.php/c/ref_5/Skript%20WS.pdf</d:href>
                <d:propstat><d:prop><d:resourcetype/><d:getcontentlength>1234</d:getcontentlength><d:getlastmodified>Wed, 01 Oct 2026 12:00:00 GMT</d:getlastmodified><d:getetag>"abc"</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
                <d:propstat><d:prop><d:displayname/></d:prop><d:status>HTTP/1.1 404 Not Found</d:status></d:propstat></d:response>
              <d:response><d:href>/webdav.php/c/ref_5/%C3%9Cbungen</d:href>
                <d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
            </d:multistatus>
            """;

        var items = WebDavClient.ParseMultiStatus(xml, new Uri("https://x.de/webdav.php/c/ref_5/"));

        Assert.Equal(2, items.Count);
        var file = items[0];
        Assert.Equal("Skript WS.pdf", file.Name);
        Assert.False(file.IsCollection);
        Assert.Equal(1234, file.Size);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), file.LastModified);
        Assert.Equal("\"abc\"", file.ETag);

        var dir = items[1];
        Assert.Equal("Übungen", dir.Name);
        Assert.True(dir.IsCollection);
        Assert.EndsWith("/", dir.Href.AbsoluteUri);
    }

    [Fact]
    public void ThrowsReadableErrorOnHtml()
    {
        var ex = Assert.Throws<WebDavException>(() => WebDavClient.ParseMultiStatus("<html><body>Login</body", new Uri("https://x.de/")));
        Assert.Contains("WebDAV", ex.Message);
    }
}

public class ClientDetectionTests
{
    private sealed class Handler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body), RequestMessage = r });
    }

    [Fact]
    public async Task ReadsClientFromIliasErrorPage()
    {
        var id = await IliasLink.DetectClientIdAsync("https://ilias.test",
            handler: new Handler("Client does not exist.<br /><a href=\"index.php?client_id=HSKA\">Please click to continue.</a>"));
        Assert.Equal("HSKA", id);
    }
}

public class IliasWebSessionTests
{
    [Fact]
    public void FindsIlias9LoginForm()
    {
        const string html = """
            <form role="form" class="il-standard-form" enctype="multipart/form-data" name="login_form" action="ilias.php?baseClass=ilstartupgui&amp;cmd=post&amp;fallbackCmd=doStandardAuthentication" method="post">
              <input id="a" type="text" name="login_form/input_3/input_4" class="form-control" />
              <input id="b" type="password" name="login_form/input_3/input_5" autocomplete="off" />
              <button class="btn btn-default" data-action="">Anmelden</button>
            </form>
            """;
        var form = Linksta.Ilias.IliasWebSession.FindLoginForm(html);
        Assert.NotNull(form);
        Assert.Equal("login_form/input_3/input_4", form.UserField);
        Assert.Equal("login_form/input_3/input_5", form.PasswordField);
        Assert.Contains("fallbackCmd=doStandardAuthentication", form.Action);
    }

    [Fact]
    public void ParsesMembershipsAndIgnoresMainbar()
    {
        const string html = """
            <div class="il-mainbar"><a href="goto.php?target=crs_999&client_id=X">Zuletzt besucht</a></div>
            <div id="il_center_col">
              <div class="il-item-title"><a href="https://ilias.x/goto.php?target=crs_1106436&amp;client_id=HSKA">Wissenschaftstheorie <span>WS2026</span></a></div>
              <div class="il-item-title"><a href="https://ilias.x/goto.php/grp/1115536">Wintersemester 2026/27</a></div>
              <a href="https://ilias.x/goto.php?target=crs_1106436&amp;client_id=HSKA"><img src="x.svg"/></a>
              <a href="https://ilias.x/goto.php?target=file_5_download">Datei</a>
            </div>
            """;
        var items = Linksta.Ilias.IliasWebSession.ParseMemberships(html).ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(new Linksta.Ilias.MembershipItem("ref_1106436", "Wissenschaftstheorie WS2026", "crs"), items[0]);
        Assert.Equal("grp", items[1].Type);
    }
}

public class WebScannerTests
{
    private const string Page = """
        <title>Inhalt: Labor Algorithmen / Optimierung: ILIAS - Hochschule Karlsruhe (HKA)</title>
        <h1 class="il-page-content-header media-heading ilHeader ">Labor Algorithmen / Optimierung</h1>
        <div id="item_row__other-1" class="ilCLI ilObjListRow">
          <h3 class="il_ContainerItemTitle"><a href="https://ilias.h-ka.de/goto.php/grp/1115536" class="il_ContainerItemTitle"  >Wintersemester 2026/27</a></h3>
          <span class="il_ItemProperty">Anmeldungsende: 1. Dez 2026, 22:00&nbsp;&nbsp;</span>
        </div>
        <div id="item_row__other-2" class="ilCLI ilObjListRow">
          <h3><a href="https://ilias.h-ka.de/goto.php/fold/252252" class="il_ContainerItemTitle"  >Raytracing (WS 17/18)</a></h3>
        </div>
        <div id="item_row__other-3" class="ilCLI ilObjListRow">
          <h3><a href="https://ilias.h-ka.de/ilias.php?baseClass=ilrepositorygui&cmdNode=zb:ob&cmdClass=ilObjFileGUI&cmd=sendfile&ref_id=1106437" class="il_ContainerItemTitle"  >wiss_ws25_klausur</a></h3>
          <span class="il_ItemProperty">
            pdf&nbsp;&nbsp;</span>
          <span class="il_ItemProperty">662.11 KB&nbsp;&nbsp;</span>
          <span class="il_ItemProperty">Version: 5&nbsp;&nbsp;</span>
          <span class="il_ItemProperty">20. Aug 2026, 12:20&nbsp;&nbsp;</span>
          <span class="il_ItemProperty">Anzahl Seiten: 2&nbsp;&nbsp;</span>
        </div>
        <div id="item_row__other-4" class="ilCLI ilObjListRow">
          <h3><a href="ilias.php?baseClass=ilLinkResourceHandlerGUI&ref_id=1116689&cmd=calldirectlink" class="il_ContainerItemTitle" target="_blank">Ein Weblink</a></h3>
        </div>
        <div id="item_row__other-5" class="ilCLI ilObjListRow">
          <h3><a href="ilias.php?baseClass=ilrepositorygui&cmdClass=ilObjFileGUI&cmd=sendfile&ref_id=9" class="il_ContainerItemTitle">taylor</a></h3>
          <span class="il_ItemProperty">cc&nbsp;&nbsp;</span><span class="il_ItemProperty">1,27 KB</span><span class="il_ItemProperty">Heute, 15:40</span>
        </div>
        """;

    [Fact]
    public void ParsesFilesFoldersAndGroups()
    {
        var items = Linksta.Sync.WebScanner.ParseItems(Page, new Uri("https://ilias.h-ka.de/ilias.php?baseClass=ilrepositorygui&ref_id=28464"));

        Assert.Equal(4, items.Count); // Weblink wird übersprungen
        var grp = items[0];
        Assert.Equal(Linksta.Sync.WebItemKind.Folder, grp.Kind);
        Assert.Equal("Wintersemester 2026/27", grp.Title);
        Assert.Equal("https://ilias.h-ka.de/ilias.php?baseClass=ilrepositorygui&ref_id=1115536", grp.Href.AbsoluteUri);

        var file = items[2];
        Assert.Equal(Linksta.Sync.WebItemKind.File, file.Kind);
        Assert.Equal("pdf", file.Extension);
        Assert.Equal(662110, file.ApproxSize);
        Assert.Equal("5", file.Version);
        Assert.Equal(new DateTimeOffset(2026, 8, 20, 10, 20, 0, TimeSpan.Zero), file.Date); // 12:20 MESZ
        Assert.Equal("wiss_ws25_klausur.pdf", Linksta.Sync.WebScanner.FileName(file));

        Assert.Equal(1270, items[3].ApproxSize);
        Assert.Equal("taylor.cc", Linksta.Sync.WebScanner.FileName(items[3]));
    }

    [Theory]
    [InlineData("20. Aug 2026, 12:20", "2026-08-20T10:20:00Z")]
    [InlineData("5. Dez 2026, 09:05", "2026-12-05T08:05:00Z")]
    [InlineData("3. Mär 2027, 23:59", "2027-03-03T22:59:00Z")]
    [InlineData("Heute, 15:40", "2026-11-06T14:40:00Z")]
    [InlineData("Gestern, 08:00", "2026-11-05T07:00:00Z")]
    public void ParsesIliasDates(string text, string expected)
    {
        var d = Linksta.Sync.WebScanner.ParseIliasDate(text, new DateTime(2026, 11, 6));
        Assert.Equal(DateTimeOffset.Parse(expected), d);
    }

    [Fact]
    public void HeuteAndExplicitDateGiveSameFingerprint()
    {
        Assert.Equal(
            Linksta.Sync.WebScanner.ParseIliasDate("Heute, 15:40", new DateTime(2026, 11, 6)),
            Linksta.Sync.WebScanner.ParseIliasDate("6. Nov 2026, 15:40"));
    }

    [Fact]
    public void ParsesPageTitle()
    {
        Assert.Equal("Labor Algorithmen / Optimierung", Linksta.Ilias.IliasWebSession.ParseTitle(Page));
        Assert.Equal("Kurs X", Linksta.Ilias.IliasWebSession.ParseTitle("<title>Inhalt: Kurs X: ILIAS - Hochschule</title>"));
    }
}
