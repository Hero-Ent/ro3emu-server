using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Server.Accounts;

/// <summary>
/// Account bans (docs/systems/account-bans.md). Every way a credential becomes an account goes through this store:
/// SignInAsync (password), VerifiedAccountAsync (password, without registration), the SDK ticket and the engine sign
/// (ResolveToken), and UnlessBannedAsync for credentials held elsewhere (the gate auth of PhonestGate.bind). Each one
/// reads account_ban fresh, since the admin panel writes it from another process, and answers a banned account as it
/// answers a wrong credential (inferred: the client has no ban message we know). The log says "banned".
/// </summary>
public sealed partial class AccountStore
{
    /// <summary>True when <paramref name="account"/> (its stored name, matched exactly) has an active ban now.</summary>
    public Task<bool> IsBannedAsync(string account) =>
        ReadAsync<bool>(new AccountBanRead(account, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    private bool IsBanned(string account) =>
        ReadSync<bool>(new AccountBanRead(account, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    /// <summary>
    /// <paramref name="account"/>, or null when it is null or banned; <paramref name="path"/> names the credential in
    /// the log line of a refusal.
    /// </summary>
    internal async Task<string?> UnlessBannedAsync(string? account, string path)
    {
        if (account == null || !await IsBannedAsync(account).ConfigureAwait(false)) return account;
        Refused(account, path);
        return null;
    }

    private string? UnlessBanned(string? account, string path)
    {
        if (account == null || !IsBanned(account)) return account;
        Refused(account, path);
        return null;
    }

    private void Refused(string account, string path)
    {
        if (_log != null) LogBanned(_log, account, path);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Account {Account} is banned: {Path} refused")]
    private static partial void LogBanned(ILogger logger, string account, string path);
}
