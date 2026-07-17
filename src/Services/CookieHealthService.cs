using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

/// <summary>
/// Validates each account's <c>.ROBLOSECURITY</c> cookie against Roblox and flags dead
/// ones via <see cref="Account.IsValid"/>. Runs off the UI thread; the IsValid setter
/// raises PropertyChanged, which WPF marshals back to bindings automatically.
/// </summary>
public static class CookieHealthService
{
    /// <summary>
    /// Checks every account that has a non-empty cookie. Bounded concurrency keeps us well
    /// under Roblox's rate limits. A network hiccup leaves the previous state untouched
    /// (we never flag an account invalid just because a request failed). Returns the number
    /// of accounts confirmed invalid.
    /// </summary>
    public static async Task<int> ValidateAllAsync(IEnumerable<Account> accounts, IProgress<string>? progress = null)
    {
        var list = accounts.Where(a => !string.IsNullOrWhiteSpace(a.Cookie)).ToList();
        if (list.Count == 0) return 0;

        int invalid = 0, done = 0;
        using var gate = new SemaphoreSlim(4);

        var tasks = list.Select(async acc =>
        {
            await gate.WaitAsync();
            try
            {
                var id = await RobloxApi.GetAuthenticatedUserAsync(acc.Cookie);
                bool ok = id != null;
                acc.IsValid = ok;
                if (!ok) Interlocked.Increment(ref invalid);

                // Backfill identity fields when we learn them and they were blank.
                if (ok)
                {
                    if (acc.UserId == 0) acc.UserId = id!.Id;
                    if (string.IsNullOrWhiteSpace(acc.Username)) acc.Username = id!.Name;
                }
            }
            catch { /* transient network failure — keep prior IsValid */ }
            finally
            {
                progress?.Report($"Validated {Interlocked.Increment(ref done)}/{list.Count}…");
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        return invalid;
    }
}
