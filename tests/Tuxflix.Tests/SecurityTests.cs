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
