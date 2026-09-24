using System.Diagnostics;
using Tuxflix.Core.Diagnostics;

namespace Tuxflix.Core.Security;

/// <summary>
/// The desktop keyring (GNOME Keyring, KWallet, KeePassXC: anything speaking the Secret Service),
/// through <c>secret-tool</c> from libsecret.
/// </summary>
/// <remarks>
/// Deliberately no file fallback, as in Mailbox: a Plex token in a file in a config folder is a
/// token anybody who can read that folder can use. Without a keyring the sign-in holds for the
/// session only, and the interface says so. Every call is bounded, because a keyring that is
/// locked or gone mid-session leaves <c>secret-tool</c> waiting for a prompt that never comes.
/// </remarks>
public sealed class Keyring
{
    private const string Tool = "secret-tool";
    private const string Service = "tuxflix";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    /// <summary>Whether a keyring can be reached at all on this machine.</summary>
    public bool IsAvailable { get; private set; } = true;

    public async Task<string?> LookupAsync(string account)
    {
        var (ok, output, _) = await RunAsync(["lookup", "service", Service, "account", account], input: null).ConfigureAwait(false);

        // secret-tool exits non-zero when nothing matches; that is an empty answer, not a failure.
        return ok && output.Length > 0 ? output : null;
    }

    public async Task<bool> StoreAsync(string account, string label, string secret)
    {
        var (ok, _, error) = await RunAsync(["store", "--label", label, "service", Service, "account", account], secret).ConfigureAwait(false);
        if (!ok) Log.Warn($"The keyring would not keep the sign-in: {error}");
        return ok;
    }

    public async Task ClearAsync(string account)
    {
        var (ok, _, error) = await RunAsync(["clear", "service", Service, "account", account], input: null).ConfigureAwait(false);
        if (!ok && error.Length > 0) Log.Warn($"The keyring would not forget the sign-in: {error}");
    }

    private async Task<(bool Ok, string Output, string Error)> RunAsync(IReadOnlyList<string> arguments, string? input)
    {
        var start = new ProcessStartInfo(Tool)
        {
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            IsAvailable = false;
            Log.Warn("secret-tool is not installed (libsecret); sign-ins will not be remembered.");
            return (false, string.Empty, "secret-tool is not installed");
        }

        if (process is null) return (false, string.Empty, "secret-tool would not start");
        using (process)
        {
            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input).ConfigureAwait(false);
                process.StandardInput.Close();
            }

            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var limit = new CancellationTokenSource(Limit);
            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                IsAvailable = false;
                Log.Warn("The keyring did not answer within ten seconds; is it locked or not running?");
                return (false, string.Empty, "the keyring did not answer");
            }

            return (process.ExitCode == 0, (await output.ConfigureAwait(false)).Trim(), (await error.ConfigureAwait(false)).Trim());
        }
    }
}
