namespace ACRLiveTiming.Decode
{
    /// <summary>
    /// Streaming nation/car extractor. Feed packets one at a time: each packet is
    /// decoded ONCE (results scan + content-block parse), replacing an earlier
    /// batch join that re-decoded a 40k rolling payload buffer every refresh. Two
    /// accumulators:
    ///   * pats  — float32(raw time) -> pseudo, from every result block seen (the join
    ///             key: a driver's cumulative times are exact floats, unique per driver).
    ///   * marked blocks — content blocks carrying a car or nation token (rare: the
    ///             results-manager / participant elements), kept in a bounded FIFO.
    /// The actual join runs in <see cref="Snapshot"/>, off the packet thread: a block
    /// can arrive BEFORE its driver's time enters pats (that ordering is why a batch
    /// join needs two full passes), so matching is deferred to query time over the
    /// small retained set. Feed also returns the packet's per-driver results (the
    /// largest raw per name, over all 8 bit-shifts) so the caller doesn't scan twice.
    /// Thread-safe: Feed on the packet thread, Snapshot from anywhere.
    /// </summary>
    public sealed class NationCarTracker
    {
        const int MaxMarkedBlocks = 8192;   // FIFO cap; ~1KB avg each. Old blocks only
                                            // lose VOTES; decided info stays in the matrix.

        readonly object _lock = new();
        LobbyDecoder.BlockStream _stream = new();
        readonly Dictionary<uint, string> _pats = new();          // float32 raw -> pseudo
        readonly Queue<(byte[] seg, List<(int a, string t)> cmarks, List<(int a, string t)> nmarks)> _marked = new();
        // Base persona -> full EOS display name ("Garuda" -> "Garuda_506"). The results
        // element carries the raw persona; the leaderboard shows the EOS display name,
        // which is that persona plus an "_<discriminator>" suffix. Both replicate on the
        // wire; the persona is what sits next to the split chain, so ResultScanner binds
        // the SHORT form. We learn the full form from the join-burst identity block and
        // canonicalise every driver name to it, so the table matches the in-game board.
        readonly Dictionary<string, string> _alias = new();
        // base personas seen with TWO different discriminators ("Garuda_1" and
        // "Garuda_2" in the same lobby): canonicalising would merge two distinct
        // drivers, so such a base is never aliased — rows keep the raw persona
        readonly HashSet<string> _ambiguous = new();
        bool _dirty;

        // ---- participant-actor channel: flag + car at JOIN, no time needed -----
        // BC_RaceParticipant (one actor per player, PERSISTENT across stages): its
        // actor-level block carries the participant ID FName = the player's display
        // name, and its RaceParticipantData component block carries Driver/CoDriver
        // Nationality + CarId. Everything on the actor's own channel — deterministic
        // per-player isolation, so it takes PRECEDENCE over the time-join vote.
        // Participant actors are recognised by their RaceParticipantData component:
        // the component's export names the guid, its outer chain names the actor. The
        // archetype path that used to tag BC_RaceParticipant actors stopped being
        // exported in mid-2026 builds, so this is the binding that actually fires.
        readonly HashSet<long> _partActors = new();
        readonly Dictionary<long, string> _partName = new();                   // actor -> pseudo
        readonly Dictionary<long, Dictionary<string, int>> _partNat = new();   // actor -> nation votes
        readonly Dictionary<long, string> _partCar = new();                    // actor -> CarId, LAST wins
                                                                               // (the player can switch cars at the service park;
                                                                               // CarId only re-replicates on change, so a majority
                                                                               // vote would keep the old car forever)
        readonly HashSet<long> _resultGuids = new();
        readonly Dictionary<long, int> _resultGuidVotes = new();

        // EOS display name = "<persona>_<digits>". The discriminator is numeric; a
        // non-numeric suffix (Lights_FL01, Mirror_L…) is an engine token, not a name.
        static readonly System.Text.RegularExpressions.Regex DiscRe =
            new(@"^(?<base>.+)_\d+$", System.Text.RegularExpressions.RegexOptions.Compiled);

        // Full display name for a base persona, else the name unchanged.
        string Canon(string name) => _alias.TryGetValue(name, out var full) ? full : name;

        static void Bump(Dictionary<string, int> c, string k)
            => c[k] = c.TryGetValue(k, out var v) ? v + 1 : 1;

        static string? Top(Dictionary<string, int>? c)
            => c == null || c.Count == 0 ? null : c.OrderByDescending(kv => kv.Value).First().Key;

        // Learn base->full aliases from this packet's byte-aligned FStrings (the EOS
        // display name replicates byte-aligned in the identity block).
        void LearnAliases(List<Primitives.FStr> fstrs0)
        {
            foreach (var f in fstrs0)
            {
                if (!Names.IsPlayerName(f.Text)) continue;
                var m = DiscRe.Match(f.Text);
                if (!m.Success) continue;
                var b = m.Groups["base"].Value;
                if (!Names.IsPlayerName(b) || _ambiguous.Contains(b)) continue;
                if (_alias.TryGetValue(b, out var existing))
                {
                    if (existing != f.Text) { _alias.Remove(b); _ambiguous.Add(b); }
                }
                else _alias[b] = f.Text;
            }
        }

        /// <summary>Teach the alias map one full EOS display name obtained out-of-band
        /// (the spawn-time identity block): "&lt;base&gt;_&lt;digits&gt;" aliases the base
        /// persona that result blocks carry. Same rules as the passive learner —
        /// ambiguous bases (two discriminators in the lobby) are never aliased.</summary>
        public void LearnAlias(string fullName)
        {
            lock (_lock)
            {
                if (!Names.IsPlayerName(fullName)) return;
                var m = DiscRe.Match(fullName);
                if (!m.Success) return;
                var b = m.Groups["base"].Value;
                if (!Names.IsPlayerName(b) || _ambiguous.Contains(b)) return;
                if (_alias.TryGetValue(b, out var existing))
                {
                    if (existing != fullName) { _alias.Remove(b); _ambiguous.Add(b); }
                }
                else _alias[b] = fullName;
            }
        }

        /// <summary>New data since the last snapshot?</summary>
        public bool Dirty { get { lock (_lock) return _dirty; } }

        /// <summary>Every (name, raw) pair of the LAST Feed, 1-sector partials
        /// INCLUDED — the car-naming scratch (a car is bound to a driver at its first
        /// split). Feed also returns these partials; the display policy decides whether
        /// the table reveals them. Consume before the next call; the list is reused.</summary>
        public IReadOnlyList<(string name, double raw)> NameRaws => _nameRaws;
        readonly List<(string name, double raw)> _nameRaws = new();

        /// <summary>Drivers present in a structurally decoded rally-results component
        /// in the LAST Feed. Includes first-sector partials, so the standings can add
        /// the name immediately; the split itself follows the active display policy.</summary>
        public IReadOnlyList<string> StartedNames => _startedNames;
        readonly List<string> _startedNames = new();

        /// <summary>Drivers whose structurally decoded result element carries
        /// bDNF=true in the LAST Feed. Each element also passed the ParticipantId,
        /// CarId and next-handle guards; malformed or ambiguous elements contribute
        /// nothing.</summary>
        public IReadOnlyList<string> DnfNames => _dnfNames;
        readonly List<string> _dnfNames = new();

        // Complete cumulative split chain for each result returned by the LAST Feed.
        // Engine consumes it immediately alongside Feed's backwards-compatible tuple.
        readonly Dictionary<string, IReadOnlyList<double>> _resultSplits = new();
        public IReadOnlyList<double>? ResultSplitsFor(string name)
        {
            lock (_lock)
                return _resultSplits.TryGetValue(name, out var splits) ? splits : null;
        }

        public void Reset()
        {
            lock (_lock)
            {
                _stream = new LobbyDecoder.BlockStream();
                _pats.Clear();
                _marked.Clear();
                _alias.Clear();
                _ambiguous.Clear();
                _partActors.Clear();
                _partName.Clear();
                _partNat.Clear();
                _partCar.Clear();
                _resultGuids.Clear();
                _resultGuidVotes.Clear();
                _nameRaws.Clear();
                _startedNames.Clear();
                _dnfNames.Clear();
                _resultSplits.Clear();
                _dirty = false;
            }
        }

        /// <summary>
        /// Ingest one packet. Returns the packet's per-driver results (name, total =
        /// raw+penalty, raw, sectors — largest raw per name across the 8 bit-shifts),
        /// which the caller feeds to the matrix. Every (name, raw) pair also lands in
        /// the join map, and the packet's content blocks are scanned for car/nation
        /// marks (block decode is best-effort: a malformed packet must not lose the
        /// results, which come from the independent byte scan).
        /// </summary>
        public List<(string name, double total, double raw, int sectors)> Feed(byte[] payload)
        {
            var (shifted, fstrs) = Primitives.ShiftScan(payload);
            return Feed(payload, shifted, fstrs);
        }

        /// <summary>Same, with the 8 bit-shifted views and their FStrings already
        /// computed (<see cref="Primitives.ShiftScan"/>) — the engine shares them
        /// across all per-packet consumers instead of re-deriving them here.</summary>
        public List<(string name, double total, double raw, int sectors)> Feed(
            byte[] payload, byte[][] shifted, List<Primitives.FStr>[] fstrs)
        {
            // results scan (8 shifts) — keeps EVERY (name, raw) pair for the join map
            // (first name wins per distinct float), while returning only the largest
            // raw per name.
            // The broad scan's partials feed the naming scratch and join map. Only the
            // structurally gated results-component scan below promotes S1 into the
            // matrix's raw cells, avoiding unrelated one-float signature collisions.
            var best = new Dictionary<string, (double raw, double pen, IReadOnlyList<double> splits)>();
            _nameRaws.Clear();
            _startedNames.Clear();
            _dnfNames.Clear();
            lock (_lock)
            {
                _resultSplits.Clear();
                LearnAliases(fstrs[0]);
                for (int shift = 0; shift < 8; shift++)
                {
                    foreach (var (rawName, raw, pen, splits) in ResultScanner.DetailedResultsIn(shifted[shift], fstrs[shift], includePartials: true))
                    {
                        var name = Canon(rawName);               // short persona -> EOS display name
                        int sectors = splits.Count;
                        uint key = BitConverter.ToUInt32(BitConverter.GetBytes((float)raw), 0);
                        // bounded like _marked: one entry per distinct split float ever
                        // seen — unbounded growth over a many-hour lobby otherwise
                        if (!_pats.ContainsKey(key) && _pats.Count < 65536) { _pats[key] = name; _dirty = true; }
                        _nameRaws.Add((name, raw));
                        if (sectors < 2) continue;               // reliable S1 is added from the results component below
                        if (!best.TryGetValue(name, out var cur) || raw > cur.raw)
                            best[name] = (raw, pen, splits);
                    }
                }
            }

            try
            {
                var blocks = _stream.Feed(payload);
                foreach (var (guid, path) in _stream.NewGuids)
                {
                    if (path == "RaceEventRallyResults") _resultGuids.Add(guid);
                    else
                    {
                        _resultGuids.Remove(guid);
                        _resultGuidVotes.Remove(guid);
                        if (path == "RaceParticipantData" && _stream.Outers.TryGetValue(guid, out var owner))
                            _partActors.Add(owner);
                    }
                }
                foreach (var block in blocks)
                {
                    var (guid, actor, seg, _) = block;
                    // A zero-split retirement stays in the partial-results array
                    // (upper handle 6) and never acquires a split signature. Decode
                    // bDNF from the element itself; ResultDnfScanner publishes only
                    // a complete, unambiguous ParticipantId/CarId/bDNF sequence.
                    IReadOnlyList<ResultDnfScanner.ResultFlag> flags =
                        Array.Empty<ResultDnfScanner.ResultFlag>();
                    if (guid != 0 && _resultGuids.Contains(guid))
                        flags = ResultDnfScanner.Scan(block);
                    else if (guid != 0 && !_stream.Guids.ContainsKey(guid))
                    {
                        // A capture may begin after the one-off class export. Infer an
                        // orphan results guid only from repeated, complete elements
                        // (name + CarId + bDNF + in-range next handle) seen in at least
                        // two blocks. ACR 0.6 serialises the per-participant session
                        // array and the final array in ONE block, so the scanner only
                        // ever sees the session array's leading element there — its
                        // votes count too.
                        var candidates = ResultDnfScanner.Scan(block);
                        int votes = candidates.Count;
                        if (votes > 0)
                        {
                            int total = _resultGuidVotes.GetValueOrDefault(guid) + votes;
                            _resultGuidVotes[guid] = total;
                            if (total >= 2)
                            {
                                _resultGuids.Add(guid);
                                flags = candidates;
                            }
                        }
                    }
                    foreach (var result in flags)
                    {
                        if (!result.Dnf) continue;
                        var name = Canon(result.Name);
                        lock (_lock)
                            if (!_dnfNames.Contains(name)) _dnfNames.Add(name);
                    }

                    // Unlike the broad packet scan used for the time join, this scan is
                    // gated on the actual results component. It is therefore safe to
                    // expose a driver's name as soon as its one-sector partial appears.
                    // Nationality FNames share the same component and can accidentally
                    // satisfy the duplicate-float signature, so exclude them.
                    if (guid != 0 && _stream.Guids.TryGetValue(guid, out var path)
                        && path == "RaceEventRallyResults")
                    {
                        var (resultShifted, resultFstrs) = Primitives.ShiftScan(seg);
                        lock (_lock)
                        {
                            for (int shift = 0; shift < 8; shift++)
                                foreach (var (rawName, raw, pen, splits) in ResultScanner.DetailedResultsIn(
                                    resultShifted[shift], resultFstrs[shift],
                                    includePartials: true, strictPartials: true))
                                {
                                    if (LobbyDecoder.IsNation(rawName)) continue;
                                    var name = Canon(rawName);
                                    int sectors = splits.Count;
                                    if (!_startedNames.Contains(name)) _startedNames.Add(name);
                                    // The broad packet scan deliberately rejects S1: at
                                    // one sector its signature can collide with unrelated
                                    // replicated floats. Inside the known results component
                                    // (and after nation filtering) it is safe to publish.
                                    if (sectors == 1
                                        && (!best.TryGetValue(name, out var cur) || raw > cur.raw))
                                        best[name] = (raw, pen, splits);
                                }
                        }
                    }

                    // participant ACTOR block: its only string property is the
                    // participant ID FName = the player's display name
                    if (guid == 0 && actor != 0 && !_partName.ContainsKey(actor)
                        && (_partActors.Contains(actor) || _stream.ParticipantActors.Contains(actor)))
                    {
                        var nm = LobbyDecoder.FirstPlayerNameIn(seg);
                        if (nm != null)
                            lock (_lock) { _partName[actor] = nm; _dirty = true; }
                    }

                    var (cmarks, nmarks) = LobbyDecoder.MarksIn(seg);
                    if (cmarks.Count == 0 && nmarks.Count == 0) continue;

                    // participant COMPONENT block (RaceParticipantData, outer = the
                    // participant actor): nation + car bound to THAT player. Since ACR
                    // 0.6 this component is the ONLY carrier of nationality (result
                    // entries lost their Driver/CoDriver data), replicated at join and
                    // on change. Its replicated properties are, in handle order:
                    // PlayerCountryId (3), DriverData Name/Surname/Nationality (4-6),
                    // CoDriverData Name/Surname/Nationality (7-9), RaceNumber (10),
                    // CarId (11) — handle numbers identical in 0.5 and 0.6 (same class
                    // layout). The driver's nation is the token under handle 6; when the
                    // handle byte is not readable (odd alignment) fall back to position:
                    // the co-driver's nation is the LAST one, the driver's the one before
                    // it (PlayerCountryId, when present, comes first). "Other" is the
                    // game's unset value — no flag. Consumed here, not by the time-join pool.
                    long owner = guid != 0 && _stream.Outers.TryGetValue(guid, out var og) ? og : 0;
                    if (owner != 0 && (_partActors.Contains(owner) || _stream.ParticipantActors.Contains(owner)))
                    {
                        string? car = null;
                        foreach (var (a, t) in cmarks)
                            if (LobbyDecoder.HandleBefore(seg, a, t) == 11) { car = t; break; }
                        car ??= cmarks.FirstOrDefault(m => Content.ContentCatalog.IsKnownCar(m.t)).t
                                ?? (cmarks.Count > 0 ? cmarks[0].t : null);

                        string? driverNat = null;
                        foreach (var (a, t) in nmarks)
                            if (LobbyDecoder.HandleBefore(seg, a, t) == 6) { driverNat = t; break; }
                        if (driverNat == null && nmarks.Count > 0)
                            driverNat = nmarks.Count >= 2 ? nmarks[^2].t : nmarks[0].t;
                        if (driverNat == "Other") driverNat = null;

                        lock (_lock)
                        {
                            if (car != null) _partCar[owner] = car;
                            if (driverNat != null)
                            {
                                if (!_partNat.TryGetValue(owner, out var nv)) _partNat[owner] = nv = new();
                                Bump(nv, driverNat);
                            }
                            _dirty = true;
                        }
                        continue;
                    }

                    lock (_lock)
                    {
                        _marked.Enqueue((seg, cmarks, nmarks));
                        if (_marked.Count > MaxMarkedBlocks) _marked.Dequeue();
                        _dirty = true;
                    }
                }
            }
            catch { /* best-effort block decode; results above are already safe */ }

            var results = new List<(string, double, double, int)>();
            lock (_lock)
            {
                foreach (var kv in best)
                {
                    _resultSplits[kv.Key] = kv.Value.splits.ToArray();
                    results.Add((kv.Key, kv.Value.raw + kv.Value.pen, kv.Value.raw, kv.Value.splits.Count));
                }
            }
            return results;
        }

        /// <summary>
        /// Run the time-join over the retained marked blocks and return
        /// {pseudo -> (nation, car)} by majority vote — the same join as the batch
        /// extractor, over blocks decoded once. Cheap enough for a periodic refresh
        /// (hundreds of small blocks, not 40k packets). Clears the dirty flag.
        /// </summary>
        public Dictionary<string, (string? nation, string? car)> Snapshot()
        {
            (byte[] seg, List<(int a, string t)> cmarks, List<(int a, string t)> nmarks)[] blocks;
            Dictionary<uint, string> pats;
            var part = new List<(string name, string? nation, string? car)>();
            lock (_lock)
            {
                blocks = _marked.ToArray();
                // canonicalise on the way out: an alias may have been learned AFTER a
                // key was first stored under the short persona, so resolve every value
                // through the map here to keep the vote keyed on one name per driver.
                pats = new Dictionary<uint, string>(_pats.Count);
                foreach (var kv in _pats) pats[kv.Key] = Canon(kv.Value);
                foreach (var kv in _partName)
                {
                    var nat = Top(_partNat.GetValueOrDefault(kv.Key));
                    var car = _partCar.GetValueOrDefault(kv.Key);
                    if (nat != null || car != null) part.Add((Canon(kv.Value), nat, car));
                }
                _dirty = false;
            }
            var result = pats.Count == 0
                ? new Dictionary<string, (string?, string?)>()
                : Join(blocks, pats);

            // overlay the participant-actor bindings: available from the JOIN (no lap
            // time needed) and per-actor isolated, so they win over the time-join vote
            foreach (var (name, nat, car) in part)
            {
                result.TryGetValue(name, out var cur);
                result[name] = (nat ?? cur.Item1, car ?? cur.Item2);
            }
            return result;
        }

        static Dictionary<string, (string?, string?)> Join(
            (byte[] seg, List<(int a, string t)> cmarks, List<(int a, string t)> nmarks)[] blocks,
            Dictionary<uint, string> pats)
        {
            var joinNat = new Dictionary<string, Dictionary<string, int>>();
            var joinCar = new Dictionary<string, Dictionary<string, int>>();
            var driverNames = new HashSet<string>(pats.Values, StringComparer.Ordinal);
            foreach (var (seg, cmarks, nmarks) in blocks)
                LobbyDecoder.JoinBlock(seg, cmarks, nmarks, pats, driverNames, joinNat, joinCar);
            return LobbyDecoder.ResolveVotes(pats.Values.Distinct(), joinNat, joinCar);
        }
    }
}
