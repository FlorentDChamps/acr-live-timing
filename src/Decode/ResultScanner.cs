using static ACRLiveTiming.Decode.Primitives;

namespace ACRLiveTiming.Decode
{
    /// <summary>
    /// Final stage-result extraction, ported 1:1 from the original protocol-RE
    /// prototype.
    ///
    /// A driver result block is self-validating: split1 (sector-1 cumulative) is
    /// written twice 5 bytes apart (f32@P == f32@P+5), then cumulative splits sit
    /// on a 15-byte stride, strictly increasing; the FINAL time is the last/largest.
    /// P is found by the duplicate signature (the anchor drifts between packets).
    /// The penalty float sits 11 bytes past the last cumulative split; displayed
    /// total = raw + penalty. Callers scan all 8 bit-shifts because the
    /// results-manager net bunch starts mid-byte, keeping the largest raw per name.
    /// </summary>
    public static class ResultScanner
    {
        const double TotalMin = 20.0, TotalMax = 1800.0;
        const int SplitStride = 15;
        const int DupGap = 5;
        const double DupTolerance = 0.05;
        const int PenaltyOffset = 11;
        const double PenaltyMax = 300.0;
        const int BlockMaxGap = 96;
        const byte LiveTag = 0x10;   // property tag at P+9 for a mid-stage LIVE running
                                     // time (a driver still on stage). Finals carry the
                                     // sector-complete tag (0x0e). We BLACKLIST 0x10
                                     // rather than whitelist 0x0e so a real finish is
                                     // never dropped if its tag differs in a live stream.

        static List<double> ChainFrom(byte[] payload, int start)
        {
            var chain = new List<double>();
            int o = start;
            while (true)
            {
                var value = F32(payload, o);
                if (value == null) break;
                double v = value.Value;
                if (!(TotalMin < v && v < TotalMax)) break;
                if (chain.Count > 0 && v <= chain[chain.Count - 1] + 0.2) break;
                chain.Add(v);
                o += SplitStride;
            }
            return chain;
        }

        /// <summary>
        /// Yields (name, rawTotal, penalty, sectors) for each driver block in a packet.
        /// `sectors` = length of the cumulative-split chain. A driver has FINISHED the
        /// stage only when this reaches the stage's full sector count; shorter chains
        /// are live intermediates (still on stage). The caller keeps the largest block
        /// per driver (the final) and, per run column, only reveals drivers whose block
        /// matches the column's max sector count — so climbing partials never show.
        /// The payload's FStrings are passed in pre-extracted (a caller scanning all
        /// 8 shifts for several purposes only pays the scan once).
        /// <paramref name="includePartials"/> also yields 1-sector chains (a driver
        /// past their FIRST split). The broad packet scan uses these for car naming;
        /// a structurally gated results-component scan may also retain them for the
        /// explicit "show splits" display mode.
        /// </summary>
        public static IEnumerable<(string name, double raw, double pen, int sectors)> ResultsIn(
            byte[] payload, List<FStr> fstrs, bool includePartials = false,
            bool strictPartials = false)
        {
            var names = new List<(int end, string name)>();
            foreach (var f in fstrs)
                if (Names.IsPlayerName(f.Text)) names.Add((f.End, f.Text));
            if (names.Count == 0) yield break;

            var seen = new HashSet<string>();
            // last valid anchor: the duplicate float needs P + DupGap + 4 <= Length
            int limit = Math.Max(0, payload.Length - DupGap - 4);
            for (int P = 0; P <= limit; P++)
            {
                var split1 = F32(payload, P);
                var split1Dup = F32(payload, P + DupGap);
                if (split1 == null || split1Dup == null) continue;
                double s1 = split1.Value;
                if (!(TotalMin < s1 && s1 < TotalMax)) continue;
                if (Math.Abs(s1 - split1Dup.Value) > DupTolerance) continue;   // split1 duplicate signature

                var chain = ChainFrom(payload, P);
                if (chain.Count < (includePartials ? 1 : 2)) continue;

                // In the actual RaceEventRallyResults component a completed S1 record
                // carries the zero next-property tag. Other replicated floats can form
                // the same duplicate signature next to a valid name (observed tags 0x19
                // and 0x86). The broad naming scan stays permissive; the component-gated
                // table path opts into this stricter S1 discriminator.
                if (strictPartials && chain.Count == 1
                    && (P + 9 >= payload.Length || payload[P + 9] != 0x00)) continue;

                // Skip ONLY the confirmed live "running time" form: the byte right
                // after the split1 duplicate (P+9) is the next property tag — 0x10
                // for a mid-stage live time, 0x0e for a sector-complete finish. We
                // drop 0x10 and keep everything else, so a genuine finish is never
                // lost. (The per-column max later discards any partial that slips
                // through, since the final is always the largest cumulative split.)
                if (P + 9 < payload.Length && payload[P + 9] == LiveTag) continue;

                int last = P + SplitStride * (chain.Count - 1);
                var penalty = F32(payload, last + PenaltyOffset);
                double pen = (penalty == null || !(0.0 <= penalty.Value && penalty.Value < PenaltyMax))
                    ? 0.0 : penalty.Value;

                (int end, string name)? owner = null;
                foreach (var (nameEnd, name) in names)   // nearest name ending <= P
                    if (nameEnd <= P && P - nameEnd <= BlockMaxGap)
                        if (owner == null || nameEnd > owner.Value.end)
                            owner = (nameEnd, name);

                // dedup: ONE ≥2-sector record per name (first anchor wins — a later
                // anchor in the same packet is a false-positive echo whose bogus
                // chain would corrupt the column's max sector count), plus at most
                // one 1-sector partial (consumers decide whether it is naming-only).
                if (owner != null && seen.Add(owner.Value.name + (chain.Count == 1 ? "|p" : "")))
                    yield return (owner.Value.name, chain[chain.Count - 1], pen, chain.Count);
            }
        }
    }
}
