using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

/// <summary>
/// Server-selection power tools built on top of <see cref="RobloxApi.GetPublicServersAsync"/>:
/// pick the lowest-ping server (Ping-Join), a fresh random server (Server-Hop), or a single
/// server with enough free slots for a whole group (Squad). A server counts as "joinable" when
/// it exposes a usable Job ID and still has at least one open slot.
/// </summary>
public static class PowerToolsService
{
    /// <summary>Result of a server pick. On success <see cref="JobId"/> is set; on hard failure
    /// <see cref="Error"/> is set and JobId is null; <see cref="Warning"/> is a soft note that still
    /// allows the caller to proceed (e.g. no server fit the whole squad).</summary>
    public record ServerPick(string? JobId, GameServer? Server, string? Error = null, string? Warning = null);

    private static bool Joinable(GameServer s) =>
        !string.IsNullOrWhiteSpace(s.Id) && s.Playing < s.MaxPlayers;

    private static async Task<(List<GameServer> servers, string? error)> LoadJoinableAsync(long placeId)
    {
        if (placeId <= 0) return (new(), "Enter a valid Place ID first.");
        List<GameServer> servers;
        try { servers = await RobloxApi.GetPublicServersAsync(placeId); }
        catch (Exception ex) { return (new(), $"Couldn't load the server list: {ex.Message}"); }

        var joinable = servers.Where(Joinable).ToList();
        if (joinable.Count == 0)
            return (new(), "No joinable public servers found for that place right now.");
        return (joinable, null);
    }

    /// <summary>Lowest-ping joinable server. Roblox often omits ping for public servers, so when
    /// no server reports a ping we fall back to the emptiest joinable one (safest to enter).</summary>
    public static async Task<ServerPick> PickBestPingAsync(long placeId)
    {
        var (servers, err) = await LoadJoinableAsync(placeId);
        if (err != null) return new(null, null, err);

        var best = servers.Where(s => s.Ping > 0).OrderBy(s => s.Ping).FirstOrDefault()
                   ?? servers.OrderBy(s => s.Playing).First();   // no ping data -> emptiest
        return new(best.Id, best);
    }

    /// <summary>A random joinable server that isn't <paramref name="excludeJobId"/> (server hop).
    /// If only the current server exists we allow re-picking it rather than failing.</summary>
    public static async Task<ServerPick> PickHopAsync(long placeId, string? excludeJobId)
    {
        var (servers, err) = await LoadJoinableAsync(placeId);
        if (err != null) return new(null, null, err);

        var pool = servers
            .Where(s => !string.Equals(s.Id, excludeJobId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (pool.Count == 0) pool = servers;

        var pick = pool[Random.Shared.Next(pool.Count)];
        return new(pick.Id, pick);
    }

    /// <summary>A single server with room for the whole squad. Prefers the fullest server that
    /// still fits everyone (keeps the group together and lands in a "live" server); if none can
    /// hold the whole squad, returns the emptiest with a warning.</summary>
    public static async Task<ServerPick> PickSquadAsync(long placeId, int squadSize)
    {
        var (servers, err) = await LoadJoinableAsync(placeId);
        if (err != null) return new(null, null, err);
        if (squadSize < 1) squadSize = 1;

        var fits = servers
            .Where(s => s.MaxPlayers - s.Playing >= squadSize)
            .OrderByDescending(s => s.Playing)   // fullest-that-fits
            .FirstOrDefault();
        if (fits != null) return new(fits.Id, fits);

        var emptiest = servers.OrderBy(s => s.Playing).First();
        int room = emptiest.MaxPlayers - emptiest.Playing;
        return new(emptiest.Id, emptiest, Warning:
            $"No single server has room for all {squadSize} — using the emptiest ({room} free slot(s)); some may not make it in.");
    }
}
