namespace Tuxflix.Core.Security;

/// <summary>Somewhere to keep a secret between runs: the desktop keyring, or a stand-in under test.</summary>
public interface ISecretStore
{
    Task<string?> LookupAsync(string account);

    Task<bool> StoreAsync(string account, string label, string secret);

    Task ClearAsync(string account);
}
