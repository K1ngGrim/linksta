using System.Net;
using System.Security;
using System.Text;

namespace Linksta.Tests;

/// <summary>Simuliert die WebDAV-Schnittstelle von ILIAS (webdav.php/&lt;client&gt;/ref_N/…).</summary>
public sealed class FakeIlias
{
    public abstract class Node(string name)
    {
        public string Name { get; set; } = name;
    }

    public sealed class Folder(string name) : Node(name)
    {
        public List<Node> Children { get; } = [];

        public Folder Dir(string name)
        {
            var f = new Folder(name);
            Children.Add(f);
            return f;
        }

        public FileNode File(string name, string content, DateTimeOffset? modified = null)
        {
            var f = new FileNode(name) { Content = Encoding.UTF8.GetBytes(content), Modified = modified ?? new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero) };
            Children.Add(f);
            return f;
        }
    }

    public sealed class FileNode(string name) : Node(name)
    {
        public byte[] Content { get; set; } = [];
        public DateTimeOffset Modified { get; set; }
        public bool Fail { get; set; }
    }

    public const string Client = "testclient";
    public const string User = "student";
    public const string Password = "geheim";

    public Dictionary<string, Folder> Refs { get; } = new();
    public int Requests;

    public Folder Course(int refId, string title)
    {
        var f = new Folder(title);
        Refs[$"ref_{refId}"] = f;
        return f;
    }

    public (int Status, string ContentType, byte[] Body) Handle(string method, string absolutePath, string? depth, string? authorization)
    {
        Interlocked.Increment(ref Requests);

        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}"));
        if (authorization != expected)
            return (401, "text/plain", "unauthorized"u8.ToArray());

        var prefix = $"/webdav.php/{Client}/";
        var idx = absolutePath.IndexOf(prefix, StringComparison.Ordinal);
        if (idx < 0)
            return (404, "text/plain", []);

        var rel = absolutePath[(idx + prefix.Length)..];
        var segments = rel.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();
        var basePath = absolutePath[..(idx + prefix.Length)];

        Node? node;
        if (segments.Count == 0)
        {
            node = new Folder("root");
            ((Folder)node).Children.AddRange(Refs.Values);
        }
        else if (!Refs.TryGetValue(segments[0], out var start))
        {
            return (404, "text/plain", []);
        }
        else
        {
            node = start;
            foreach (var seg in segments.Skip(1))
            {
                node = (node as Folder)?.Children.FirstOrDefault(c => c.Name == seg);
                if (node is null)
                    return (404, "text/plain", []);
            }
        }

        var selfHref = basePath + string.Join('/', segments.Select(Uri.EscapeDataString));

        if (method == "GET")
        {
            if (node is not FileNode file)
                return (405, "text/plain", []);
            if (file.Fail)
                return (500, "text/plain", []);
            return (200, "application/octet-stream", file.Content);
        }

        if (method != "PROPFIND")
            return (405, "text/plain", []);

        var sb = new StringBuilder("""<?xml version="1.0" encoding="utf-8"?><d:multistatus xmlns:d="DAV:">""");
        AppendResponse(sb, selfHref + (node is Folder ? "/" : ""), node);
        if (depth == "1" && node is Folder folder)
            foreach (var child in folder.Children)
                AppendResponse(sb, selfHref.TrimEnd('/') + "/" + Uri.EscapeDataString(child.Name) + (child is Folder ? "/" : ""), child);
        sb.Append("</d:multistatus>");
        return (207, "application/xml; charset=utf-8", Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static void AppendResponse(StringBuilder sb, string href, Node node)
    {
        sb.Append("<d:response><d:href>").Append(SecurityElement.Escape(href)).Append("</d:href><d:propstat><d:prop>");
        sb.Append("<d:displayname>").Append(SecurityElement.Escape(node.Name)).Append("</d:displayname>");
        if (node is Folder)
        {
            sb.Append("<d:resourcetype><d:collection/></d:resourcetype>");
        }
        else if (node is FileNode f)
        {
            sb.Append("<d:resourcetype/>");
            sb.Append("<d:getcontentlength>").Append(f.Content.Length).Append("</d:getcontentlength>");
            sb.Append("<d:getlastmodified>").Append(f.Modified.ToString("R")).Append("</d:getlastmodified>");
        }
        sb.Append("</d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>");
    }
}

/// <summary>Für Unit-Tests: FakeIlias als HttpMessageHandler.</summary>
public sealed class FakeIliasHandler(FakeIlias ilias) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var depth = request.Headers.TryGetValues("Depth", out var d) ? d.First() : null;
        var (status, type, body) = ilias.Handle(request.Method.Method, request.RequestUri!.AbsolutePath, depth, request.Headers.Authorization?.ToString());
        var res = new HttpResponseMessage((HttpStatusCode)status) { Content = new ByteArrayContent(body) };
        res.Content.Headers.TryAddWithoutValidation("Content-Type", type);
        return Task.FromResult(res);
    }
}

/// <summary>Für End-to-End-Tests: FakeIlias als echter HTTP-Server auf localhost.</summary>
public sealed class FakeIliasServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly FakeIlias _ilias;
    public string BaseUrl { get; }

    public FakeIliasServer(FakeIlias ilias)
    {
        _ilias = ilias;
        var port = FreePort();
        BaseUrl = $"http://127.0.0.1:{port}/ilias";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _ = Task.Run(Loop);
    }

    private async Task Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                return;
            }

            try
            {
                var (status, type, body) = _ilias.Handle(ctx.Request.HttpMethod, ctx.Request.Url!.AbsolutePath, ctx.Request.Headers["Depth"], ctx.Request.Headers["Authorization"]);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = type;
                ctx.Response.ContentLength64 = body.Length;
                await ctx.Response.OutputStream.WriteAsync(body);
                ctx.Response.Close();
            }
            catch
            {
                try { ctx.Response.Abort(); } catch { /* egal */ }
            }
        }
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
    }
}
