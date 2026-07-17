using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

/// <summary>
/// Fleet-management helpers for large account collections: duplicate detection and
/// safe export. All logic is pure so it stays easy to reason about and test.
/// </summary>
public static class ScaleService
{
    /// <summary>Groups of accounts that share the same Roblox UserId (true duplicates).</summary>
    public static List<List<Account>> FindDuplicates(IEnumerable<Account> accounts) =>
        accounts.Where(a => a.UserId > 0)
                .GroupBy(a => a.UserId)
                .Where(g => g.Count() > 1)
                .Select(g => g.ToList())
                .ToList();

    /// <summary>CSV export — never includes cookies, so it is safe to share.</summary>
    public static string ToCsv(IEnumerable<Account> accounts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Username,Alias,DisplayName,UserId,Group,Color,Presence,Robux,Rap,Premium");
        foreach (var a in accounts)
            sb.AppendLine(string.Join(",",
                Csv(a.Username), Csv(a.Alias), Csv(a.DisplayName),
                a.UserId.ToString(CultureInfo.InvariantCulture),
                Csv(a.Group), Csv(a.Color), Csv(a.Presence),
                a.Robux.ToString(CultureInfo.InvariantCulture),
                a.Rap.ToString(CultureInfo.InvariantCulture),
                a.IsPremium ? "yes" : "no"));
        return sb.ToString();
    }

    /// <summary>JSON export; cookies are written only when <paramref name="includeCookies"/> is set.</summary>
    public static string ToJson(IEnumerable<Account> accounts, bool includeCookies)
    {
        var rows = accounts.Select(a => new
        {
            a.Username, a.Alias, a.DisplayName, a.UserId, a.Group, a.Color,
            a.Presence, a.Robux, a.Rap, a.IsPremium,
            Cookie = includeCookies ? a.Cookie : null
        });
        return JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string Csv(string? v)
    {
        v ??= "";
        if (v.Contains(',') || v.Contains('"') || v.Contains('\n'))
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        return v;
    }
}
