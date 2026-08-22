using System.Text.RegularExpressions;
using ACRLiveTiming.Content;

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
        // A route (variant) ends with <Full|Short<n>|Cut<n>><Forward|Reverse>; the bare
        // level name (replicated constantly as the persistent level) never does. Matching
        // the suffix explicitly matters: "…FullForward" has no digit and used to pass as
        // a level name, so Full routes were never recognised as the driven route.
        static readonly Regex VariantSuffixRe = new(@"(?:Full|Short\d+|Cut\d+)(?:Forward|Reverse)$", RegexOptions.Compiled);

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

        /// <summary>True if s is a route VARIANT (Full/Short/Cut + Forward/Reverse suffix,
        /// or a route the content catalog knows), not the bare level name. The variant
        /// is the actually-driven route.</summary>
        public static bool IsVariant(string s)
            => (StageRe.IsMatch(s) && VariantSuffixRe.IsMatch(s)) || ContentCatalog.IsKnownRoute(s);

        /// <summary>The level a route belongs to, by name: "WelesS3HafrenNorthCut1Reverse"
        /// → "WelesS3HafrenNorth". Spelling is the wire's; see <see cref="VariantExtends"/>.</summary>
        public static string BaseOf(string variant) => VariantSuffixRe.Replace(variant, "");

        /// <summary>True if the route drives the given level. Compared through the
        /// catalog's level aliases: the game spells some routes differently from their
        /// level ("WelesS3HafrenNorthCut1Reverse" runs on "WalesS3HafrenNorth").</summary>
        public static bool VariantExtends(string variant, string level)
            => ContentCatalog.CanonicalLevel(BaseOf(variant)) == ContentCatalog.CanonicalLevel(level);

        /// <summary>The bare LEVEL name in the packet ("MonteCarloS2Sisteron"), longest
        /// match wins; routes (variants) are handled separately and never count as the
        /// level, whatever their length.</summary>
        public static string? StageBaseIn(IEnumerable<string> strings)
        {
            string? best = null;
            foreach (var s in strings)
                if (StageRe.IsMatch(s) && !IsVariant(s) && (best == null || s.Length > best.Length)) best = s;
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
