using System.Text.Json;
using System.Text.Json.Serialization;

namespace RobloxAccountManager.Services;

/// <summary>
/// Stores a settings value DPAPI-encrypted ("enc1:…") instead of in plain text. Used for the
/// secrets that live in settings.json — the proxy password, the local API token and the Discord
/// webhook URL (anyone holding a webhook URL can post into that channel).
///
/// Reading accepts the old plain-text form too, so a settings file from v1.x loads unchanged and is
/// encrypted the next time it is saved. A value that cannot be decrypted — the file was copied from
/// another Windows user — reads as empty rather than as a blob of ciphertext.
/// </summary>
public sealed class ProtectedStringConverter : JsonConverter<string>
{
    // Without this the serializer hands a JSON null past Read and the property ends up null, not "".
    public override bool HandleNull => true;

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return "";
        string raw = reader.GetString() ?? "";
        return Crypto.UnprotectString(raw);
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        try { writer.WriteStringValue(string.IsNullOrEmpty(value) ? "" : Crypto.ProtectString(value)); }
        catch { writer.WriteStringValue(""); }   // never fall back to writing the secret in the clear
    }
}
