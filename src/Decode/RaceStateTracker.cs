using static ACRLiveTiming.Decode.RaceStateWire;

namespace ACRLiveTiming.Decode
{
    /// <summary>
    /// Streaming finish detector over the per-car <c>URaceStateData</c> component.
    /// Feed packets one at a time; the NetGUID map, partial-bunch reassembly and
    /// per-car state persist across calls, so a finish is reported the packet it
    /// becomes known. Two signals, in priority order:
    ///   * PHASE — the car's ERacePhase enters Ended/EndSequence/Post: its current
    ///             RaceTime IS the finish, event-driven and exact (probed: matches the
    ///             displayed result raw to ~1 ms, and catches finishes the old
    ///             stale-gap heuristic missed).
    ///   * RESET — the car's RaceTime drops to ~0 (next stage begins): the PREVIOUS
    ///             sample was that stage's finish. Safety net for a car that finishes
    ///             and immediately leaves (delta replication can skip its Ended phase).
    /// Phase transitions to None/Retire/Disqualify while running are NOT finishes
    /// (retire / quit mid-stage — a future DNF signal).
    /// Finishes accumulate (see <see cref="Peaks"/>) and are never withdrawn; each is
    /// reported ONCE, in the return of the <see cref="Feed"/> call that detects it.
    /// Not thread-safe: drive from a single thread (the packet feed).
    /// </summary>
    public sealed class RaceStateTracker
    {
        /// <summary>Float32 tolerance (seconds) for "same finish time". Times are
        /// decoded as float32 from two sources — the replicated RaceStateData timer
        /// peak and the result scan — which never agree bit-exactly, so equality is a
        /// near-match. Deliberately loose: too tight and one finish gets counted
        /// twice. Distinct from name binding (see SessionMatrix.NameMatchTolerance),
        /// which needs the opposite trade-off.</summary>
        public const double FinishMatchTolerance = 0.15;

        // wire format (handles, phase values, ReadState) is in RaceStateWire.
        sealed class Car
        {
            public float rt = float.NaN;   // latest RaceTime sample
            public int phase = -1;         // latest known phase
            public float dist = float.NaN; // latest DistanceOnMainSpline (metres)
            public int pos = -1;           // latest Position (live rally standing)
        }

        // A passive capture can begin after the one-off NetGUID export which names a
        // RaceStateData component. The content blocks still carry their numeric guid,
        // so recognise the component by its very distinctive wire shape: several
        // coherent samples containing BOTH h4 RaceTime and h9 spline distance. Requiring
        // three samples avoids promoting a foreign component after a chance float hit.
        sealed class OrphanCandidate
        {
            public int hits;
            public float rt;
            public float dist;
        }

        const int MaxOrphanCandidates = 512;

        LobbyDecoder.BlockStream _stream = new();
        readonly HashSet<long> _rsd = new();          // known RaceStateData NetGUIDs
        readonly HashSet<long> _inferredRsd = new();  // path export missed (mid-session capture)
        readonly Dictionary<long, OrphanCandidate> _orphanCandidates = new();
        readonly HashSet<long> _sec = new();          // known RaceSectorsPlayerData NetGUIDs
        readonly Dictionary<long, long> _ownerRsd = new();  // outer actor guid -> its RaceStateData guid
        readonly Dictionary<long, HashSet<float>> _splitSeen = new();  // rsd guid -> reported split times
        readonly List<float> _secTimes = new();       // scratch for ReadSectorTimes
        readonly Dictionary<long, Car> _cars = new();
        readonly List<double> _peaks = new();
        readonly Dictionary<long, List<double>> _peaksByCar = new();  // per-car dedup
        readonly List<double> _new = new();            // peaks added by the current Feed
        readonly List<(long id, double time)> _newPairs = new();  // same, with the car's guid
        readonly List<(long id, float dist, int phase, int pos)> _progress = new();  // this Feed's live updates
        bool _any;                                     // decoded any timer sample

        /// <summary>All finish times detected so far (raw, seconds), unordered.</summary>
        public IReadOnlyList<double> Peaks => _peaks;

        /// <summary>Finishes detected by the LAST Feed, tagged with the car's NetGUID —
        /// lets the caller bind a car identity to a driver name (the raw time matches
        /// the driver's result row). Same lifetime as the Feed return: consume before
        /// the next call.</summary>
        public IReadOnlyList<(long id, double time)> NewFinishPairs => _newPairs;

        /// <summary>Cumulative sector times decoded by the LAST Feed from the car's
        /// sibling RaceSectorsPlayerData component (same outer actor), tagged with the
        /// RaceStateData guid and deduplicated across the session. A split value
        /// matches the driver's result-row raw exactly, so the caller can name a car
        /// at its FIRST sector — long before the finish. Consume before the next call.</summary>
        public IReadOnlyList<(long id, double time)> NewSplitPairs => _newSplits;
        readonly List<(long id, double time)> _newSplits = new();

        /// <summary>Car identities resolved by the LAST Feed: (RaceStateData guid, EOS
        /// display name), from the steamid+pseudo identity block of the component's
        /// OUTER PlayerState actor. The actor is respawned per stage with the identity
        /// re-replicated in its spawn bunch, so every car marker is nameable at spawn —
        /// BEFORE the start, no sector time needed. Deterministic (the identity block
        /// sits on the actor's own channel), unlike the time-match which needs a first
        /// split. Consume before the next call.</summary>
        public IReadOnlyList<(long id, string name)> NewCarNames => _newNames;
        readonly List<(long id, string name)> _newNames = new();
        readonly Dictionary<long, string> _actorName = new();   // ps actor guid -> pseudo
        readonly HashSet<long> _namedRsd = new();               // rsd guids already emitted

        /// <summary>Live per-car updates decoded by the LAST Feed: current
        /// DistanceOnMainSpline (metres), ERacePhase and Position (live rally
        /// standing, h7). One entry per car that replicated new state in this packet;
        /// consume before the next call. Car actors are respawned per stage (fresh
        /// NetGUIDs each run); only the local player's actor persists.</summary>
        public IReadOnlyList<(long id, float dist, int phase, int pos)> Progress => _progress;

        /// <summary>Drop all state for a new session (new lobby / manual reset).</summary>
        public void Reset()
        {
            _stream = new LobbyDecoder.BlockStream();
            _rsd.Clear();
            _inferredRsd.Clear();
            _orphanCandidates.Clear();
            _sec.Clear();
            _ownerRsd.Clear();
            _splitSeen.Clear();
            _cars.Clear();
            _peaks.Clear();
            _peaksByCar.Clear();
            _new.Clear();
            _newPairs.Clear();
            _newSplits.Clear();
            _newNames.Clear();
            _actorName.Clear();
            _namedRsd.Clear();
            _progress.Clear();
            _any = false;
        }

        /// <summary>
        /// Ingest one packet. Returns the raw finish times detected by THIS packet
        /// (usually empty — consume before the next call, the list is reused) and
        /// whether a timer stream is present (false => caller should sector-gate).
        /// The full accumulated set is <see cref="Peaks"/>.
        /// </summary>
        public (List<double> newTimes, bool present) Feed(byte[] payload)
        {
            _new.Clear();
            _newPairs.Clear();
            _newSplits.Clear();
            _newNames.Clear();
            _progress.Clear();
            var blocks = _stream.Feed(payload);

            // learn RaceStateData guids from THIS packet's exports (a guid is exported
            // once, at channel open, before/with its first property data — so it is
            // known by the time its block is read). The sectors component shares its
            // outer actor with the RaceStateData guid — that link carries the split
            // times over to the car identity.
            foreach (var (guid, path) in _stream.NewGuids)
            {
                if (path == ComponentPath)
                {
                    _inferredRsd.Remove(guid);
                    _orphanCandidates.Remove(guid);
                    _rsd.Add(guid);
                    if (_stream.Outers.TryGetValue(guid, out var owner))
                    {
                        _ownerRsd[owner] = guid;
                        // the owner PlayerState actor was usually identified at ITS
                        // spawn, before this component ever replicated — bind now
                        if (_actorName.TryGetValue(owner, out var nm) && _namedRsd.Add(guid))
                            _newNames.Add((guid, nm));
                    }
                }
                else
                {
                    // A real late export always wins over a structural inference: the
                    // guid belongs to some other component, so everything the inference
                    // derived from it is wrong. Drop its car state AND its peaks —
                    // otherwise a misread component keeps producing finishes for the
                    // rest of the session. Peaks already handed to the caller cannot be
                    // recalled from here; those are inert unless one happens to land
                    // within FinishMatchTolerance of a real result time.
                    if (_inferredRsd.Remove(guid))
                    {
                        _rsd.Remove(guid);
                        _cars.Remove(guid);
                        _splitSeen.Remove(guid);
                        if (_peaksByCar.Remove(guid, out var bogus))
                            foreach (var p in bogus) _peaks.Remove(p);
                    }
                    _orphanCandidates.Remove(guid);
                    if (path == SectorsComponentPath) _sec.Add(guid);
                }
            }

            // identities resolved by this packet (opposite arrival order: the actor's
            // identity block landed with/after its RaceStateData export)
            foreach (var (actor, name) in _stream.NewIdents)
            {
                _actorName[actor] = name;
                if (_ownerRsd.TryGetValue(actor, out var rsdGuid) && _namedRsd.Add(rsdGuid))
                    _newNames.Add((rsdGuid, name));
            }

            foreach (var (guid, _, block, bitoff) in blocks)
            {
                if (_sec.Contains(guid))
                {
                    // sibling sectors component: decode this delta's cumulative split
                    // times and tag them with the owner's RaceStateData guid.
                    if (_stream.Outers.TryGetValue(guid, out var owner)
                        && _ownerRsd.TryGetValue(owner, out var rsdGuid))
                    {
                        _secTimes.Clear();
                        ReadSectorTimes(block, bitoff, _secTimes);
                        if (_secTimes.Count > 0)
                        {
                            if (!_splitSeen.TryGetValue(rsdGuid, out var seen))
                                _splitSeen[rsdGuid] = seen = new HashSet<float>();
                            foreach (var t in _secTimes)
                                if (seen.Add(t)) _newSplits.Add((rsdGuid, t));
                        }
                    }
                    continue;
                }
                // Known non-RaceStateData components are never probed, and neither is
                // the actor-level guid 0. Only a genuinely unknown guid is a candidate
                // — expected when sniffing started after its export. Rejected here,
                // before ReadState, so the decode cost is paid only where it can matter.
                bool known = _rsd.Contains(guid);
                if (!known && (guid == 0 || _stream.Guids.ContainsKey(guid))) continue;

                var (rt, dist, phase, pos) = ReadState(block, bitoff);
                if (!known)
                {
                    bool joint = !float.IsNaN(rt) && rt >= 0f && rt < FinishMax
                        && !float.IsNaN(dist) && dist >= 0f && dist < 100_000f
                        && phase <= PhaseDisqualify;
                    if (!joint) continue;

                    if (!_orphanCandidates.TryGetValue(guid, out var candidate))
                    {
                        // Bound captures of long-running lobbies containing many
                        // unrelated, never-exported subobjects. Evict the single-hit
                        // entries first: a candidate sitting at 2 hits is one sample
                        // from promotion and must not be dropped for fresh noise. If
                        // every entry is still in contention, skip this guid — the next
                        // packet carrying it offers another chance.
                        if (_orphanCandidates.Count >= MaxOrphanCandidates)
                        {
                            foreach (var stale in _orphanCandidates
                                         .Where(kv => kv.Value.hits < 2)
                                         .Select(kv => kv.Key).ToList())
                                _orphanCandidates.Remove(stale);
                            if (_orphanCandidates.Count >= MaxOrphanCandidates) continue;
                        }
                        _orphanCandidates[guid] = candidate = new OrphanCandidate();
                    }

                    // Race time and travelled distance are monotonic within a run.
                    // Small backwards tolerance absorbs float jitter; a real reset
                    // starts a fresh candidate sequence.
                    bool coherent = candidate.hits == 0
                        || (rt + 2f >= candidate.rt && dist + 50f >= candidate.dist
                            && rt - candidate.rt < 120f && dist - candidate.dist < 5_000f);
                    candidate.hits = coherent ? candidate.hits + 1 : 1;
                    candidate.rt = rt;
                    candidate.dist = dist;
                    if (candidate.hits < 3) continue;

                    _orphanCandidates.Remove(guid);
                    _inferredRsd.Add(guid);
                    _rsd.Add(guid);
                }
                if (!_cars.TryGetValue(guid, out var car)) _cars[guid] = car = new Car();

                bool rtValid = !float.IsNaN(rt) && rt >= 0 && rt < FinishMax;
                if (rtValid)
                {
                    // RESET safety net: RaceTime back to ~0 means the next stage began;
                    // the previous sample was that stage's finish (dedup absorbs it when
                    // the phase signal already reported it).
                    if (!float.IsNaN(car.rt) && rt < car.rt * 0.5f && car.rt > FinishMin)
                        AddPeak(guid, car.rt);
                    car.rt = rt;
                    _any = true;
                }

                if (phase >= 0 && phase != car.phase)
                {
                    // PHASE signal: entering an end phase from a non-end phase = the car
                    // just crossed the line; its current RaceTime is the exact finish.
                    bool wasEnd = car.phase >= PhaseEnded && car.phase <= PhasePost;
                    bool isEnd = phase >= PhaseEnded && phase <= PhasePost;
                    if (isEnd && !wasEnd && !float.IsNaN(car.rt) && car.rt > FinishMin)
                        AddPeak(guid, car.rt);
                    car.phase = phase;
                }

                // live progression: report the car's spline distance every time it
                // replicates (or its phase/position changes — a finish/retire moves
                // the marker style even when the distance delta is skipped).
                if (pos > 0) car.pos = pos;
                bool distValid = !float.IsNaN(dist) && dist >= 0f && dist < 100_000f;
                if (distValid) car.dist = dist;
                if ((distValid || phase >= 0 || pos > 0) && !float.IsNaN(car.dist))
                    _progress.Add((guid, car.dist, car.phase, car.pos));
            }

            return (_new, _any);
        }

        void AddPeak(long guid, double v)
        {
            // dedup PER CAR (phase + reset can both report the same finish of the same
            // car) — never across cars: two drivers finishing within the tolerance of
            // each other are two distinct finishes
            if (_peaksByCar.TryGetValue(guid, out var own)
                && own.Exists(f => Math.Abs(f - v) < FinishMatchTolerance)) return;
            if (!_peaksByCar.TryGetValue(guid, out own))
                _peaksByCar[guid] = own = new List<double>();
            own.Add(v);
            _peaks.Add(v);
            _new.Add(v);
            _newPairs.Add((guid, v));
        }
    }
}
