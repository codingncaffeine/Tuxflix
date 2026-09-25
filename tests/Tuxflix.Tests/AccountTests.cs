using System.Net;
using System.Text;
using Tuxflix.Core.Plex;
using Tuxflix.Core.Security;
using Xunit;

namespace Tuxflix.Tests;

/// <summary>Answers requests from a table of URL to reply, as a stand-in network.</summary>
internal sealed class FakeNetwork(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
{
    public List<string> Asked { get; } = [];

    /// <summary>Every address asked, with the token its request carried (null for none).</summary>
    public List<(string Uri, string? Token)> Sent { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = request.Headers.TryGetValues("X-Plex-Token", out var values) ? string.Join(",", values) : null;
        lock (Asked)
        {
            Asked.Add(request.RequestUri!.ToString());
            Sent.Add((request.RequestUri!.ToString(), token));
        }

        return answer(request);
    }

    public static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static string Identity(string machine) =>
        $$$"""{"MediaContainer":{"size":0,"machineIdentifier":"{{{machine}}}","version":"1.42.1"}}""";
}

public sealed class AccountTests
{
    private static HttpClient Client(FakeNetwork network) =>
        new PlexClientIdentity("test-client", "0.0.0", "tests").CreateHttpClient(network);

    [Fact]
    public async Task ThePinFlowReadsPlexTvsReplies()
    {
        var network = new FakeNetwork(request => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/api/v2/pins" => FakeNetwork.Json("""{"id":888596567,"code":"abcdefghijklmnopqrstuvwxy","expiresIn":1800,"authToken":null,"qr":"https://plex.tv/api/v2/pins/qr/x"}"""),
            "/api/v2/pins/888596567" => FakeNetwork.Json("""{"id":888596567,"code":"abcdefghijklmnopqrstuvwxy","authToken":"account-token"}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        }));
        var account = new PlexAccountClient(Client(network));

        var pin = await account.CreatePinAsync(TestContext.Current.CancellationToken);
        var signIn = PlexAccountClient.SignInPage("test-client", pin).ToString();
        var checkedPin = await account.CheckPinAsync(pin.Id, TestContext.Current.CancellationToken);

        Assert.Equal(1800, pin.ExpiresIn);
        Assert.Null(pin.AuthToken);
        Assert.StartsWith("https://app.plex.tv/auth#?clientID=test-client&code=abcdefghijklmnopqrstuvwxy", signIn, StringComparison.Ordinal);
        Assert.Contains("strong=true", network.Asked[0], StringComparison.Ordinal);
        Assert.Equal("account-token", checkedPin!.AuthToken);
    }

    [Fact]
    public async Task ResourcesKeepOnlyServersOwnedFirst()
    {
        const string resources = """
            [
              {"name":"Phone","provides":"client,player","clientIdentifier":"p1","owned":true,"connections":[]},
              {"name":"Friend's","provides":"server","clientIdentifier":"s2","owned":false,"sourceTitle":"Robin","accessToken":"t2","connections":[]},
              {"name":"Den","product":"Plex Media Server","productVersion":"1.42.1.10060-4e8b05daf","platform":"Linux","provides":"server","clientIdentifier":"s1","owned":true,"accessToken":"t1","publicAddressMatches":true,
               "connections":[{"protocol":"https","address":"192.168.1.20","port":32400,"uri":"https://192-168-1-20.abc.plex.direct:32400","local":true,"relay":false,"IPv6":false}]}
            ]
            """;
        var network = new FakeNetwork(_ => Task.FromResult(FakeNetwork.Json(resources)));
        var account = new PlexAccountClient(Client(network));

        var servers = await account.GetServersAsync("account-token", TestContext.Current.CancellationToken);

        Assert.Equal(["Den", "Friend's"], servers.Select(s => s.Name));
        Assert.Equal("t1", servers[0].AccessToken);
        Assert.Equal("Robin", servers[1].SourceTitle);
        Assert.Single(servers[0].Connections!);
        Assert.True(servers[0].PublicAddressMatches);
        Assert.False(servers[1].PublicAddressMatches);
    }

    [Fact]
    public async Task ARejectedAccountTokenIsReportedAsSuch()
    {
        var network = new FakeNetwork(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var account = new PlexAccountClient(Client(network));
        await Assert.ThrowsAsync<PlexUnauthorizedException>(() => account.GetUserAsync("stale", TestContext.Current.CancellationToken));
    }
}

public sealed class ConnectionPickerTests
{
    private static PlexResource Server(params PlexConnection[] connections) => new()
    {
        Name = "Den",
        ClientIdentifier = "machine-1",
        Provides = "server",
        AccessToken = "server-token",
        Connections = [.. connections],
    };

    /// <summary>A server plex.tv sees at this client's own public address.</summary>
    private static PlexResource OnItsNetwork(params PlexConnection[] connections) => new()
    {
        Name = "Den",
        ClientIdentifier = "machine-1",
        Provides = "server",
        AccessToken = "server-token",
        PublicAddressMatches = true,
        Connections = [.. connections],
    };

    private static HttpClient Client(FakeNetwork network) =>
        new PlexClientIdentity("test-client", "0.0.0", "tests").CreateHttpClient(network);

    [Fact]
    public async Task ALocalAnswerWinsOverRemoteAndRelay()
    {
        var server = Server(
            new PlexConnection { Uri = "https://relay.plex.direct:8443", Relay = true },
            new PlexConnection { Uri = "https://1-2-3-4.abc.plex.direct:32400", Address = "1.2.3.4", Port = 32400 },
            new PlexConnection { Uri = "https://192-168-1-20.abc.plex.direct:32400", Address = "192.168.1.20", Port = 32400, Local = true });

        var network = new FakeNetwork(async request =>
        {
            // The local one is slowest, and still must win.
            if (request.RequestUri!.Host.StartsWith("192-168", StringComparison.Ordinal)) await Task.Delay(150);
            return FakeNetwork.Json(FakeNetwork.Identity("machine-1"));
        });

        var picked = await ConnectionPicker.PickAsync(Client(network), server, TestContext.Current.CancellationToken);

        Assert.NotNull(picked);
        Assert.Equal(ConnectionKind.Local, picked.Kind);
        Assert.StartsWith("https://192-168-1-20", picked.Uri.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMachineThatIsNotTheServerIsNeverTakenForIt()
    {
        var server = Server(
            new PlexConnection { Uri = "https://192-168-1-20.abc.plex.direct:32400", Address = "192.168.1.20", Port = 32400, Local = true },
            new PlexConnection { Uri = "https://1-2-3-4.abc.plex.direct:32400", Address = "1.2.3.4", Port = 32400 });

        var network = new FakeNetwork(request => Task.FromResult(FakeNetwork.Json(FakeNetwork.Identity(
            request.RequestUri!.Host.StartsWith("192", StringComparison.Ordinal) ? "someone-else" : "machine-1"))));

        var picked = await ConnectionPicker.PickAsync(Client(network), server, TestContext.Current.CancellationToken);

        Assert.NotNull(picked);
        Assert.Equal(ConnectionKind.Remote, picked.Kind);
    }

    [Fact]
    public async Task APlexDirectNameTheRouterWillNotResolveFallsBackToPlainHttpLocally()
    {
        var server = OnItsNetwork(new PlexConnection { Uri = "https://192-168-1-20.abc.plex.direct:32400", Protocol = "https", Address = "192.168.1.20", Port = 32400, Local = true });

        var network = new FakeNetwork(request => request.RequestUri!.Scheme == "https"
            ? throw new HttpRequestException("Name or service not known")
            : Task.FromResult(FakeNetwork.Json(FakeNetwork.Identity("machine-1"))));

        var picked = await ConnectionPicker.PickAsync(Client(network), server, TestContext.Current.CancellationToken);

        Assert.NotNull(picked);
        Assert.Equal("http://192.168.1.20:32400/", picked.Uri.ToString());
        Assert.DoesNotContain(network.Asked, asked => asked.Contains("server-token", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoAddressIsHandedTheTokenBeforeItAnswersAsTheServer()
    {
        // A stranger holds the local address; the real server answers remotely. Neither probe,
        // plain or secure, may carry the server's token.
        var server = OnItsNetwork(
            new PlexConnection { Uri = "https://192-168-1-20.abc.plex.direct:32400", Protocol = "https", Address = "192.168.1.20", Port = 32400, Local = true },
            new PlexConnection { Uri = "https://1-2-3-4.abc.plex.direct:32400", Protocol = "https", Address = "1.2.3.4", Port = 32400 });

        var network = new FakeNetwork(request => Task.FromResult(FakeNetwork.Json(FakeNetwork.Identity(
            request.RequestUri!.Host.StartsWith("1-2-3-4", StringComparison.Ordinal) ? "machine-1" : "someone-else"))));

        var picked = await ConnectionPicker.PickAsync(Client(network), server, TestContext.Current.CancellationToken);

        Assert.NotNull(picked);
        Assert.Equal(ConnectionKind.Remote, picked.Kind);
        Assert.Equal(3, network.Sent.Count);
        Assert.All(network.Sent, sent => Assert.Null(sent.Token));
    }

    [Fact]
    public void PlainHttpIsNeverTriedOffTheServersNetwork()
    {
        // plex.tv sees this client somewhere else (a café, a hotel): the local address may be anyone's.
        var server = Server(
            new PlexConnection { Uri = "https://192-168-1-20.abc.plex.direct:32400", Protocol = "https", Address = "192.168.1.20", Port = 32400, Local = true },
            new PlexConnection { Uri = "http://192.168.1.20:32400", Protocol = "http", Address = "192.168.1.20", Port = 32400, Local = true });

        var candidates = ConnectionPicker.Candidates(server).ToList();

        Assert.Equal(["https://192-168-1-20.abc.plex.direct:32400/"], candidates.Select(c => c.Uri.ToString()));
    }

    [Fact]
    public void ARemoteOrRelayAddressMustBeSecure()
    {
        var server = OnItsNetwork(
            new PlexConnection { Uri = "http://1.2.3.4:32400", Protocol = "http", Address = "1.2.3.4", Port = 32400 },
            new PlexConnection { Uri = "http://5.6.7.8:8443", Protocol = "http", Address = "5.6.7.8", Port = 8443, Relay = true },
            new PlexConnection { Uri = "https://1-2-3-4.abc.plex.direct:32400", Protocol = "https", Address = "1.2.3.4", Port = 32400 },
            new PlexConnection { Uri = "http://192.168.1.20:32400", Protocol = "http", Address = "192.168.1.20", Port = 32400, Local = true });

        var candidates = ConnectionPicker.Candidates(server).Select(c => c.Uri.ToString()).ToList();

        Assert.Equal(["https://1-2-3-4.abc.plex.direct:32400/", "http://192.168.1.20:32400/"], candidates);
    }

    [Fact]
    public void AServerThatRequiresSecureConnectionsGetsNoPlainFallback()
    {
        // On the server's own network, where plain http would otherwise be tried.
        var server = new PlexResource
        {
            ClientIdentifier = "machine-1",
            HttpsRequired = true,
            PublicAddressMatches = true,
            Connections = [new PlexConnection { Uri = "https://192-168-1-20.abc.plex.direct:32400", Address = "192.168.1.20", Port = 32400, Local = true }],
        };

        Assert.Single(ConnectionPicker.Candidates(server));
    }

    [Fact]
    public async Task NothingAnsweringMeansNoConnection()
    {
        var server = Server(new PlexConnection { Uri = "https://1-2-3-4.abc.plex.direct:32400", Address = "1.2.3.4", Port = 32400 });
        var network = new FakeNetwork(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        Assert.Null(await ConnectionPicker.PickAsync(Client(network), server, TestContext.Current.CancellationToken));
    }
}

public sealed class ReadOnlySecretStoreTests
{
    private sealed class MemorySecrets : ISecretStore
    {
        public Dictionary<string, string> Secrets { get; } = [];

        public Task<string?> LookupAsync(string account) => Task.FromResult(Secrets.GetValueOrDefault(account));

        public Task<bool> StoreAsync(string account, string label, string secret)
        {
            Secrets[account] = secret;
            return Task.FromResult(true);
        }

        public Task ClearAsync(string account)
        {
            Secrets.Remove(account);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ReadsTheSecretAndNeverChangesIt()
    {
        var real = new MemorySecrets();
        real.Secrets["plex-client"] = "the-real-token";
        var store = new ReadOnlySecretStore(real);

        Assert.Equal("the-real-token", await store.LookupAsync("plex-client"));
        Assert.False(await store.StoreAsync("plex-client", "label", "another-token"));
        await store.ClearAsync("plex-client");
        Assert.False(await store.StoreAsync("plex-other", "label", "a-token"));

        Assert.Equal(new Dictionary<string, string> { ["plex-client"] = "the-real-token" }, real.Secrets);
    }
}
