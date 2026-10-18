using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ACRLiveTiming.Submission;

/// <summary>Outcome of a pairing exchange: the new association and its token, or
/// a message for the operator.</summary>
public sealed record ExchangeResult(Association? Association, string? Token, string Message, bool KeepKey,
                                    IReadOnlyList<string>? IgnoredSettings = null);

/// <summary>Outcome of one send: what the association row shows, and what the
/// answer means for the association.</summary>
public sealed record SubmitResult(bool Stored, string Message, int? Status, bool TokenRefused, bool? Active);

/// <summary>
/// The only network code of results submission: the exchange and the sends. One
/// request each, no automatic retry, never a redirect, normal TLS validation. The
/// token appears only in the Authorization header of a send.
/// </summary>
public static class SubmissionClient
{
    private const int MaxBody = 64 * 1024;
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static readonly JsonSerializerOptions PlainJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static string UserAgent => "ACRLiveTiming/" + AppInfo.Version;

    public static async Task<ExchangeResult> ExchangeAsync(PairingKey key, string clientInstanceId)
    {
        var endpoint = new Uri(key.ServerUrl.OriginalString + "/v1/ingest/pairings/exchange");
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
        {
            ["schemaVersion"] = "acr-pairing/1.0",
            ["pairingCode"] = key.PairingCode,
            ["clientInstanceId"] = clientInstanceId,
            ["appVersion"] = AppInfo.Version
        }, PlainJson);

        HttpResponseMessage response;
        try { response = await Http.SendAsync(Request(endpoint, body, token: null)); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new(null, null, "Server unreachable — try again.", KeepKey: true);
        }

        using (response)
        {
            int status = (int)response.StatusCode;
            string text = await ReadBodyAsync(response);
            if (status is >= 300 and < 400)
                return new(null, null, $"Redirect refused (HTTP {status}).", KeepKey: true);
            if (status == 429)
                return new(null, null, "Too many attempts — try again later" + RetryAfter(response) + ".", KeepKey: true);
            if (status >= 500)
                return new(null, null, $"Server unreachable — try again (HTTP {status}).", KeepKey: true);
            if (status is 400 or 401 or 403 or 404 or 409 or 410 or 422)
                return new(null, null, $"Key already used or expired — ask for a new one (HTTP {status}).", KeepKey: false);
            if (status is < 200 or >= 300)
                return new(null, null, $"Pairing refused (HTTP {status}){ServerMessage(text, ": ")}", KeepKey: false);
            return ParseExchange(key, text);
        }
    }

    /// <summary>Reads a 2xx exchange answer. Every rule that fails makes the whole
    /// exchange fail: nothing of it is kept.</summary>
    public static ExchangeResult ParseExchange(PairingKey key, string text)
    {
        static ExchangeResult Fail(string why) => new(null, null, "Invalid pairing answer: " + why + ".", KeepKey: false);
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(text);
            root = doc.RootElement.Clone();
        }
        catch (JsonException) { return Fail("not JSON"); }
        if (root.ValueKind != JsonValueKind.Object) return Fail("not a JSON object");

        string? schema = Str(root, "schemaVersion");
        if (schema != null && !schema.StartsWith("acr-pairing-response/1.", StringComparison.Ordinal))
            return Fail("unsupported version");
        string? token = Str(root, "submissionToken");
        if (string.IsNullOrEmpty(token)) return Fail("no submissionToken");
        string? submission = Str(root, "submissionUrl");
        if (submission == null) return Fail("no submissionUrl");
        string? urlError = ServerAddress.CheckSubmissionUrl(submission, key.ServerUrl, out _);
        if (urlError != null) return Fail(urlError);

        root.TryGetProperty("event", out var ev);
        bool grouped = ev.ValueKind == JsonValueKind.Object;
        bool hasEventId = root.TryGetProperty("eventId", out var eventIdElement)
                          || (grouped && ev.TryGetProperty("eventId", out eventIdElement));
        if (!hasEventId || eventIdElement.ValueKind != JsonValueKind.String) return Fail("eventId must be a string");
        string eventId = eventIdElement.GetString()!;
        if (eventId.Length is < 1 or > 128) return Fail("eventId must be 1 to 128 characters");

        DateTimeOffset? expires = null;
        if (Str(root, "expiresAt") is string expiresText)
        {
            if (!DateTimeOffset.TryParse(expiresText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var e))
                return Fail("expiresAt is not a date");
            expires = e;
        }

        var settings = ReadSettings(root, out var ignoredSettings);
        var association = new Association
        {
            ServerUrl = key.ServerUrl.OriginalString,
            KeyHash = key.Hash,
            SubmissionUrl = submission,
            EventId = eventId,
            EventName = Str(root, "eventName") ?? (grouped ? Str(ev, "name") : null),
            ServerName = Str(root, "serverName"),
            ExpiresAt = expires,
            AuthorizationId = Str(root, "authorizationId"),
            EventSettings = settings,
            PairedAt = DateTimeOffset.UtcNow,
            PairedTimestamp = Association.Now()
        };
        return new(association, token, "Paired.", KeepKey: false, ignoredSettings.Count == 0 ? null : ignoredSettings);
    }

    /// <summary>The optional eventSettings block. An invalid field is dropped and
    /// named in <paramref name="ignored"/> for the log, never a failed exchange.</summary>
    private static EventSettings? ReadSettings(JsonElement root, out List<string> ignored)
    {
        ignored = [];
        if (!root.TryGetProperty("eventSettings", out var s) || s.ValueKind != JsonValueKind.Object) return null;
        var settings = new EventSettings();
        if (s.TryGetProperty("penaltyCapPct", out var pct))
        {
            if (pct.ValueKind == JsonValueKind.Number && pct.TryGetDouble(out double value) && value >= 0 && value <= 500)
                settings.PenaltyCapPct = value;
            else
                ignored.Add("penaltyCapPct");
        }
        if (s.TryGetProperty("dnfNoRejoin", out var dnf))
        {
            if (dnf.ValueKind is JsonValueKind.True or JsonValueKind.False)
                settings.DnfNoRejoin = dnf.GetBoolean();
            else
                ignored.Add("dnfNoRejoin");
        }
        return settings.PenaltyCapPct == null && settings.DnfNoRejoin == null ? null : settings;
    }

    public static async Task<SubmitResult> SubmitAsync(Association association, byte[] body)
    {
        string? token = association.GetToken();
        if (token == null) return new(false, "No token — a new key is needed.", null, false, null);
        if (ServerAddress.CheckSubmissionUrl(association.SubmissionUrl, association.Server, out var url) != null)
            return new(false, "Invalid submission address.", null, false, null);

        HttpResponseMessage response;
        try { response = await Http.SendAsync(Request(url!, body, token)); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new(false, "Server unreachable — send again.", null, false, null);
        }

        using (response)
        {
            int status = (int)response.StatusCode;
            string text = await ReadBodyAsync(response);
            return Interpret(status, text, RetryAfter(response));
        }
    }

    /// <summary>Maps one send's answer to what the operator sees.</summary>
    public static SubmitResult Interpret(int status, string text, string retryAfter = "")
    {
        if (status is >= 200 and < 300)
        {
            // only an explicit "ended" closes the association: a server that says
            // nothing about it keeps receiving results
            bool active = ReadAssociation(text) != "ended";
            return new(true, "Stored" + ServerMessage(text, " — "), status, false, active);
        }
        if (status == 429) return new(false, "Too many requests" + retryAfter + ".", status, false, null);
        if (status is 401 or 403) return new(false, "Association refused — a new key is needed.", status, true, null);
        if (status is >= 300 and < 400) return new(false, $"Redirect refused (HTTP {status}).", status, false, null);
        if (status >= 500 || status == 408) return new(false, $"Server unreachable — send again (HTTP {status}).", status, false, null);
        return new(false, $"Refused (HTTP {status}){ServerMessage(text, ": ")}", status, false, null);
    }

    private static HttpRequestMessage Request(Uri url, byte[] body, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response)
    {
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync();
            byte[] buffer = new byte[MaxBody];
            int total = 0, read;
            while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total))) > 0) total += read;
            return Encoding.UTF8.GetString(buffer, 0, total);
        }
        catch { return ""; }
    }

    private static string? ReadAssociation(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? Str(doc.RootElement, "association") : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The server's "message", else "error", else the start of a non-JSON
    /// body: plain text, at most 300 characters. Empty when there is none.</summary>
    public static string ServerMessage(string text, string prefix)
    {
        string message;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            message = root.ValueKind == JsonValueKind.Object ? Str(root, "message") ?? Str(root, "error") ?? "" : "";
        }
        catch (JsonException) { message = text; }
        message = TextSanitizer.Clean(message, 300);
        return message.Length > 0 ? prefix + message : "";
    }

    private static string RetryAfter(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        TimeSpan? delay = retry?.Delta ?? (retry?.Date is DateTimeOffset date ? date - DateTimeOffset.UtcNow : null);
        if (delay is not TimeSpan d || d <= TimeSpan.Zero) return "";
        if (d > TimeSpan.FromHours(1)) d = TimeSpan.FromHours(1);
        return d.TotalSeconds < 90 ? $" (retry in {Math.Ceiling(d.TotalSeconds)} s)" : $" (retry in {Math.Ceiling(d.TotalMinutes)} min)";
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
