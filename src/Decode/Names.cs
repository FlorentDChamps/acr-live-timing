using System.Text.RegularExpressions;

namespace ACRLiveTiming.Decode
{
    /// <summary>
    /// Name/stage classification, ported from the original protocol-RE prototype.
    /// Distinguishes player
    /// display names from EOS GUIDs and stage/level names.
    /// </summary>
    public static class Names
    {
        static readonly Regex Hex32 = new(@"^[0-9A-Fa-f]{32}$", RegexOptions.Compiled);

        // stage name: <Location>S<num><Name>[<Variant>]. Location may be multi-word
        // CamelCase (GreeceS4Loutraki, WalesS3HafrenNorth, MonteCarloS2Sisteron); an
        // optional route variant may follow (MonteCarloS2SisteronCut2Reverse — a cut /
        // reverse-direction routing of the same level). The trailing (?:[A-Z]…)* captures
        // that variant so we can show the exact route, not just the base level.
        static readonly Regex StageRe = new(@"^(?:[A-Z][a-z]+)+S\d+[A-Z][A-Za-z]+(?:[A-Z][A-Za-z0-9]*)*$", RegexOptions.Compiled);
        // the bare level, no variant suffix — used to tell a real ROUTE (variant) apart
        // from the level name (which is replicated constantly as the persistent level).
        static readonly Regex StageBaseRe = new(@"^(?:[A-Z][a-z]+)+S\d+[A-Z][A-Za-z]+$", RegexOptions.Compiled);

        /// <summary>Run-start event markers: a fresh table / stage.</summary>
        public static readonly string[] RunStart =
            { "FSM.Flow.BeginStartSequence", "RaceEventTimer.PreSemaphore" };

        // setup-ref punctuation, rejected in player names (hot path: no per-call alloc)
        static readonly char[] SetupChars = { '(', ')', ':' };

        public static bool IsPlayerName(string s)
        {
            // A name only OWNS a result block when it sits next to a valid duplicate-
            // signature split chain (ResultScanner), so engine tokens / car models /
            // setup fields / nation names never bind — no reject-list needed here (a
            // hardcoded brand + nation blacklist was verified redundant on every capture
            // and dropped). We only exclude the two things that DO look name-like and can
            // sit near a block: EOS GUIDs (32 hex) and stage/level names.
            // Player names CAN contain '.' or '-' (atsam.88, 2K-Tan); setup refs use '(' ':' ')'.
            if (Hex32.IsMatch(s) || StageRe.IsMatch(s))
                return false;
            if (s.IndexOfAny(SetupChars) >= 0)
                return false;
            return s.Length >= 2 && s.Length <= 24;
        }

        /// <summary>True if s is a route VARIANT (has a Cut/Reverse-style suffix), not the
        /// bare level name. The variant is the actually-driven route.</summary>
        public static bool IsVariant(string s) => StageRe.IsMatch(s) && !StageBaseRe.IsMatch(s);

        /// <summary>The MOST SPECIFIC route in the packet: both the bare level
        /// ("MonteCarloS2Sisteron") and its variant ("…Cut2Reverse") can be present;
        /// the longer one is the actual route.</summary>
        public static string? StageNameIn(IEnumerable<string> strings)
        {
            string? best = null;
            foreach (var s in strings)
                if (StageRe.IsMatch(s) && (best == null || s.Length > best.Length)) best = s;
            return best;
        }

        public static bool HasRunStart(IEnumerable<string> strings)
        {
            foreach (var s in strings)
                foreach (var marker in RunStart)
                    if (s == marker) return true;
            return false;
        }
    }
}
