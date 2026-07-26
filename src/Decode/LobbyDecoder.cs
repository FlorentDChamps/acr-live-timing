using System.Text;
using System.Text.RegularExpressions;

namespace ACRLiveTiming.Decode
{
    /// <summary>
    /// UE 5.4.3 replication decoder — nation + car per driver. Ported 1:1 from
    /// the original protocol-RE prototype. The results-manager replicates, per
    /// driver, an element
    /// FRaceParticipantRallyResultEntry whose last cumulative sector Time equals the
    /// driver's raw total (an exact float32, unique per driver, that ResultScanner
    /// already extracts with the pseudo). We reassemble the (possibly partial) net
    /// bunches into content blocks, locate that float's byte pattern, then read the
    /// first CarId token forward and the first Nationality token after it — binding
    /// nation+car to the exact driver by time, no channel/guid/proximity guess.
    /// Validated ms-exact vs in-game screenshots.
    /// </summary>
    public static class LobbyDecoder
    {
        /// <summary>Stable account identifiers and the mutable display name carried
        /// by a PlayerState identity block.</summary>
        public readonly record struct PlayerIdentity(string SteamId, string EosPuid, string Name);

        const int MAX_CHSEQUENCE = 1024;
        const int MAX_CLOSE_REASON = 15;
        const int MAXPKT_BITS = 1024 * 8;
        const int JITTER_BITS = 12;
        static readonly int[] PREFIXES = { 5, 6, 4, 7, 3, 8 };

        // ---- bit reader ------------------------------------------------------

        sealed class BR
        {
            readonly byte[] _b;
            public int pos;
            readonly int _n;
            public bool err;

            public BR(byte[] b) { _b = b; pos = 0; _n = b.Length * 8; err = false; }

            public int Bit()
            {
                if (pos >= _n) { err = true; return 0; }
                int v = (_b[pos >> 3] >> (pos & 7)) & 1;
                pos++;
                return v;
            }

            public long Bits(int k)
            {
                long v = 0;
                for (int i = 0; i < k; i++) v |= (long)Bit() << i;
                return v;
            }

            public long IntPacked()
            {
                long val = 0; int cnt = 0;
                while (true)
                {
                    long by = Bits(8);
                    val |= (by >> 1) << (7 * cnt); cnt++;
                    if ((by & 1) == 0 || cnt > 5) break;
                }
                return val;
            }

            public long ReadInt(long maxv)
            {
                long val = 0, mask = 1;
                while ((val + mask) < maxv && !err)
                {
                    if (Bit() != 0) val |= mask;
                    mask <<= 1;
                }
                return val;
            }
        }

        static int PayloadEnd(byte[] pl)
        {
            int end = pl.Length * 8;
            while (end > 0 && ((pl[(end - 1) >> 3] >> ((end - 1) & 7)) & 1) == 0) end--;
            return end - 1;
        }

        static int HeaderEnd(byte[] pl, int prefix)
        {
            var br = new BR(pl) { pos = prefix };
            long wc = br.Bits(4) + 1;
            br.Bits(14); br.Bits(14);          // AckedSeq, Seq
            br.Bits(32 * (int)wc);             // ack history
            if (br.Bit() != 0) br.Bits(JITTER_BITS);
            return br.pos;
        }

        // ---- package-map / NetGUID serialization -----------------------------

        static string? RdFString(BR br)
        {
            long ln = br.Bits(32);
            if (ln == 0) return "";
            int[] chars;
            if ((ln & 0x80000000L) != 0)
            {
                long n = 0x100000000L - ln;
                if (n > 1024) { br.err = true; return null; }
                chars = new int[n];
                for (int i = 0; i < n; i++) chars[i] = (int)br.Bits(16);
            }
            else
            {
                if (ln > 1024) { br.err = true; return null; }
                chars = new int[ln];
                for (int i = 0; i < ln; i++) chars[i] = (int)br.Bits(8);
            }
            var sb = new StringBuilder();
            for (int i = 0; i < chars.Length - 1; i++) if (chars[i] != 0) sb.Append((char)chars[i]);
            return sb.ToString();
        }

        [ThreadStatic] static Dictionary<long, string>? _guidCapture;   // NetGUID -> path (feeds BlockStream.Guids)
        [ThreadStatic] static Dictionary<long, long>? _outerCapture;    // NetGUID -> outer NetGUID

        static long InternalLoadObject(BR br, bool exporting, int depth)
        {
            if (depth > 16 || br.err) { br.err = true; return 0; }
            long ng = br.IntPacked();
            if (br.err || ng == 0) return ng;
            long flags = 0;
            if (exporting)
            {
                flags = br.Bits(8);
                if (br.err) return ng;
            }
            if ((flags & 0x1) != 0)                     // bHasPath
            {
                long og = InternalLoadObject(br, exporting, depth + 1);   // outer
                var path = RdFString(br);               // path
                if (path != null && path.Length > 0)
                {
                    if (_guidCapture != null) _guidCapture[ng] = path;
                    // outer chain: lets a consumer group subobjects of the SAME actor
                    // (e.g. the per-player data components share their owner's guid)
                    if (_outerCapture != null && og != 0) _outerCapture[ng] = og;
                }
                if ((flags & 0x4) != 0) br.Bits(32);    // bHasNetworkChecksum
            }
            return ng;
        }

        static bool RecvNetguidBunch(BR br)
        {
            if (br.Bit() != 0) return false;            // bHasRepLayoutExport -> different path
            long num = br.Bits(32);
            if (br.err || num > 2048) { br.err = true; return false; }
            for (long i = 0; i < num; i++)
            {
                InternalLoadObject(br, true, 0);
                if (br.err) return false;
            }
            return true;
        }

        static string? ReadName(BR br)
        {
            if (br.Bit() != 0) { br.IntPacked(); return "Actor"; }
            var s = RdFString(br);
            br.Bits(32);
            return s;
        }

        static bool ReadSpawnTransform(BR br)
        {
            for (int i = 0; i < 4; i++) if (br.Bit() != 0) return false;
            return !br.err;
        }

        // ---- bunch parsing ---------------------------------------------------

        sealed class Rec
        {
            public int ch, open, close, exports, mustmap, partial, pinit, pfin, pstart, nbits;
            public string? chname;
        }

        static List<Rec>? TryParse(byte[] pl, int start)
        {
            var br = new BR(pl) { pos = start };
            int end = PayloadEnd(pl);
            var outl = new List<Rec>();
            int guard = 0;
            while (br.pos < end - 8 && !br.err)
            {
                guard++;
                if (guard > 128) return null;
                int bControl = br.Bit();
                int bOpen = bControl != 0 ? br.Bit() : 0;
                int bClose = bControl != 0 ? br.Bit() : 0;
                if (bClose != 0) br.ReadInt(MAX_CLOSE_REASON);
                br.Bit();                               // bIsReplicationPaused
                int bReliable = br.Bit();
                long chindex = br.IntPacked();
                int bHasExports = br.Bit();
                int bHasMustMapped = br.Bit();
                int bPartial = br.Bit();
                if (bReliable != 0) br.ReadInt(MAX_CHSEQUENCE);
                int pInit = 0, pFin = 0;
                if (bPartial != 0) { pInit = br.Bit(); pFin = br.Bit(); }
                string? chname = null;
                if (bReliable != 0 || bOpen != 0) chname = ReadName(br);
                long nbits = br.ReadInt(MAXPKT_BITS);
                int pstart = br.pos;
                if (br.err || pstart + nbits > end + 1 || chindex > 32767) return null;
                outl.Add(new Rec
                {
                    ch = (int)chindex, open = bOpen, close = bClose, exports = bHasExports,
                    mustmap = bHasMustMapped, partial = bPartial, pinit = pInit,
                    pfin = pFin, chname = chname, pstart = pstart, nbits = (int)nbits
                });
                br.pos = pstart + (int)nbits;
            }
            bool tiled = Math.Abs(br.pos - end) <= 8 && outl.Count > 0;
            return tiled ? outl : null;
        }

        static List<Rec> ParsePacket(byte[] pl)
        {
            foreach (int pref in PREFIXES)
            {
                var res = TryParse(pl, HeaderEnd(pl, pref));
                if (res != null) return res;
            }
            return new List<Rec>();
        }

        // ---- partial-bunch reassembly + content blocks -----------------------

        static List<byte> PullBits(byte[] pl, int startbit, int nbits)
        {
            var outl = new List<byte>(nbits);
            for (int i = 0; i < nbits; i++)
                outl.Add((byte)((pl[(startbit + i) >> 3] >> ((startbit + i) & 7)) & 1));
            return outl;
        }

        static byte[] PackBits(List<byte> bits)
        {
            var outb = new byte[(bits.Count + 7) >> 3];
            for (int i = 0; i < bits.Count; i++)
                if (bits[i] != 0) outb[i >> 3] |= (byte)(1 << (i & 7));
            return outb;
        }

        static List<(int ps, int npb, long guid)> ContentBlocks(BR br, int endbit)
        {
            var outl = new List<(int, int, long)>();
            int guard = 0;
            while (br.pos < endbit - 8 && !br.err)
            {
                guard++;
                if (guard > 128) break;
                br.Bit();                               // bHasRepLayout
                long guid = 0;                          // subobject NetGUID (0 = actor-level)
                if (br.Bit() == 0)                      // bIsActor == 0 -> subobject
                {
                    guid = InternalLoadObject(br, false, 0);   // subobject guid
                    if (br.err) break;
                    if (br.Bit() == 0)                  // bStablyNamed == 0
                    {
                        if (br.Bit() != 0) br.Bits(8);  // bIsDestroyMessage
                        else InternalLoadObject(br, false, 0);   // class guid
                    }
                }
                if (br.err) break;
                long npb = br.IntPacked();
                if (npb <= 0 || npb > (endbit - br.pos)) break;
                outl.Add((br.pos, (int)npb, guid));
                br.pos += (int)npb;
            }
            return outl;
        }

        static byte[] Slice(byte[] m, int a, int b)
        {
            a = Math.Max(0, a); b = Math.Min(m.Length, b);
            if (b <= a) return Array.Empty<byte>();
            var r = new byte[b - a];
            Array.Copy(m, a, r, 0, b - a);
            return r;
        }

        // ---- player identity (steamid+pseudo) in a PlayerState bunch -----------

        // The identity block replicates as two consecutive FStrings:
        // "<steamid64>_+_|<32-hex EOS puid>" then the EOS display name. The marker
        // "_+_|" preceded by >=6 digits and followed by exactly 32 hex + NUL + a sane
        // FString length prefix cannot occur by chance, so a hit is a certain bind.
        // Scanned at all 8 bit shifts (the block usually lands mid-bitstream). The
        // display-name FString is parsed properly (ANSI or UTF-16), so exotic
        // pseudonyms (accents, CJK) come out intact — unlike an ASCII token scan.
        static PlayerIdentity? IdentIn(byte[] seg)
        {
            for (int sh = 0; sh < 8; sh++)
            {
                var d = sh == 0 ? seg : Primitives.Shr(seg, sh);
                for (int i = 6; i + 40 < d.Length; i++)
                {
                    if (d[i] != (byte)'_' || d[i + 1] != (byte)'+' || d[i + 2] != (byte)'_' || d[i + 3] != (byte)'|')
                        continue;
                    int j = i;                               // backtrack the steamid digits
                    while (j > 0 && d[j - 1] >= (byte)'0' && d[j - 1] <= (byte)'9') j--;
                    if (i - j < 6) continue;
                    int h = i + 4;                           // 32 hex chars then NUL
                    if (h + 33 > d.Length) continue;
                    bool okHex = true;
                    for (int k = 0; k < 32 && okHex; k++)
                    {
                        byte c = d[h + k];
                        okHex = (c >= (byte)'0' && c <= (byte)'9') || (c >= (byte)'a' && c <= (byte)'f');
                    }
                    if (!okHex || d[h + 32] != 0) continue;
                    // the display-name FString follows the id, separated by a small
                    // tag (1 byte observed) — probe a short window rather than assume
                    // byte-adjacency; ReadFStringAt is strict enough to reject junk
                    for (int o = 0; o <= 8; o++)
                    {
                        var name = ReadFStringAt(d, h + 33 + o);
                        if (name != null)
                            return new PlayerIdentity(
                                Encoding.ASCII.GetString(d, j, i - j),
                                Encoding.ASCII.GetString(d, h, 32),
                                name);
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Earliest valid player-name FString in a content block, across the 8 bit
        /// shifts (ANSI or UTF-16). Used on the ACTOR-level block of a
        /// BC_RaceParticipant channel, whose only string property is the participant
        /// ID FName — which IS the player's display name. Guards: blocks bigger than
        /// ~96 bytes are NOT the participant actor block (a stale channel→actor
        /// binding can mis-route a big roster/GameState block here), and nation /
        /// car / tyre vocabulary tokens are never IDs (they'd bind a crew default
        /// like "Lebertre" or a nation as a driver).
        /// </summary>
        public static string? FirstPlayerNameIn(byte[] seg)
        {
            if (seg.Length > 96) return null;
            string? best = null;
            int bestPos = int.MaxValue;
            for (int sh = 0; sh < 8; sh++)
            {
                var d = sh == 0 ? seg : Primitives.Shr(seg, sh);
                for (int i = 0; i + 6 < d.Length && i * 8 + sh < bestPos; i++)
                {
                    var s = ReadFStringAt(d, i);
                    if (s == null || !Names.IsPlayerName(s)) continue;
                    if (Nations.Contains(s) || TyreRe.IsMatch(s)
                        || (s.Length >= 10 && CarRe.IsMatch(s))) continue;
                    int pos = i * 8 + sh;
                    if (pos < bestPos) { bestPos = pos; best = s; }
                }
            }
            return best;
        }

        // Read one wire FString at a byte offset: int32 len; len>0 = ANSI (len bytes
        // incl NUL), len<0 = UTF-16LE (-len chars incl NUL). Null on any malformation.
        static string? ReadFStringAt(byte[] d, int off)
        {
            if (off + 4 > d.Length) return null;
            int len = d[off] | (d[off + 1] << 8) | (d[off + 2] << 16) | (d[off + 3] << 24);
            int p = off + 4;
            if (len > 1 && len <= 64)
            {
                if (p + len > d.Length || d[p + len - 1] != 0) return null;
                var sb = new StringBuilder(len - 1);
                for (int k = 0; k < len - 1; k++)
                {
                    if (d[p + k] < 0x20 || d[p + k] >= 0x7f) return null;
                    sb.Append((char)d[p + k]);
                }
                return sb.Length >= 2 ? sb.ToString() : null;
            }
            if (len < -1 && len >= -64)
            {
                int n = -len;
                if (p + 2 * n > d.Length || d[p + 2 * n - 2] != 0 || d[p + 2 * n - 1] != 0) return null;
                var sb = new StringBuilder(n - 1);
                for (int k = 0; k < n - 1; k++)
                {
                    int c = d[p + 2 * k] | (d[p + 2 * k + 1] << 8);
                    if (c < 0x20) return null;
                    sb.Append((char)c);
                }
                return sb.Length >= 2 ? sb.ToString() : null;
            }
            return null;
        }

        sealed class Pending { public required Rec First; public required List<byte> Bits; }

        /// <summary>
        /// Streaming bunch reassembler: feed packets one at a time and get that
        /// packet's reassembled content blocks, each tagged with its subobject NetGUID
        /// and start bit offset (so per-object replicated property streams can be
        /// parsed). Partial-bunch reassembly state and the NetGUID→path map persist
        /// across <see cref="Feed"/> calls, so a decoder can process a live stream
        /// incrementally instead of re-parsing the whole payload buffer on every pass.
        /// Not thread-safe: drive one stream from a single thread.
        /// </summary>
        public sealed class BlockStream
        {
            readonly Dictionary<int, Pending> _pending = new();
            readonly Dictionary<long, string> _fresh = new();         // this packet's exports
            readonly Dictionary<long, long> _freshOuter = new();      // this packet's outer links
            readonly Dictionary<int, long> _chActor = new();          // channel -> actor guid (from open bunches)
            readonly HashSet<long> _psActors = new();                 // actors archetyped BC_RacePlayerState
            readonly Dictionary<long, PlayerIdentity> _identities = new(); // last identity seen per ps actor
            public Dictionary<long, string> Guids { get; } = new();   // NetGUID -> path

            /// <summary>Actors archetyped BC_RaceParticipant — ONE per player, created
            /// at join and PERSISTENT across stages (unlike the per-stage PlayerState).
            /// Its actor-level block carries the participant ID (= the player's display
            /// name, an FName string) and its RaceParticipantData component carries
            /// nation / car / crew — flag+car at JOIN, no lap time needed.</summary>
            public HashSet<long> ParticipantActors { get; } = new();

            /// <summary>Player identities resolved by the LAST <see cref="Feed"/> call:
            /// (PlayerState actor guid, stable account identifiers and EOS display name), from the steamid+pseudo
            /// identity block on the actor's own channel. The PlayerState actor is the
            /// OUTER of the per-player data components (RaceStateData etc, see
            /// <see cref="Outers"/>), and it is respawned per stage with the identity
            /// re-replicated in its open bunch — so every stage's fresh actors are
            /// re-identified at spawn, BEFORE the start. Consume before the next Feed.</summary>
            public List<(long actor, PlayerIdentity identity)> NewIdents { get; } = new();

            /// <summary>NetGUID -> outer NetGUID, from the export chain. Two subobjects
            /// with the same outer belong to the same actor (e.g. a player's
            /// RaceStateData and RaceSectorsPlayerData components).</summary>
            public Dictionary<long, long> Outers { get; } = new();

            /// <summary>Guid→path exports first seen in the LAST <see cref="Feed"/> call
            /// (a guid is exported once, then referenced by number). Lets a consumer
            /// track guids of interest incrementally instead of re-scanning
            /// <see cref="Guids"/> every packet. Reused across calls — consume before
            /// the next Feed.</summary>
            public List<(long guid, string path)> NewGuids { get; } = new();

            public List<(long guid, long actor, byte[] block, int bitoff)> Feed(byte[] pl)
            {
                NewGuids.Clear();
                NewIdents.Clear();
                var outl = new List<(long, long, byte[], int)>();
                if (pl.Length < 12) return outl;
                _fresh.Clear();
                _freshOuter.Clear();
                var prev = _guidCapture;
                var prevOuter = _outerCapture;
                _guidCapture = _fresh;
                _outerCapture = _freshOuter;
                try
                {
                    foreach (var rec in ParsePacket(pl))
                    {
                        int ch = rec.ch;
                        if (rec.partial == 0)
                        {
                            foreach (var blk in Handle(rec, PullBits(pl, rec.pstart, rec.nbits)))
                                outl.Add(blk);
                        }
                        else
                        {
                            if (rec.pinit != 0)
                                _pending[ch] = new Pending { First = rec, Bits = PullBits(pl, rec.pstart, rec.nbits) };
                            else if (_pending.TryGetValue(ch, out var p))
                                p.Bits.AddRange(PullBits(pl, rec.pstart, rec.nbits));
                            if (rec.pfin != 0 && _pending.TryGetValue(ch, out var fin))
                            {
                                _pending.Remove(ch);
                                foreach (var blk in Handle(fin.First, fin.Bits))
                                    outl.Add(blk);
                            }
                        }
                        // a closed channel index is reused for a different actor later —
                        // drop the binding so a stale actor can't claim the next tenant
                        if (rec.close != 0) { _chActor.Remove(ch); _pending.Remove(ch); }
                    }
                }
                finally { _guidCapture = prev; _outerCapture = prevOuter; }
                // fold this packet's exports into the persistent map, keeping the first
                // path per guid (a re-export carries the same path).
                foreach (var kv in _fresh)
                    if (!Guids.ContainsKey(kv.Key))
                    {
                        Guids[kv.Key] = kv.Value;
                        NewGuids.Add((kv.Key, kv.Value));
                    }
                foreach (var kv in _freshOuter)
                    if (!Outers.ContainsKey(kv.Key)) Outers[kv.Key] = kv.Value;
                return outl;
            }

            IEnumerable<(long guid, long actor, byte[] block, int bitoff)> Handle(Rec first, List<byte> bits)
            {
                byte[] merged = PackBits(bits);
                int total = bits.Count;
                var br = new BR(merged);
                bool bail = false;
                if (first.exports != 0 && !RecvNetguidBunch(br)) bail = true;
                if (!bail && first.mustmap != 0)
                {
                    long nmm = br.Bits(16);
                    if (nmm > 4096) bail = true;
                    else for (long i = 0; i < nmm; i++) br.IntPacked();
                }
                if (!bail && first.open != 0)
                {
                    long a = InternalLoadObject(br, false, 0);
                    if (a != 0 && (a & 1) == 0)
                    {
                        long arch = InternalLoadObject(br, false, 0);   // archetype
                        InternalLoadObject(br, false, 0);               // level
                        _chActor[first.ch] = a;
                        // the archetype's path was exported in this very bunch (_fresh)
                        // or in an earlier packet (Guids); it names the actor's class
                        string? ap = _fresh.TryGetValue(arch, out var p0) ? p0
                                   : Guids.TryGetValue(arch, out var p1) ? p1 : null;
                        if (ap != null && ap.Contains("RacePlayerState")) _psActors.Add(a);
                        else if (ap != null && ap.Contains("BC_RaceParticipant")) ParticipantActors.Add(a);
                        if (!ReadSpawnTransform(br)) bail = true;
                    }
                    else if (a != 0) _chActor[first.ch] = a;
                }
                if (!br.err && !bail)
                {
                    long bunchActor = _chActor.GetValueOrDefault(first.ch);
                    foreach (var (ps, npb, guid) in ContentBlocks(br, total))
                        yield return (guid, bunchActor, Slice(merged, ps >> 3, (ps + npb + 7) >> 3), ps & 7);
                }

                // identity scan on an actor's channel — the identity block replicates in
                // the actor's OPEN bunch (respawned each stage), so this usually hits
                // exactly once per actor, at spawn. NOT gated on the BC_RacePlayerState
                // archetype tag: a game update can stop exporting that archetype path
                // (seen 2026-07-11: the class GUID no longer resolves, _psActors stays
                // empty) while the identity block itself is unchanged in the stream — so
                // gate on the OPEN bunch (cheap, once per actor) OR the archetype tag when
                // it IS available. Once an actor is identified, scan its later bunches
                // too: a changed pseudo replicates there with the same account IDs.
                // IdentIn's marker is a certain bind (near-zero false positives), and a
                // hit on a non-player actor never binds downstream (only RaceStateData
                // outers are named), so scanning every open is safe.
                if (_chActor.TryGetValue(first.ch, out var owner)
                    && (first.open != 0 || _psActors.Contains(owner) || _identities.ContainsKey(owner)))
                {
                    var identity = IdentIn(merged);
                    if (identity != null
                        && (!_identities.TryGetValue(owner, out var current) || current != identity.Value))
                    {
                        _identities[owner] = identity.Value;
                        NewIdents.Add((owner, identity.Value));
                    }
                }
            }
        }

        // ---- nation + car extraction -----------------------------------------

        // The COMPLETE game nationality vocabulary, extracted from the ACR asset names
        // `/Game/Data/UITextures/NationalityFlags/T_<Country>` in the UE4SS object dump —
        // the game's own list, so no country is missed and spellings match the wire
        // (including the game's own typos: "Irleand", "Zimbawe", "Kazakhastan", "Camerun").
        // "Other" is the game's unknown-nationality value. This is a CLASSIFIER (tells a
        // nationality FName apart from a name/place token in the byte stream), not a label
        // map. Regenerate with: grep -oE 'NationalityFlags/T_[A-Za-z]+' dump | sed 's|.*T_||'
        static readonly HashSet<string> Nations = new()
        {
            "Afghanistan", "Albania", "Algeria", "Andorra", "Angola", "AntiguaAndBarbuda",
            "Argentina", "Armenia", "Australia", "Austria", "Azerbaijan", "Bahamas", "Bahrain",
            "Bangladesh", "Barbados", "Belarus", "Belgium", "Belize", "Benin", "Bolivia",
            "BosniaHerzegovina", "Botswana", "Brazil", "Brunei", "Bulgaria", "BurkinaFaso",
            "Burundi", "Cambodia", "Camerun", "Canada", "CapoVerde", "CentralAfricanRepublic",
            "Chad", "Chile", "China", "Colombia", "Comoros", "CostaRica", "Croatia", "Cuba",
            "Cyprus", "CzechRepublic", "DemocraticRepublicOfCongo", "Denmark", "Djibouti",
            "Dominica", "DominicanRepublic", "Ecuador", "Egypt", "ElSalvador", "EquatorialGuinea",
            "Eritrea", "Estonia", "Eswatini", "Ethiopia", "Fiji", "Finland", "France", "Gabon",
            "Gambia", "Georgia", "Germany", "Ghana", "Greece", "Grenada", "Guatemala", "Guinea",
            "GuineaBissau", "Guyana", "Haiti", "Honduras", "HongKong", "Hungary", "Iceland",
            "India", "Indonesia", "Iran", "Iraq", "Irleand", "Israel", "Italy", "Jamaica", "Japan",
            "Jordan", "Kazakhastan", "Kenya", "Kiribati", "Kuwait", "Kyrgyzstan", "Laos", "Latvia",
            "Lebanon", "Lesotho", "Liberia", "Libya", "Lichtenstein", "Lithuania", "Luxembourg",
            "Macau", "Madagascar", "Malawi", "Malaysia", "Maldives", "Mali", "Malta", "Mauritania",
            "Mauritius", "Mexico", "Micronesia", "Moldova", "Monaco", "Mongolia", "Montenegro",
            "Morocco", "Mozambique", "Myanmar", "Namibia", "Nauru", "Nepal", "Netherlands",
            "NewZealand", "Nicaragua", "Niger", "Nigeria", "NorthKorea", "NorthMacedonia", "Norway",
            "Oman", "Other", "Pakistan", "Palau", "Panama", "PapaNewGuinea", "Paraguay", "Peru",
            "Philippines", "Poland", "Portugal", "Qatar", "RepublicOfCongo", "Romania", "Russia",
            "Rwanda", "SaintKittsAndNevis", "SaintLucia", "SaintVincentAndGrenadines", "Samoa",
            "SanMarino", "SaoTomeAndPrincipe", "SaudiArabia", "Senegal", "Serbia", "Seychelles",
            "SierraLeone", "Singapore", "Slovakia", "Slovenia", "SolomonIslands", "Somalia",
            "SouthAfrica", "SouthKorea", "SouthSudan", "Spain", "SriLanka", "Sudan", "Suriname",
            "Sweden", "Switzerland", "Syria", "TaipeiChina", "Tajikistan", "Tanzania", "Thailand",
            "TimorLeste", "Togo", "Tonga", "TrinidadAndTobago", "Tunisia", "Turkey", "Turkmenistan",
            "Tuvalu", "Uganda", "Ukraine", "UnitedArabEmirates", "UnitedKingdom", "UnitedStates",
            "Uruguay", "Uzbekistan", "Vanuatu", "Vatican", "Venezuela", "Vietnam", "Wales", "Yemen",
            "Zambia", "Zimbawe",
        };

        /// <summary>True for one of the game's nationality FNames. Result blocks also
        /// contain these strings; callers which expose a name before the complete
        /// result is available must exclude them explicitly.</summary>
        public static bool IsNation(string value) => Nations.Contains(value);

        // car model token: a multi-word CamelCase name (a first Capitalised word, then at
        // least one more Upper/digit-led chunk) — SkodaFabiaRSRally2, CitroenXsaraWRC,
        // Peugeot306IIMaxiKitCar, LanciaDeltaIntegraleEvo, Fiat131Abarth. No brand/suffix
        // list: the CarId is simply the FIRST such token after the time, read raw from the
        // wire. A len>=10 guard rejects short CamelCase bit-shift noise / name tokens.
        static readonly Regex CarRe = new(
            @"^[A-Z][a-z]+(?:[A-Z0-9][A-Za-z0-9]*)+$", RegexOptions.Compiled);

        // tyre compound FNames (GravelSoft, TarmacHard, …) are CamelCase too and sit in
        // the SAME participant component (TiresAllocation) as the CarId — and tyre
        // changes re-replicate often, so without this guard they'd out-vote the car.
        // No car brand starts with a surface word.
        static readonly Regex TyreRe = new(
            @"^(Gravel|Tarmac|Snow|Wet|Ice|Asphalt|Mud)", RegexOptions.Compiled);

        static IEnumerable<(int start, string text)> Tokens(byte[] d)
        {
            int i = 0, n = d.Length;
            while (i < n)
            {
                if (d[i] >= 0x20 && d[i] < 0x7f)
                {
                    int j = i;
                    while (j < n && d[j] >= 0x20 && d[j] < 0x7f) j++;
                    if (j - i >= 2)
                    {
                        var sb = new StringBuilder(j - i);
                        for (int k = i; k < j; k++) sb.Append((char)d[k]);
                        yield return (i, sb.ToString());
                    }
                    i = j;
                }
                else i++;
            }
        }

        static void Bump(Dictionary<string, int> c, string k)
            => c[k] = c.TryGetValue(k, out var v) ? v + 1 : 1;

        static string? Top(Dictionary<string, int> c)
            => c.Count == 0 ? null : c.OrderByDescending(kv => kv.Value).First().Key;

        /// <summary>
        /// Car / nation token marks in a content block, across all 8 bit-shifts,
        /// positions in absolute BITS, sorted ascending. A block with no marks is
        /// uninteresting for the nation/car join.
        /// </summary>
        public static (List<(int a, string t)> cars, List<(int a, string t)> nations) MarksIn(byte[] seg)
        {
            var cmarks = new List<(int a, string t)>();
            var nmarks = new List<(int a, string t)>();
            for (int shn = 0; shn < 8; shn++)
            {
                var d = shn == 0 ? seg : Primitives.Shr(seg, shn);
                foreach (var (st, t) in Tokens(d))
                {
                    int ab = st * 8 + shn;
                    if (Nations.Contains(t)) nmarks.Add((ab, t));
                    else if (t.Length >= 10 && CarRe.IsMatch(t) && !TyreRe.IsMatch(t))
                    {
                        // strip a trailing Set* suffix; a token STARTING with "Set"
                        // (e.g. SetupGravelDefault) would leave "" — an empty mark
                        // positioned early would win the join vote, skip it
                        var car = t.Split("Set")[0];
                        if (car.Length >= 2) cmarks.Add((ab, car));
                    }
                }
            }
            cmarks.Sort((x, y) => x.a.CompareTo(y.a));
            nmarks.Sort((x, y) => x.a.CompareTo(y.a));
            return (cmarks, nmarks);
        }

        /// <summary>
        /// Match a block's marks against the time->pseudo map and vote. Shared by the
        /// batch extractor and the streaming tracker so the join logic can't drift.
        /// </summary>
        public static void JoinBlock(
            byte[] seg,
            List<(int a, string t)> cmarks, List<(int a, string t)> nmarks,
            Dictionary<uint, string> pats,
            Dictionary<string, Dictionary<string, int>> joinNat,
            Dictionary<string, Dictionary<string, int>> joinCar)
        {
            for (int shn = 0; shn < 8; shn++)
            {
                var d = shn == 0 ? seg : Primitives.Shr(seg, shn);
                for (int i = 0; i < d.Length - 3; i++)
                {
                    uint key = (uint)(d[i] | (d[i + 1] << 8) | (d[i + 2] << 16) | (d[i + 3] << 24));
                    if (!pats.TryGetValue(key, out var nm)) continue;
                    int pos = i * 8 + shn;

                    int carpos = pos;
                    string? car = null;
                    foreach (var (a, t) in cmarks)
                        if (-40 <= a - pos && a - pos < 800) { car = t; carpos = a; break; }

                    // Nation is bound ONLY through the driver's OWN participant element,
                    // anchored by the car token next to their time float. Without a car
                    // anchor the float is a bare coincidence inside a results-summary
                    // block (every driver's cumulative times land there) — searching for
                    // a nation from that position bleeds in a NEIGHBOUR's flag. So no
                    // car => no nation vote (a driver whose nationality never replicated
                    // stays unknown rather than being painted with someone else's flag).
                    if (car == null) continue;

                    if (!joinCar.TryGetValue(nm, out var cc)) joinCar[nm] = cc = new();
                    Bump(cc, car);

                    // Driver.Nationality is the FIRST nation after the car — but the
                    // Name/Surname fields sit between them, and for exotic (UTF-16)
                    // pseudonyms those are twice as long, pushing the nation far out.
                    // So bound the search by the NEXT car token (= next driver's
                    // element) instead of a fixed width: adapts to element length,
                    // still can't bleed into the next driver. Capped at 1800 bits.
                    int nextCar = carpos + 1800;
                    foreach (var (a, _) in cmarks)
                        if (a > carpos + 20) { nextCar = a; break; }   // cmarks sorted asc
                    int bound = Math.Min(nextCar, carpos + 1800);
                    string? nat = null;
                    foreach (var (a, t) in nmarks)
                        if (carpos - 20 <= a && a < bound) { nat = t; break; }

                    if (nat != null)
                    {
                        if (!joinNat.TryGetValue(nm, out var nc)) joinNat[nm] = nc = new();
                        Bump(nc, nat);
                    }
                }
            }
        }

        /// <summary>Majority vote per driver -> (nation, car); drivers with no votes omitted.</summary>
        public static Dictionary<string, (string? nation, string? car)> ResolveVotes(
            IEnumerable<string> drivers,
            Dictionary<string, Dictionary<string, int>> joinNat,
            Dictionary<string, Dictionary<string, int>> joinCar)
        {
            var result = new Dictionary<string, (string?, string?)>();
            foreach (var nm in drivers)
            {
                joinNat.TryGetValue(nm, out var nc);
                joinCar.TryGetValue(nm, out var cc);
                string? nation = nc != null ? Top(nc) : null;
                string? car = cc != null ? Top(cc) : null;
                if (nation != null || car != null) result[nm] = (nation, car);
            }
            return result;
        }
    }
}
