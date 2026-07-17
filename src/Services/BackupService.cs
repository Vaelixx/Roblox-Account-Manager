using System.IO;
using System.Text;
using System.Text.Json;
using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

/// <summary>
/// Portable, password-encrypted account export/import.
///
/// The normal store encrypts cookies with DPAPI, which is bound to the current
/// Windows user — copying accounts.dat to another PC loses everything. A backup
/// instead serialises the raw cookies and re-encrypts the whole blob with a
/// user-chosen password (AES-256-GCM), so it can be restored anywhere.
///
/// File layout: magic "RAMBK1\n" + Crypto.EncryptPassword(json).
/// </summary>
public static class BackupService
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("RAMBK1\n");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private class Entry
    {
        public string Cookie { get; set; } = "";
        public string Username { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Alias { get; set; } = "";
        public string Group { get; set; } = "Default";
        public string Description { get; set; } = "";
        public string TotpSecret { get; set; } = "";
        public string BrowserTrackerId { get; set; } = "";
    }

    public static void Export(IEnumerable<Account> accounts, string path, string password)
    {
        var entries = accounts.Select(a => new Entry
        {
            Cookie = a.Cookie,
            Username = a.Username,
            DisplayName = a.DisplayName,
            Alias = a.Alias,
            Group = a.Group,
            Description = a.Description,
            TotpSecret = a.TotpSecret,
            BrowserTrackerId = a.BrowserTrackerId
        }).ToList();

        byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entries, JsonOpts));
        byte[] enc = Crypto.EncryptPassword(json, password);

        using var fs = File.Create(path);
        fs.Write(Magic);
        fs.Write(enc);
    }

    /// <summary>
    /// Decrypts a backup and returns its accounts (cookies in the clear, in memory only).
    /// Throws on wrong password / not a backup file.
    /// </summary>
    public static List<Account> Import(string path, string password)
    {
        byte[] all = File.ReadAllBytes(path);
        if (all.Length < Magic.Length || !all.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("Not a Roblox Account Manager backup file.");

        byte[] enc = all[Magic.Length..];
        byte[] plain = Crypto.Decrypt(enc, password);   // throws on wrong password / tamper
        var entries = JsonSerializer.Deserialize<List<Entry>>(Encoding.UTF8.GetString(plain)) ?? new();

        return entries.Select(e => new Account
        {
            Cookie = e.Cookie,
            Username = e.Username,
            DisplayName = e.DisplayName,
            Alias = e.Alias,
            Group = string.IsNullOrWhiteSpace(e.Group) ? "Default" : e.Group,
            Description = e.Description,
            TotpSecret = e.TotpSecret,
            BrowserTrackerId = e.BrowserTrackerId
        }).ToList();
    }
}
