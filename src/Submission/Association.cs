using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace ACRLiveTiming.Submission;

public enum AssociationState { Active, Ended, Refused, Expired }

/// <summary>What the server suggests for its event. Every field is optional;
/// the operator can change the board settings afterwards.</summary>
public sealed class EventSettings
{
    public double? PenaltyCapPct { get; set; }
    public bool? DnfNoRejoin { get; set; }
}

/// <summary>
/// One successful exchange: a server, its event and the submission token. Only
/// active associations are kept across restarts; the token is DPAPI-encrypted
/// for this Windows user, with the association id as entropy, and never leaves
/// this object in clear except as the Authorization header of a send.
/// </summary>
public sealed class Association
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ServerUrl { get; set; } = "";
    public string KeyHash { get; set; } = "";
    public string SubmissionUrl { get; set; } = "";
    public string EventId { get; set; } = "";
    public string? EventName { get; set; }
    public string? ServerName { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? AuthorizationId { get; set; }
    public EventSettings? EventSettings { get; set; }
    public DateTimeOffset PairedAt { get; set; }
    public string TokenProtected { get; set; } = "";
    public AssociationState State { get; set; } = AssociationState.Active;

    // ---- this run only ----
    /// <summary>Stopwatch timestamp of the exchange when it happened in this run of
    /// the app; null for an association restored from disk, which precedes every
    /// stage of this run.</summary>
    [JsonIgnore] public long? PairedTimestamp { get; set; }
    [JsonIgnore] public bool Selected { get; set; } = true;
    [JsonIgnore] public bool Sending { get; set; }
    [JsonIgnore] public string LastSend { get; set; } = "";
    [JsonIgnore] public string LastSendDetail { get; set; } = "";

    [JsonIgnore] public Uri Server => new(ServerUrl);
    [JsonIgnore] public string Host => ServerAddress.DisplayHost(Server);
    [JsonIgnore] public bool HasToken => TokenProtected.Length > 0;

    /// <summary>Plain-text label: "serverName · host — eventName".</summary>
    [JsonIgnore]
    public string Label
    {
        get
        {
            string server = string.IsNullOrWhiteSpace(ServerName) ? Host : TextSanitizer.Clean(ServerName, 200) + " · " + Host;
            string name = TextSanitizer.Clean(EventName, 200);
            return name.Length > 0 ? server + " — " + name : server + " — " + TextSanitizer.Clean(EventId, 128);
        }
    }

    /// <summary>The public form shown on the web page: the server's name, or its
    /// host without the port, and the event name. Never the URL.</summary>
    [JsonIgnore] public string PublicServer => string.IsNullOrWhiteSpace(ServerName) ? ServerAddress.DisplayHost(Server, withPort: false) : TextSanitizer.Clean(ServerName, 200);
    [JsonIgnore] public string PublicEvent => string.IsNullOrWhiteSpace(EventName) ? TextSanitizer.Clean(EventId, 128) : TextSanitizer.Clean(EventName, 200);

    /// <summary>True when the stage started after this association was made.</summary>
    public bool StartedAfterPairing(long? stageStartTimestamp)
        => stageStartTimestamp is long start && (PairedTimestamp is not long paired || start > paired);

    public void SetToken(string token)
        => TokenProtected = Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(Id), DataProtectionScope.CurrentUser));

    public string? GetToken()
    {
        if (!HasToken) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(TokenProtected), Encoding.UTF8.GetBytes(Id), DataProtectionScope.CurrentUser));
        }
        catch { return null; }   // another Windows user or a damaged file: treat as no token
    }

    public void DeleteToken(AssociationState state)
    {
        TokenProtected = "";
        State = state;
        Selected = false;
    }

    public static long Now() => Stopwatch.GetTimestamp();
}
