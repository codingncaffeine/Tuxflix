namespace Tuxflix.Core.Security;

/// <summary>
/// Reads another store's secrets and never changes them: for tool runs that borrow the real
/// profile's sign-in, which must not store over it, nor delete it when plex.tv turns it down.
/// </summary>
public sealed class ReadOnlySecretStore(ISecretStore inner) : ISecretStore
{
    public Task<string?> LookupAsync(string account) => inner.LookupAsync(account);

    /// <summary>Keeps nothing, and says so: the caller carries on with a sign-in for this run only.</summary>
    public Task<bool> StoreAsync(string account, string label, string secret) => Task.FromResult(false);

    public Task ClearAsync(string account) => Task.CompletedTask;
}
