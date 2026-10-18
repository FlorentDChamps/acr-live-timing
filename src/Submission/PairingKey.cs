using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ACRLiveTiming.Submission;

/// <summary>
/// A pairing key as the server hands it out: <c>acrlt1:</c> followed by the
/// base64url (no padding) of a small UTF-8 JSON document naming the server and a
/// pairing code. It is neither encrypted nor signed; it only makes the pair easy
/// to paste. Decoding it makes no request.
/// </summary>
public sealed class PairingKey
{
    public const string Prefix = "acrlt1:";
    private const int MaxLength = 4096;
    private const int MaxCodeLength = 128;

    public Uri ServerUrl { get; }
    public string PairingCode { get; }

    private PairingKey(Uri serverUrl, string pairingCode)
    {
        ServerUrl = serverUrl;
        PairingCode = pairingCode;
    }

    /// <summary>The host the operator checks before pairing.</summary>
    public string Host => ServerAddress.DisplayHost(ServerUrl);

    /// <summary>Recognises a key already exchanged without storing its code.</summary>
    public string Hash
    {
        get
        {
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(ServerUrl.OriginalString + "\n" + PairingCode));
            return Convert.ToHexString(digest).ToLowerInvariant();
        }
    }

    /// <summary>Decodes <paramref name="text"/>; on failure <paramref name="error"/>
    /// says which part is wrong.</summary>
    public static bool TryParse(string text, out PairingKey? key, out string error)
    {
        key = null;
        text = text.Trim();
        if (text.Length == 0) { error = "Paste a key."; return false; }
        if (text.Length > MaxLength) { error = "Key too long."; return false; }
        if (!text.StartsWith(Prefix, StringComparison.Ordinal)) { error = $"Not a pairing key (it must start with \"{Prefix}\")."; return false; }

        byte[] json;
        try { json = Base64UrlDecode(text[Prefix.Length..]); }
        catch (FormatException) { error = "Invalid key: damaged base64url."; return false; }

        string? schema, serverUrl, code;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { error = "Invalid key: not a JSON object."; return false; }
            schema = ReadString(doc.RootElement, "schemaVersion");
            serverUrl = ReadString(doc.RootElement, "serverUrl");
            code = ReadString(doc.RootElement, "pairingCode");
        }
        catch (JsonException) { error = "Invalid key: damaged JSON."; return false; }

        if (schema == null || !schema.StartsWith("acr-pairing-key/1.", StringComparison.Ordinal))
        {
            error = "Unsupported key version" + (schema == null ? "." : $" ({Clean(schema)}).");
            return false;
        }
        if (serverUrl == null) { error = "Invalid key: no serverUrl."; return false; }
        string? urlError = ServerAddress.CheckServerUrl(serverUrl, out var url);
        if (urlError != null) { error = "Invalid key: " + urlError + "."; return false; }
        if (string.IsNullOrEmpty(code) || code.Length > MaxCodeLength || code.Any(c => c < 0x20 || c > 0x7E))
        {
            error = "Invalid key: bad pairingCode.";
            return false;
        }

        key = new PairingKey(url!, code);
        error = "";
        return true;
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Clean(string value) => TextSanitizer.Clean(value, 60);

    private static byte[] Base64UrlDecode(string text)
    {
        if (text.Length == 0 || text.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_')))
            throw new FormatException();
        string b64 = text.Replace('-', '+').Replace('_', '/');
        switch (b64.Length % 4)
        {
            case 2: b64 += "=="; break;
            case 3: b64 += "="; break;
            case 1: throw new FormatException();
        }
        return Convert.FromBase64String(b64);
    }
}
