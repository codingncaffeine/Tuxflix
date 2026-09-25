using System.Net;
using Tuxflix.Core.Plex;
using Xunit;

namespace Tuxflix.Tests;

/// <summary>A server's answers name paths on the server; nothing they name may take the token, or the player, anywhere else.</summary>
public sealed class ServerAddressTests
{
    private const string Base = "https://192-168-1-20.abc.plex.direct:32400/";

    // Each leads away from the server: another host, a scheme-relative host, the same host by plain
    // http or another port, and schemes the player would take for a local file or a filter graph.
    public static TheoryData<string> AwayFromTheServer =>
    [
        "https://elsewhere.example/steal",
        "//elsewhere.example/steal",
        "http://192-168-1-20.abc.plex.direct:32400/library/streams/5",
        "https://192-168-1-20.abc.plex.direct:32401/library/streams/5",
        "file:///etc/passwd",
        "lavfi://sine",
    ];

    private static (PlexServerClient Client, FakeNetwork Network) Server()
    {
        var network = new FakeNetwork(_ => Task.FromResult(FakeNetwork.Json("""{"MediaContainer":{"size":0}}""")));
        var http = new PlexClientIdentity("test-client", "0.0.0", "tests").CreateHttpClient(network);
        return (new PlexServerClient(http, new Uri(Base), "server-token", "Den"), network);
    }

    [Theory]
    [MemberData(nameof(AwayFromTheServer))]
    public async Task AKeyAwayFromTheServerIsNeverFollowed(string key)
    {
        var (client, network) = Server();
        var cancellation = TestContext.Current.CancellationToken;

        Assert.Null(client.MediaUri(key));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetLyricsAsync(new MediaStream { Key = key }, cancellation));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.RequestPartAsync(key, 0, null, cancellation));
        if (Uri.TryCreate(key, UriKind.Absolute, out var absolute))
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetBytesAsync(absolute, cancellation));
        }

        Assert.Empty(network.Sent);
    }

    [Fact]
    public async Task AKeyOnTheServerIsFollowedWithTheToken()
    {
        var (client, network) = Server();
        var cancellation = TestContext.Current.CancellationToken;

        Assert.Equal(Base + "library/parts/7/1690000000/file.mkv", client.MediaUri("/library/parts/7/1690000000/file.mkv")?.AbsoluteUri);
        Assert.Equal(Base + "library/streams/5", client.MediaUri(Base + "library/streams/5")?.AbsoluteUri);
        await client.GetLyricsAsync(new MediaStream { Key = "/library/streams/5" }, cancellation);
        (await client.RequestPartAsync("/library/parts/7/1690000000/file.mkv", 0, null, cancellation)).Dispose();

        Assert.Equal(
            [(Base + "library/streams/5", (string?)"server-token"), (Base + "library/parts/7/1690000000/file.mkv?download=1", "server-token")],
            network.Sent);
    }
}

/// <summary>A redirect may take a request elsewhere, never the sign-in with it.</summary>
public sealed class RedirectTests
{
    private static HttpResponseMessage Redirect(string location, HttpStatusCode status = HttpStatusCode.Found)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static FakeNetwork Network() => new(request => Task.FromResult(request.RequestUri!.AbsolutePath switch
    {
        "/away" => Redirect("https://sign-in.example/login"),
        "/home" => Redirect("/library/sections"),
        "/down" => Redirect("http://server.example/library/sections"),
        "/there-and-back" => Redirect("https://sign-in.example/bounce"),
        "/bounce" => Redirect("https://server.example/library/sections", HttpStatusCode.TemporaryRedirect),
        _ => FakeNetwork.Json("""{"MediaContainer":{"size":0}}"""),
    }));

    private static async Task<List<(string Uri, string? Token)>> FollowAsync(string path)
    {
        var network = Network();
        var http = new PlexClientIdentity("test-client", "0.0.0", "tests").CreateHttpClient(network);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://server.example" + path);
        request.Headers.TryAddWithoutValidation("X-Plex-Token", "server-token");
        using var _ = await http.SendAsync(request, TestContext.Current.CancellationToken);
        return network.Sent;
    }

    [Fact]
    public async Task AHopToAnotherHostLeavesTheTokenBehind() =>
        Assert.Equal([("https://server.example/away", (string?)"server-token"), ("https://sign-in.example/login", null)], await FollowAsync("/away"));

    [Fact]
    public async Task AHopOnTheSameServerKeepsIt() =>
        Assert.Equal([("https://server.example/home", (string?)"server-token"), ("https://server.example/library/sections", "server-token")], await FollowAsync("/home"));

    [Fact]
    public async Task ASecureRequestIsNeverRedirectedToPlainHttp() =>
        Assert.Equal([("https://server.example/down", (string?)"server-token")], await FollowAsync("/down"));

    [Fact]
    public async Task ATokenLeftBehindStaysBehindComingBack() =>
        Assert.Equal(
            [("https://server.example/there-and-back", (string?)"server-token"), ("https://sign-in.example/bounce", null), ("https://server.example/library/sections", null)],
            await FollowAsync("/there-and-back"));

    /// <summary>
    /// The real network stack, on loopback: the runtime's own redirect following must stay off, or
    /// the guard never sees a redirect and the token goes along. 127.0.0.2 is another origin.
    /// </summary>
    [Fact]
    public async Task OnARealSocketTheTokenStopsAtTheOrigin()
    {
        using var elsewhere = new Loopback("127.0.0.2", _ => Loopback.Ok);
        using var server = new Loopback("127.0.0.1", path => path == "/away" ? Loopback.Found($"http://127.0.0.2:{elsewhere.Port}/landed") : Loopback.Ok);
        using var http = new PlexClientIdentity("test-client", "0.0.0", "tests").CreateHttpClient();
        var client = new PlexServerClient(http, new Uri($"http://127.0.0.1:{server.Port}/"), "server-token", "Den");

        await client.GetAsync("/away", TestContext.Current.CancellationToken);

        Assert.Equal([("/away", (string?)"server-token")], server.Seen);
        Assert.Equal([("/landed", (string?)null)], elsewhere.Seen);
    }

    /// <summary>A one-connection-at-a-time HTTP responder on a loopback address, recording each request's path and token.</summary>
    private sealed class Loopback : IDisposable
    {
        public const string Ok = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 29\r\nConnection: close\r\n\r\n{\"MediaContainer\":{\"size\":0}}";

        private readonly System.Net.Sockets.TcpListener _listener;
        private readonly Func<string, string> _answer;

        public Loopback(string address, Func<string, string> answer)
        {
            _answer = answer;
            _listener = new System.Net.Sockets.TcpListener(IPAddress.Parse(address), 0);
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public List<(string Path, string? Token)> Seen { get; } = [];

        public static string Found(string location) => $"HTTP/1.1 302 Found\r\nLocation: {location}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";

        public void Dispose() => _listener.Stop();

        private async Task ServeAsync()
        {
            while (true)
            {
                System.Net.Sockets.TcpClient connection;
                try
                {
                    connection = await _listener.AcceptTcpClientAsync();
                }
                catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                using (connection)
                {
                    var stream = connection.GetStream();
                    var head = new System.Text.StringBuilder();
                    var buffer = new byte[4096];
                    while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal) && head.Length < 16384)
                    {
                        var read = await stream.ReadAsync(buffer);
                        if (read == 0) break;
                        head.Append(System.Text.Encoding.ASCII.GetString(buffer, 0, read));
                    }

                    var lines = head.ToString().Split("\r\n");
                    var path = lines[0].Split(' ') is [_, var target, ..] ? target : string.Empty;
                    var token = lines.FirstOrDefault(l => l.StartsWith("X-Plex-Token:", StringComparison.OrdinalIgnoreCase))?["X-Plex-Token:".Length..].Trim();
                    lock (Seen) Seen.Add((path, token));
                    await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(_answer(path)));
                }
            }
        }
    }
}

/// <summary>A <c>file://</c> address is read only as artwork a download kept, never as whatever a server names.</summary>
public sealed class LocalArtworkTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tuxflix-tests", "artwork-" + Guid.NewGuid().ToString("N")[..8]);

    public LocalArtworkTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Address(string name) => new Uri(Path.Combine(_folder, name)).AbsoluteUri;

    [Fact]
    public void ArtworkKeptBesideADownloadIsRead()
    {
        File.WriteAllBytes(Path.Combine(_folder, "poster.jpg"), [1, 2, 3]);

        Assert.Equal([1, 2, 3], Tuxflix.Core.Downloads.DownloadManager.ReadArtwork(Address("poster.jpg")));
    }

    [Fact]
    public void NothingElseOnDiskIsRead()
    {
        // The link's target path and the file it names are both nine bytes long, so the link reads
        // as a sensible size whichever of the two a length reports: only the link check refuses it.
        File.WriteAllBytes(Path.Combine(_folder, "notes.txt"), "123456789"u8.ToArray());
        File.CreateSymbolicLink(Path.Combine(_folder, "still.jpg"), "notes.txt");
        Assert.Equal(9, new FileInfo(Path.Combine(_folder, "still.jpg")).Length);
        using (var large = File.Create(Path.Combine(_folder, "backdrop.jpg"))) large.SetLength(Tuxflix.Core.Downloads.DownloadManager.MaxArtworkBytes + 1);

        Assert.Null(Tuxflix.Core.Downloads.DownloadManager.ReadArtwork(Address("notes.txt")));
        Assert.Null(Tuxflix.Core.Downloads.DownloadManager.ReadArtwork("file:///dev/zero"));
        Assert.Null(Tuxflix.Core.Downloads.DownloadManager.ReadArtwork(Address("still.jpg")));
        Assert.Null(Tuxflix.Core.Downloads.DownloadManager.ReadArtwork(Address("backdrop.jpg")));
        Assert.Null(Tuxflix.Core.Downloads.DownloadManager.ReadArtwork("https://example.com/poster.jpg"));
    }

    [Fact]
    public async Task APipeNamedLikeArtworkIsRefusedWithoutWaiting()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var pipe = Path.Combine(_folder, "poster.jpg");
        using (var mkfifo = System.Diagnostics.Process.Start("mkfifo", [pipe])) await mkfifo.WaitForExitAsync(cancellation);
        Assert.True(File.Exists(pipe));

        var read = Task.Run(() => Tuxflix.Core.Downloads.DownloadManager.ReadArtwork(Address("poster.jpg")), cancellation);

        Assert.Same(read, await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(5), cancellation)));
        Assert.Null(await read);
    }
}
