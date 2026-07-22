using System.Diagnostics.CodeAnalysis;
using ACRLiveTiming.Decode;

namespace ACRLiveTiming.Net
{
    /// <summary>
    /// Finds the ACR game server endpoint by protocol signature (never hardcoded:
    /// IP and port change every lobby). A public, non-Steam source that accumulates
    /// SigMin payloads carrying our cleartext protocol (extractable FStrings or a
    /// 0x14 bunch header) is the server. Ported from the original protocol-RE
    /// prototype.
    ///
    /// No-circumvention policy: this detector locks on CLEARTEXT traffic only. If the
    /// game ever encrypts the stream, the signature simply stops matching, nothing
    /// locks, and <see cref="SuspectedEncrypted"/> fires once. The tool never tries to
    /// defeat, decrypt or work around a protection — decoding stops where cleartext
    /// stops, by design.
    /// </summary>
    public sealed class ServerDetector
    {
        const int SigMin = 4;
        // Encryption heuristic: how many sustained no-signature packets from one public
        // endpoint (and what fraction of them looking non-cleartext) before we tell the
        // user the stream looks encrypted / unsupported rather than silently doing nothing.
        const int EncSampleMin = 300;
        const double EncHighEntropyFrac = 0.8;

        readonly Dictionary<(string ip, int port), int> _score = new();
        readonly Dictionary<(string ip, int port), (int total, int highEntropy)> _noSig = new();
        bool _noticeSent;

        /// <summary>Fires once when a public endpoint streams sustained traffic that
        /// carries no cleartext signature and looks encrypted (likely a game update that
        /// turned on encryption). Informational only — the tool does not react beyond
        /// telling the user; it will not attempt to decrypt.</summary>
        public event Action<string>? SuspectedEncrypted;

        public bool Feed(string ip, int port, byte[] payload,
                         [NotNullWhen(true)] out string? serverIp, out int serverPort)
        {
            serverIp = null; serverPort = 0;
            if (IsPrivate(ip)) return false;
            if ((port >= 27000 && port <= 27050) || port == 3478 || port == 4379 || port == 4380)
                return false;   // Steam / SteamNetworkingSockets noise

            bool signature = Primitives.FStrings(payload).Count > 0 || (payload.Length > 0 && payload[0] == 0x14);
            if (!signature)
            {
                TrackNonSignature(ip, port, payload);
                return false;
            }

            var key = (ip, port);
            // unbounded-growth guard: sniffing ALL inbound UDP can slowly accumulate
            // signature-like sources over a long search. Scores rebuild in SigMin
            // packets, so dropping them all is cheap.
            if (!_score.ContainsKey(key) && _score.Count >= 1024) _score.Clear();
            _score.TryGetValue(key, out var count);
            _score[key] = count + 1;
            if (_score[key] >= SigMin)
            {
                serverIp = ip; serverPort = port;
                return true;
            }
            return false;
        }

        /// <summary>Watch for a public endpoint that floods us with unreadable traffic.
        /// If it is sustained and looks non-cleartext, surface it once — this is the
        /// visible symptom of the game encrypting its stream.</summary>
        void TrackNonSignature(string ip, int port, byte[] payload)
        {
            if (_noticeSent) return;
            var key = (ip, port);
            if (!_noSig.ContainsKey(key) && _noSig.Count >= 1024) _noSig.Clear();
            _noSig.TryGetValue(key, out var s);
            s.total++;
            if (LooksHighEntropy(payload)) s.highEntropy++;
            _noSig[key] = s;

            if (s.total >= EncSampleMin && s.highEntropy >= s.total * EncHighEntropyFrac)
            {
                _noticeSent = true;
                SuspectedEncrypted?.Invoke(
                    $"{ip}:{port} is sending sustained traffic this tool cannot read — it looks " +
                    "encrypted or is not ACR. This tool only decodes cleartext and will not attempt " +
                    "to decrypt anything. If ACR shipped an update that encrypts its traffic, live " +
                    "timing is no longer available.");
            }
        }

        /// <summary>Cheap non-cleartext test: a long payload with very few printable
        /// bytes. Cleartext UE bunches are dense with FStrings/printable bytes; an
        /// encrypted payload is near-uniform random, so almost nothing is printable.</summary>
        static bool LooksHighEntropy(byte[] payload)
        {
            if (payload.Length < 32) return false;
            int printable = 0;
            foreach (var b in payload)
                if (b >= 32 && b < 127) printable++;
            return printable < payload.Length / 4;   // < 25% printable
        }

        public void Reset()
        {
            _score.Clear();
            _noSig.Clear();
            // _noticeSent stays latched: warn at most once per app run, not per reconnect.
        }

        public static bool IsPrivate(string ip)
        {
            if (ip.StartsWith("192.168.") || ip.StartsWith("10.") || ip.StartsWith("127.")
                || ip.StartsWith("169.254.") || ip == "255.255.255.255")
                return true;
            var parts = ip.Split('.');
            if (parts.Length < 2 || !int.TryParse(parts[0], out var a) || !int.TryParse(parts[1], out var b))
                return false;
            if (a >= 224 && a <= 239) return true;              // multicast, whole block
            if (a == 172 && b >= 16 && b <= 31) return true;    // RFC1918
            if (a == 100 && b >= 64 && b <= 127) return true;   // CGNAT / Tailscale (100.64/10)
            return false;
        }
    }
}
