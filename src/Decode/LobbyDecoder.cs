using System.Text;

namespace ACRLiveTiming.Decode
{
    /// <summary>
    /// Unreal Engine net-packet parser (written against UE 5.4.3; UE 5.6.1 / ACR 0.6
    /// adds one partial-bunch header flag, auto-detected per stream): packet header,
    /// bunch headers, NetGUID exports, partial-bunch reassembly and the content blocks
    /// of each bunch, tagged with the object (subobject NetGUID or actor) they belong
    /// to. <see cref="BlockStream"/> is the streaming form; the property payload of a
    /// block is read by <see cref="RepLayout"/>.
    /// </summary>
    public static class LobbyDecoder
    {
        const int MAX_CHSEQUENCE = 1024;
        const int MAX_CLOSE_REASON = 15;
        const int MAXPKT_BITS = 1024 * 8;
        // Packet header: 6 prefix bits (handshake flag + session/client ids), then the
        // FNetPacketNotify header (4-bit history word count, two 14-bit sequences, the
        // ack history words), then the packet-info flag followed, when set, by 11 bits
        // of packet info (jitter clock). Prefix 6 is the layout of every ACR build so
        // far; the other prefixes are tried only when it does not tile (5 differs from 6
        // only on packets carrying packet info, and used to be tried first).
        const int JITTER_BITS = 11;
        static readonly int[] PREFIXES = { 6, 5, 4, 7, 3, 8 };

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
            if (br.Bit() != 0) br.Bits(JITTER_BITS);   // bHasPacketInfoPayload + packet info
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
            public int seq = -1;          // ChSequence of a reliable bunch (-1: unreliable)
            public string? chname;
        }

        // UE 5.4 serialises two partial-bunch flags (bPartialInitial, bPartialFinal);
        // UE 5.5+ inserts bPartialCustomExportsFinal between them. The layout is not
        // announced on the wire, so a stream tries its preferred width first and flips
        // after the other one tiles a few packets in a row (see ParsePacket).
        const int PartialBitsUe54 = 2;
        const int PartialBitsUe55 = 3;

        static List<Rec>? TryParse(byte[] pl, int start, int partialBits)
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
                int seq = bReliable != 0 ? (int)br.ReadInt(MAX_CHSEQUENCE) : -1;
                int pInit = 0, pFin = 0;
                if (bPartial != 0)
                {
                    pInit = br.Bit();
                    if (partialBits == PartialBitsUe55) br.Bit();   // bPartialCustomExportsFinal
                    pFin = br.Bit();
                }
                string? chname = null;
                if (bReliable != 0 || bOpen != 0) chname = ReadName(br);
                long nbits = br.ReadInt(MAXPKT_BITS);
                int pstart = br.pos;
                if (br.err || pstart + nbits > end + 1 || chindex > 32767) return null;
                outl.Add(new Rec
                {
                    ch = (int)chindex, open = bOpen, close = bClose, exports = bHasExports,
                    mustmap = bHasMustMapped, partial = bPartial, pinit = pInit,
                    pfin = pFin, seq = seq, chname = chname, pstart = pstart, nbits = (int)nbits
                });
                br.pos = pstart + (int)nbits;
            }
            bool tiled = Math.Abs(br.pos - end) <= 8 && outl.Count > 0;
            return tiled ? outl : null;
        }

        /// <summary>Parse one packet's bunch headers, preferring the partial-flag
        /// layout that tiled the previous packets; a packet that only tiles under the
        /// other layout is still used, and three such packets in a row flip the
        /// preference (an occasional accidental tiling never flips it).</summary>
        static List<Rec> ParsePacket(byte[] pl, ref int partialBits, ref int altWins)
        {
            foreach (int pref in PREFIXES)
            {
                var res = TryParse(pl, HeaderEnd(pl, pref), partialBits);
                if (res != null) { altWins = 0; return res; }
            }
            int alt = partialBits == PartialBitsUe54 ? PartialBitsUe55 : PartialBitsUe54;
            foreach (int pref in PREFIXES)
            {
                var res = TryParse(pl, HeaderEnd(pl, pref), alt);
                if (res != null)
                {
                    if (++altWins >= 3) { partialBits = alt; altWins = 0; }
                    return res;
                }
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

        static List<(int ps, int npb, long guid, bool hasRepLayout)> ContentBlocks(BR br, int endbit)
        {
            var outl = new List<(int, int, long, bool)>();
            int guard = 0;
            while (br.pos < endbit - 8 && !br.err)
            {
                guard++;
                if (guard > 128) break;
                bool hasRepLayout = br.Bit() != 0;
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
                outl.Add((br.pos, (int)npb, guid, hasRepLayout));
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
            /// <summary>A reassembled Unreal content block. <see cref="Data"/> includes
            /// the two boundary bytes needed to preserve a non-byte-aligned payload;
            /// <see cref="BitOffset"/> and <see cref="BitLength"/> identify its exact
            /// bit range. <see cref="HasRepLayout"/> is the content-block header flag
            /// that selects property-handle replication.</summary>
            public readonly record struct ContentBlock(
                long Guid,
                long Actor,
                byte[] Data,
                int BitOffset,
                int BitLength,
                bool HasRepLayout)
            {
                public void Deconstruct(out long guid, out long actor, out byte[] data, out int bitOffset)
                    => (guid, actor, data, bitOffset) = (Guid, Actor, Data, BitOffset);
            }

            readonly Dictionary<int, Pending> _pending = new();
            readonly Dictionary<int, int> _lastSeq = new();           // channel -> last reliable ChSequence seen
            int _partialBits = PartialBitsUe54;                       // bunch-header layout in use
            int _altWins;                                             // consecutive packets tiling only under the other layout
            readonly Dictionary<long, string> _fresh = new();         // this packet's exports
            readonly Dictionary<long, long> _freshOuter = new();      // this packet's outer links
            readonly Dictionary<int, long> _chActor = new();          // channel -> actor guid (from open bunches)
            public Dictionary<long, string> Guids { get; } = new();   // NetGUID -> path

            /// <summary>Actor NetGUID -> archetype path exported with its spawn
            /// ("Default__BC_RaceGameState_C"): names the actor's class, so its
            /// actor-level property blocks can be decoded against a layout.</summary>
            public Dictionary<long, string> ActorArchetypes { get; } = new();

            /// <summary>Actors whose channel opened in the LAST Feed: (actor guid,
            /// archetype guid, archetype path if known). Consume before the next Feed.</summary>
            public List<(long actor, long archetype, string? path)> OpenedActors { get; } = new();

            /// <summary>Diagnostic hook: one line per reassembly decision (null = off).</summary>
            public Action<string>? Trace { get; set; }

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

            public List<ContentBlock> Feed(byte[] pl)
            {
                NewGuids.Clear();
                OpenedActors.Clear();
                var outl = new List<ContentBlock>();
                if (pl.Length < 12) return outl;
                _fresh.Clear();
                _freshOuter.Clear();
                var prev = _guidCapture;
                var prevOuter = _outerCapture;
                _guidCapture = _fresh;
                _outerCapture = _freshOuter;
                try
                {
                    foreach (var rec in ParsePacket(pl, ref _partialBits, ref _altWins))
                    {
                        int ch = rec.ch;
                        // A reliable bunch the server retransmitted (its ack was lost) is
                        // seen twice by a passive capture; the receiver keeps one copy by
                        // ChSequence — so must the reassembly, or the content doubles up.
                        if (rec.seq >= 0)
                        {
                            if (_lastSeq.TryGetValue(ch, out var last) && rec.seq == last)
                            {
                                Trace?.Invoke($"ch{ch} seq{rec.seq} duplicate dropped");
                                continue;
                            }
                            _lastSeq[ch] = rec.seq;
                        }
                        Trace?.Invoke($"ch{ch} seq{rec.seq} open={rec.open} close={rec.close} exp={rec.exports} part={rec.partial}/{rec.pinit}{rec.pfin} nbits={rec.nbits}");
                        List<byte>? bits = PullBits(pl, rec.pstart, rec.nbits);
                        if (rec.exports != 0)
                        {
                            // NetGUID exports are consumed per RAW bunch (UNetConnection::
                            // ReceivedRawBunch), before any partial reassembly: a partial
                            // bunch flagged with exports carries exports only and adds no
                            // content, while a whole bunch continues with its content
                            // right after them. UE 5.5+ may spread the exports of one
                            // reassembled bunch over several partial bunches, each with
                            // its own export header — parsing them one bunch at a time
                            // handles both engines.
                            var ebr = new BR(PackBits(bits));
                            bool ok = RecvNetguidBunch(ebr);
                            if (rec.partial != 0) bits = new List<byte>();
                            else if (ok && ebr.pos <= bits.Count) bits = bits.GetRange(ebr.pos, bits.Count - ebr.pos);
                            else bits = null;    // NetFieldExports or a malformed export list: no content to read
                        }
                        if (bits == null) { /* skip content */ }
                        else if (rec.partial == 0)
                        {
                            foreach (var blk in Handle(rec, bits))
                                outl.Add(blk);
                        }
                        else
                        {
                            // UE 5.5+ splits a big bunch into an exports-only partial run and a
                            // content run that starts with its OWN bPartialInitial: a second
                            // initial on a channel whose pending run holds no content yet
                            // continues that run (keeping the open/must-map flags of the first).
                            if (rec.pinit != 0 && _pending.TryGetValue(ch, out var run) && run.Bits.Count == 0)
                            {
                                run.First.open |= rec.open;
                                run.First.mustmap |= rec.mustmap;
                                run.Bits.AddRange(bits);
                            }
                            else if (rec.pinit != 0)
                                _pending[ch] = new Pending { First = rec, Bits = bits };
                            else if (_pending.TryGetValue(ch, out var p))
                                p.Bits.AddRange(bits);
                            if (rec.pfin != 0 && _pending.TryGetValue(ch, out var fin))
                            {
                                _pending.Remove(ch);
                                foreach (var blk in Handle(fin.First, fin.Bits))
                                    outl.Add(blk);
                            }
                        }
                        // a closed channel index is reused for a different actor later —
                        // drop the binding so a stale actor can't claim the next tenant
                        if (rec.close != 0) { _chActor.Remove(ch); _pending.Remove(ch); _lastSeq.Remove(ch); }
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

            IEnumerable<ContentBlock> Handle(Rec first, List<byte> bits)
            {
                byte[] merged = PackBits(bits);
                int total = bits.Count;
                var br = new BR(merged);
                bool bail = false;
                // exports were already consumed per raw bunch in Feed
                if (first.mustmap != 0)
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
                        if (ap != null) ActorArchetypes[a] = ap;
                        OpenedActors.Add((a, arch, ap));
                        if (!ReadSpawnTransform(br)) bail = true;
                    }
                    else if (a != 0) { _chActor[first.ch] = a; OpenedActors.Add((a, 0, null)); }
                }
                Trace?.Invoke($"handle ch{first.ch} open={first.open} bits={total} err={br.err} bail={bail} actor={_chActor.GetValueOrDefault(first.ch)}");
                if (!br.err && !bail)
                {
                    long bunchActor = _chActor.GetValueOrDefault(first.ch);
                    foreach (var (ps, npb, guid, hasRepLayout) in ContentBlocks(br, total))
                        yield return new ContentBlock(
                            guid,
                            bunchActor,
                            Slice(merged, ps >> 3, (ps + npb + 7) >> 3),
                            ps & 7,
                            npb,
                            hasRepLayout);
                }
            }
        }
    }
}
