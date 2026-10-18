using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace ACRLiveTiming.Submission;

/// <summary>Rules on the addresses a results server is reached at: the pasted
/// key's <c>serverUrl</c> and the <c>submissionUrl</c> the exchange returns.</summary>
public static class ServerAddress
{
    /// <summary>Why <paramref name="text"/> is not an acceptable server base URL,
    /// or null when it is. Https only; plain http only in a Debug build, to a test
    /// server on this PC or the local network (see <see cref="IsDebugHttpHost"/>).</summary>
    public static string? CheckServerUrl(string text, out Uri? url)
    {
        url = null;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed)) return "serverUrl is not an absolute URL";
        string? scheme = CheckScheme(parsed);
        if (scheme != null) return scheme;
        if (parsed.UserInfo.Length > 0) return "serverUrl must not carry a user name or password";
        if (parsed.Query.Length > 0 || parsed.Fragment.Length > 0) return "serverUrl must not carry a query or fragment";
        if (text.EndsWith('/')) return "serverUrl must not end with '/'";
        url = parsed;
        return null;
    }

    /// <summary>Why <paramref name="text"/> is not an acceptable submissionUrl for a
    /// key pointing at <paramref name="server"/>, or null when it is.</summary>
    public static string? CheckSubmissionUrl(string text, Uri server, out Uri? url)
    {
        url = null;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed)) return "submissionUrl is not an absolute URL";
        if (!SameOrigin(parsed, server)) return "submissionUrl is not on the key's server";
        if (parsed.UserInfo.Length > 0 || parsed.Fragment.Length > 0) return "submissionUrl carries a user name or fragment";
        url = parsed;
        return null;
    }

    public static bool SameOrigin(Uri a, Uri b)
        => string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
           && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase)
           && a.Port == b.Port;

    /// <summary>The host the operator checks: punycode, with the port when it is
    /// not the scheme's default ("results.example.org", "203.0.113.10:8443").</summary>
    public static string DisplayHost(Uri url, bool withPort = true)
    {
        string host = url.HostNameType == UriHostNameType.IPv6 ? "[" + url.IdnHost + "]" : url.IdnHost;
        return withPort && !url.IsDefaultPort ? host + ":" + url.Port.ToString(CultureInfo.InvariantCulture) : host;
    }

    private static string? CheckScheme(Uri url)
    {
        if (url.Scheme == Uri.UriSchemeHttps) return null;
#if DEBUG
        if (url.Scheme == Uri.UriSchemeHttp && IsDebugHttpHost(url)) return null;
        return "serverUrl must be https (http only to localhost or a private IP address)";
#else
        return "serverUrl must be https";
#endif
    }

    /// <summary>A Debug build's plain-http test target: localhost, or a literal
    /// loopback / private IP address. Never another host name, which could
    /// resolve anywhere.</summary>
    public static bool IsDebugHttpHost(Uri url)
    {
        if (url.IsLoopback && string.Equals(url.Host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (url.HostNameType is not (UriHostNameType.IPv4 or UriHostNameType.IPv6)) return false;
        if (!IPAddress.TryParse(url.IdnHost.Trim('[', ']'), out var ip)) return false;
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;      // fc00::/7 unique local
        byte[] b = ip.GetAddressBytes();
        return b[0] == 10
               || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
               || (b[0] == 192 && b[1] == 168);
    }
}
