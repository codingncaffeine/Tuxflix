using System.Net;
using System.Text;
using Tuxflix.App.ViewModels;
using Tuxflix.Core;
using Tuxflix.Core.Security;
using Tuxflix.Core.Settings;
using Xunit;

namespace Tuxflix.HeadlessTests;

/// <summary>
/// The whole sign-in, from the page to the library, against a stand-in plex.tv and server, with
/// no browser opened and nothing written to the real keyring.
/// </summary>
public sealed class SignInFlowTests
{
    private sealed class MemorySecrets : ISecretStore
    {
        public Dictionary<string, string> Kept { get; } = [];

        public Task<string?> LookupAsync(string account) => Task.FromResult(Kept.GetValueOrDefault(account));

        public Task<bool> StoreAsync(string account, string label, string secret)
        {
            Kept[account] = secret;
            return Task.FromResult(true);
        }

        public Task ClearAsync(string account)
        {
            Kept.Remove(account);
            return Task.CompletedTask;
        }
    }

    private sealed class StandIn : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "clients.plex.tv")
            {
                return uri.AbsolutePath switch
                {
                    "/api/v2/pins" => Json("""{"id":1,"code":"code","expiresIn":60}"""),
                    "/api/v2/pins/1" => Json("""{"id":1,"code":"code","authToken":"account-token"}"""),
                    "/api/v2/user" => Json("""{"id":7,"username":"viewer","title":"Viewer"}"""),
                    "/api/v2/resources" => Json("""
                        [{"name":"Den","provides":"server","clientIdentifier":"machine-1","owned":true,"accessToken":"server-token",
                          "connections":[{"protocol":"https","address":"10.0.0.2","port":32400,"uri":"https://10-0-0-2.abc.plex.direct:32400","local":true,"relay":false}]}]
                        """),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                };
            }

            // The server answers after a moment, as a real one on the network does: long enough that
            // a probe cancelled by the navigation to the Connecting page would have died first.
            await Task.Delay(40, cancellationToken);
            return uri.AbsolutePath switch
            {
                "/identity" => Json("""{"MediaContainer":{"machineIdentifier":"machine-1"}}"""),
                _ => Json("""{"MediaContainer":{"size":0}}"""),
            };
        }

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    [Fact]
    public async Task SigningInOpensTheOnlyServerRatherThanCancellingItsOwnConnection()
    {
        var root = Path.Combine(Path.GetTempPath(), "tuxflix-headless", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = AppPaths.Resolve(root, Environment.GetEnvironmentVariable);
            paths.EnsureCreated();
            var secrets = new MemorySecrets();
            var browserOpened = 0;
            var shell = new ShellViewModel(SettingsStore.Load(paths.SettingsFile), paths, new StandIn())
            {
                Keyring = secrets,
                OpenUrl = _ => browserOpened++,
            };

            shell.Router.Navigate(new SignInPageViewModel(shell));

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (shell.Session is null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            // Where sign-in stood at the deadline, so a failure (seen once in about 75 runs) says what happened.
            Assert.True(shell.Session is not null, $"no session after 15 s; the page is {shell.Router.Current?.GetType().Name} \"{shell.Router.Current?.Title}\", error \"{(shell.Router.Current as PageViewModel)?.ErrorMessage}\", browser opened {browserOpened} times");
            Assert.Equal(1, browserOpened);
            Assert.Equal("Den", shell.Session.Name);
            Assert.IsType<HomePageViewModel>(shell.Router.Current);
            Assert.Equal("account-token", Assert.Single(secrets.Kept).Value);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // A test folder under the temporary directory; nothing else lives in it.
            }
        }
    }
}
