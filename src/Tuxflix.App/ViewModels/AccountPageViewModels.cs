using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tuxflix.Core.Diagnostics;
using Tuxflix.Core.Plex;

namespace Tuxflix.App.ViewModels;

/// <summary>
/// Signing in with Plex: a PIN is created, Plex's own sign-in page opens in the browser, and the
/// PIN is polled until plex.tv attaches a token. The password never passes through Tuxflix.
/// </summary>
public sealed partial class SignInPageViewModel(ShellViewModel shell) : PageViewModel
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    public override string Title => "Sign in";

    public override bool ShowsRail => false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWaiting), nameof(IsFinishing))]
    public partial SignInStage Stage { get; private set; } = SignInStage.Starting;

    public bool IsWaiting => Stage == SignInStage.Waiting;

    public bool IsFinishing => Stage == SignInStage.Finishing;

    /// <summary>Plex's sign-in page for this attempt, shown so it can be copied to another browser.</summary>
    [ObservableProperty]
    public partial string? SignInLink { get; private set; }

    /// <summary>
    /// Sign in from another device instead: a short code to type at plex.tv/link on a phone or a
    /// computer, as Plex's own TV apps do. Nobody types a password with a remote.
    /// </summary>
    public bool UseLinkCode { get; init; }

    /// <summary>The code for plex.tv/link, spaced for reading across a room; null until plex.tv gives one.</summary>
    [ObservableProperty]
    public partial string? LinkCode { get; private set; }

    protected override async Task LoadAsync(CancellationToken cancellation)
    {
        Stage = SignInStage.Starting;
        var pin = await shell.Account.CreatePinAsync(strong: !UseLinkCode, cancellation);
        if (UseLinkCode)
        {
            LinkCode = string.Join(' ', pin.Code.ToUpperInvariant().ToCharArray());
        }
        else
        {
            SignInLink = PlexAccountClient.SignInPage(shell.Identity.ClientIdentifier, pin).ToString();
            OpenBrowser();
        }

        Stage = SignInStage.Waiting;

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(pin.ExpiresIn ?? 1800);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(PollInterval, cancellation);
            var checkedPin = await shell.Account.CheckPinAsync(pin.Id, cancellation);
            if (checkedPin?.AuthToken is not { Length: > 0 } token) continue;

            Stage = SignInStage.Finishing;
            await shell.CompleteSignInAsync(token, remember: true, cancellation);
            return;
        }

        ErrorMessage = UseLinkCode
            ? "The code expired before it was entered. Try again for a new one."
            : "The sign-in link expired before it was used. Try again for a new one.";
    }

    [RelayCommand]
    private void OpenBrowser()
    {
        if (SignInLink is not null) shell.OpenUrl(SignInLink);
    }

    [RelayCommand]
    private void TryAgain() => _ = ActivateAsync();

    [RelayCommand]
    private void Cancel() => shell.Router.Reset(new WelcomePageViewModel(shell));
}

public enum SignInStage
{
    Starting,
    Waiting,
    Finishing,
}

/// <summary>Every server the account can reach, with how each one answers, to choose from.</summary>
public sealed partial class ServersPageViewModel : PageViewModel
{
    private readonly ShellViewModel _shell;

    public ServersPageViewModel(ShellViewModel shell, IReadOnlyList<PlexResource> servers, string? message)
    {
        _shell = shell;
        Message = message;
        Servers = new ObservableCollection<ServerRowViewModel>(servers.Select(s => new ServerRowViewModel(shell, s)));
    }

    public override string Title => "Servers";

    public override bool ShowsRail => false;

    public ObservableCollection<ServerRowViewModel> Servers { get; }

    public string? Message { get; }

    public bool HasMessage => Message is not null;

    protected override async Task LoadAsync(CancellationToken cancellation)
    {
        // Ask every server at once; each row fills in its own answer as it arrives.
        using var http = _shell.CreateHttpClient();
        await Task.WhenAll(Servers.Select(row => row.ProbeAsync(http, cancellation)));
    }
}

public sealed partial class ServerRowViewModel(ShellViewModel shell, PlexResource server) : ObservableObject
{
    public PlexResource Server { get; } = server;

    public string Name => Server.Name;

    public string Detail => string.Join("  ·  ", new[]
    {
        Server.Owned ? "Yours" : Server.SourceTitle is { Length: > 0 } owner ? $"Shared by {owner}" : "Shared with you",
        Server.ProductVersion is { Length: > 0 } version ? $"Plex Media Server {version.Split('-')[0]}" : null,
        Server.Platform,
    }.Where(part => !string.IsNullOrEmpty(part)));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnline), nameof(IsOffline), nameof(IsChecking))]
    public partial string Status { get; private set; } = "Checking…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnline), nameof(IsOffline), nameof(IsChecking))]
    public partial bool? Reachable { get; private set; }

    public bool IsChecking => Reachable is null;

    public bool IsOnline => Reachable == true;

    public bool IsOffline => Reachable == false;

    internal async Task ProbeAsync(HttpClient http, CancellationToken cancellation)
    {
        var connection = await ConnectionPicker.PickAsync(http, Server, cancellation);
        Reachable = connection is not null;
        Status = connection is null ? "Unreachable" : $"{connection.Describe()}  ·  {connection.Latency.TotalMilliseconds:0} ms";
    }

    [RelayCommand]
    private void Connect() => _ = ConnectSafelyAsync();

    private async Task ConnectSafelyAsync()
    {
        try
        {
            await shell.ConnectAsync(Server);
        }
        catch (Exception ex)
        {
            Log.Warn($"Connecting to {Server.Name} failed.", ex);
        }
    }
}

/// <summary>A short wait with something to say: signing in, connecting.</summary>
public sealed class StatusPageViewModel(string heading, string message) : PageViewModel
{
    public override string Title => heading;

    public override bool ShowsRail => false;

    public string Heading => heading;

    public string Message => message;
}
