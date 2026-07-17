using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

/// <summary>
/// Launches a <see cref="LaunchPreset"/>: resolves each alias/username to a live account and
/// starts them into the preset's place/server, waiting the configured delay between clients.
/// The account resolver is injected at startup so this service stays free of the UI store.
/// </summary>
public static class PresetService
{
    private static Func<string, Account?>? _resolve;

    /// <summary>Wires the alias/username → account resolver. Call once at startup.</summary>
    public static void Init(Func<string, Account?> resolver) => _resolve = resolver;

    /// <summary>
    /// Launches every account in the preset. Returns (launched, failed) counts. Silently skips
    /// aliases that don't resolve to a known account.
    /// </summary>
    public static async Task<(int launched, int failed)> LaunchAsync(LaunchPreset preset)
    {
        if (_resolve == null) return (0, 0);

        int launched = 0, failed = 0;
        string? jobId = string.IsNullOrWhiteSpace(preset.JobId) ? null : preset.JobId;
        int delay = Math.Max(0, preset.JoinDelaySeconds);

        for (int i = 0; i < preset.Aliases.Count; i++)
        {
            var acc = _resolve(preset.Aliases[i]);
            if (acc == null) { failed++; continue; }

            try
            {
                var result = await LauncherService.LaunchAsync(acc, preset.PlaceId, jobId);
                if (result.Success) launched++; else failed++;
            }
            catch { failed++; }

            // Delay between (but not after) launches.
            if (i < preset.Aliases.Count - 1 && delay > 0)
                await Task.Delay(TimeSpan.FromSeconds(delay));
        }

        return (launched, failed);
    }

    /// <summary>Finds a preset by name (case-insensitive) in the current settings.</summary>
    public static LaunchPreset? Find(string name) =>
        SettingsService.Current.LaunchPresets
            .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}
