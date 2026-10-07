using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

/// <summary>
/// Launches a <see cref="LaunchPreset"/>: resolves each alias/username to a live account and the
/// preset's destination to a <see cref="JoinTarget"/>, then starts the accounts through
/// <see cref="LauncherService.LaunchBatchAsync"/>. The account resolver is injected at startup so this
/// service stays free of the UI store.
/// </summary>
public static class PresetService
{
    private static Func<string, Account?>? _resolve;

    /// <summary>Wires the alias/username → account resolver. Call once at startup.</summary>
    public static void Init(Func<string, Account?> resolver) => _resolve = resolver;

    /// <summary>Outcome of a preset run. <see cref="Error"/> is set when nothing could be launched at all.</summary>
    public sealed record RunResult(int Launched, int Failed, IReadOnlyList<string> Errors, string? Error = null, int NotStarted = 0);

    /// <summary>
    /// Launches every account in the preset. Aliases that don't resolve to a known account count as
    /// failed. The destination is resolved once, before the first launch.
    /// </summary>
    /// <param name="origin">
    /// Tag for the clients this run starts (see <see cref="ProcessRegistry.Tracked.Origin"/>): a schedule
    /// uses it to close exactly its own clients later, including ones the watchdog relaunched.
    /// </param>
    public static async Task<RunResult> LaunchAsync(LaunchPreset preset,
        Action<Account, int>? onLaunching = null, Action<int>? onWaiting = null, CancellationToken ct = default,
        string? origin = null)
    {
        if (_resolve == null) return new(0, 0, Array.Empty<string>());

        var accounts = new List<Account>();
        var errors = new List<string>();
        foreach (string alias in preset.Aliases)
        {
            var acc = _resolve(alias);
            if (acc != null) accounts.Add(acc);
            else errors.Add(L.T("Automation.Preset.MissingAccount", alias));
        }
        if (accounts.Count == 0)
            return new(0, errors.Count, errors, L.T("Automation.Preset.NoAccounts"));

        // A share link needs a signed-in session to resolve; any valid account of the preset will do.
        var target = await JoinTargetResolver.ForPresetAsync(preset,
            () => accounts.FirstOrDefault(a => a.IsValid)?.Cookie ?? accounts[0].Cookie);
        if (target.Target == null)
        {
            DiagnosticsService.Warn("preset", $"Preset '{preset.Name}' has no usable destination");
            return new(0, accounts.Count + errors.Count, errors, target.Error);
        }

        var batch = await LauncherService.LaunchBatchAsync(accounts, target.Target,
            new LauncherService.BatchOptions(preset.JoinDelaySeconds, preset.RandomDelaySeconds, preset.PerformanceProfile, origin),
            onLaunching, onWaiting, ct);

        errors.AddRange(batch.Errors);
        return new(batch.Launched, batch.Failed + (preset.Aliases.Count - accounts.Count), errors, NotStarted: batch.NotStarted);
    }

    /// <summary>Finds a preset by name (case-insensitive) in the current settings.</summary>
    public static LaunchPreset? Find(string name) =>
        SettingsService.Current.LaunchPresets
            .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}
