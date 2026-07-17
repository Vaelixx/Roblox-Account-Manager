using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RobloxAccountManager.Services;

/// <summary>
/// Imports accounts from ic3w0lf's "Roblox Account Manager" (the widely-used
/// original by ic3w0lf22).
///
/// Strategy — a locator + tolerant reader, on purpose:
///   ic3w0lf stores its account list in an <c>AccountData</c> blob that, when a
///   master password is set, is AES-encrypted with a scheme we deliberately do
///   NOT try to replicate (its key derivation is an implementation detail that
///   changes between releases — decrypting it blindly would silently corrupt
///   or leak tokens). Instead we read the file as raw bytes, decode with
///   Latin-1 (a lossless byte→char map so ASCII survives even inside an
///   otherwise-binary blob) and let the <see cref="AccountStore"/> cookie regex
///   pull any <c>.ROBLOSECURITY</c> tokens back out.
///
///   • Unencrypted setups (no master password) keep cookies as plain JSON text —
///     the tokens are found directly.
///   • ic3w0lf's own "Export" feature writes plain cookies — also found.
///   • An AES-encrypted blob yields zero visible tokens → we report that clearly
///     and point the user at ic3w0lf's Export instead of guessing.
///
/// The actual add/validation is done by <see cref="AccountStore.ImportManyAsync"/>;
/// this service only finds the file and extracts the cookie-bearing text.
/// </summary>
public static class Ic3w0lfImportService
{
    // Same token ic3w0lf and Roblox both use; kept local so this service has no
    // hidden coupling to AccountStore's private regex.
    private static readonly Regex CookieRegex =
        new(@"_\|WARNING:-DO-NOT-SHARE-THIS\.[^""'\s]+", RegexOptions.Compiled);

    /// <summary>
    /// Well-known spots where ic3w0lf's manager keeps its <c>AccountData</c>.
    /// Ordered most- to least-likely; only existing files are returned.
    /// </summary>
    public static IReadOnlyList<string> CandidatePaths()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var names = new[] { "AccountData", "AccountData.json", "accounts.json" };
        var folders = new[]
        {
            Path.Combine(appData,  "RAMDataFolder"),
            Path.Combine(localApp, "RAMDataFolder"),
            Path.Combine(appData,  "RAM"),
            Path.Combine(localApp, "RAM"),
            Path.Combine(appData,  "Roblox Account Manager"),
            Path.Combine(localApp, "Roblox Account Manager"),
        };

        var found = new List<string>();
        foreach (var f in folders)
            foreach (var n in names)
            {
                var p = Path.Combine(f, n);
                if (File.Exists(p)) found.Add(p);
            }
        return found;
    }

    /// <summary>First auto-detected ic3w0lf data file, or <c>null</c>.</summary>
    public static string? AutoLocate() => CandidatePaths().FirstOrDefault();

    public record ReadResult(bool Ok, string Text, int CookieCount, string Message);

    /// <summary>
    /// Reads <paramref name="path"/> and pulls out every cookie-bearing region
    /// as text suitable for <see cref="AccountStore.ImportManyAsync"/>.
    /// Never throws for a missing/locked/encrypted file — returns a explanatory
    /// <see cref="ReadResult"/> with <c>Ok = false</c> instead.
    /// </summary>
    public static ReadResult ReadFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new(false, "", 0, "File not found.");

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            return new(false, "", 0, $"Couldn't read the file: {ex.Message}");
        }

        // Latin-1 is a total byte→char map: ASCII cookie tokens survive verbatim
        // even when the surrounding bytes are non-text (an encrypted blob).
        string text = Encoding.Latin1.GetString(bytes);
        int count = CookieRegex.Matches(text).Count;

        if (count == 0)
            return new(false, text, 0,
                "No cookies found in that file. If your ic3w0lf manager uses a master " +
                "password its account data is encrypted — open ic3w0lf, use its " +
                "\"Export\" feature to write a plain cookie list, and import that here.");

        return new(true, text, count,
            $"Found {count} cookie{(count == 1 ? "" : "s")} — validating with Roblox…");
    }
}
