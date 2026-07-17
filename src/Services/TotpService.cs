using System.Security.Cryptography;
using System.Text;

namespace RobloxAccountManager.Services;

/// <summary>
/// RFC 6238 TOTP (time-based one-time password) generator, so a 2FA-protected
/// account's current code can be shown/copied when re-adding it in the browser.
/// The secret is the Base32 string Roblox shows under "Can't scan the QR code?".
/// </summary>
public static class TotpService
{
    /// <summary>Current 6-digit code for a Base32 secret, or null if the secret is unusable.</summary>
    public static string? Generate(string base32Secret, int digits = 6, int periodSeconds = 30)
    {
        try
        {
            byte[] key = Base32Decode(base32Secret);
            if (key.Length == 0) return null;

            long counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / periodSeconds;
            byte[] counterBytes = BitConverter.GetBytes(counter);
            if (BitConverter.IsLittleEndian) Array.Reverse(counterBytes);

            using var hmac = new HMACSHA1(key);
            byte[] hash = hmac.ComputeHash(counterBytes);

            int offset = hash[^1] & 0x0F;
            int binary = ((hash[offset] & 0x7F) << 24)
                       | ((hash[offset + 1] & 0xFF) << 16)
                       | ((hash[offset + 2] & 0xFF) << 8)
                       | (hash[offset + 3] & 0xFF);

            int otp = binary % (int)Math.Pow(10, digits);
            return otp.ToString().PadLeft(digits, '0');
        }
        catch { return null; }
    }

    /// <summary>Seconds until the current code rolls over — handy for a live countdown.</summary>
    public static int SecondsRemaining(int periodSeconds = 30)
        => periodSeconds - (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() % periodSeconds);

    private static byte[] Base32Decode(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return Array.Empty<byte>();
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        input = input.Trim().Replace(" ", "").Replace("-", "").TrimEnd('=').ToUpperInvariant();

        var bits = new StringBuilder();
        foreach (char c in input)
        {
            int idx = alphabet.IndexOf(c);
            if (idx < 0) continue; // skip stray characters instead of throwing
            bits.Append(Convert.ToString(idx, 2).PadLeft(5, '0'));
        }

        var bytes = new List<byte>();
        for (int i = 0; i + 8 <= bits.Length; i += 8)
            bytes.Add(Convert.ToByte(bits.ToString(i, 8), 2));

        return bytes.ToArray();
    }
}
