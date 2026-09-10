namespace ACRLiveTiming.Decode
{
    /// <summary>
    /// Layout-driven replication decoder: reassembles bunches (<see cref="LobbyDecoder.BlockStream"/>),
    /// maps every content block to the class of its object — components by the name
    /// their NetGUID is exported under, actors by the archetype exported with their
    /// spawn, orphans (export missed by a late capture) by the one layout that decodes
    /// their blocks exactly — and reads the replicated properties with the class
    /// layout (<see cref="RepLayouts"/>). Deltas are merged into per-object state, so
    /// consumers see whole objects: participants (id, nation, car, crew), player
    /// states (display name, participant id, the guids of their per-car components),
    /// race states (time, phase, position, distance), sector records, the rally result
    /// arrays and the game state's travel track. Every value is exact: no byte
    /// pattern, no bit-shift scan, no vote.
    /// Not thread-safe: drive one instance from a single thread.
    /// </summary>
    public sealed class ReplicationDecoder
    {
        // ---- decoded object model -------------------------------------------------

        /// <summary>BC_RaceParticipant actor + its RaceParticipantData component.</summary>
        public sealed class Participant
        {
            public long Actor;
            public long DataGuid;
            public string? Id;               // participant id FName = the player's display name
            public int Type = -1;             // ERaceParticipantType
            public int Status = -1;           // ERaceParticipantStatus
            public string? Nation;           // DriverData.Nationality (null when "Other"/unknown)
            public string? CoDriverNation;
            public string? DriverName, DriverSurname, CoDriverName, CoDriverSurname;
            public string? CountryId;        // PlayerCountryId (FString)
            public string? CarId;
            public int RaceNumber = -1;
        }

        /// <summary>BC_RacePlayerState actor: names the per-car components it owns.</summary>
        public sealed class Player
        {
            public long Actor;
            public string? Name;             // PlayerNamePrivate (EOS display name)
            public string? ParticipantId;
            public string? CountryId;
            public string? UniqueId;         // "EOSPlus:<steamid64>_+_|<32-hex EOS puid>"
            /// <summary>Steam id and EOS product user id split out of the unique id.</summary>
            public (string steamId, string eosPuid)? Account
            {
                get
                {
                    var id = UniqueId;
                    if (id == null) return null;
                    int colon = id.IndexOf(':');
                    var contents = colon >= 0 ? id[(colon + 1)..] : id;
                    int sep = contents.IndexOf("_+_|", StringComparison.Ordinal);
                    if (sep <= 0 || sep + 4 >= contents.Length) return null;
                    return (contents[..sep], contents[(sep + 4)..]);
                }
            }
            public long RaceStateData;       // guid of the car's RaceStateData component
            public long RaceSectorsPlayerData;
            public int PlayerId = -1;
        }

        /// <summary>URaceStateData component (one per car, respawned each stage).</summary>
        public sealed class CarState
        {
            public long Guid;
            public long OwnerActor;          // the PlayerState actor the component belongs to
            public int RaceId = -1;
            public float RaceTime = float.NaN;
            public int Phase = -1;
            public int Position = -1;
            public float Distance = float.NaN;
            public bool RaceTimeValid;
        }

        /// <summary>One rally result (a participant on one race): cumulative sector
        /// times, penalty, car, and the DNF/DQ flags when the entry is final.</summary>
        public sealed class RallyResult
        {
            public string ParticipantId = "";
            public int RaceId = -1;
            public SortedDictionary<int, float> Sectors { get; } = new();   // index -> cumulative time
            public int SectorCount;                                          // replicated array count
            public float Penalty;
            public string? CarId;
            public bool Dnf, Dq;
            public float? Raw => Sectors.Count > 0 ? Sectors.Values.Max() : null;
            public IReadOnlyList<float> Splits => Sectors.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
        }

        public enum ResultSource { Partial, Session, Best }

        public readonly record struct ResultUpdate(ResultSource Source, RallyResult Result);

        // ---- state -------------------------------------------------------------------

        public LobbyDecoder.BlockStream Stream { get; private set; } = new();

        readonly Dictionary<long, RepLayout> _layoutByGuid = new();        // component guid -> layout
        // actor guid -> layout, from the spawn archetype or inferred from a component the
        // actor owns (a late capture misses the spawn but still sees the component exports)
        readonly Dictionary<long, RepLayout> _actorLayout = new();
        static readonly Dictionary<string, string> ActorClassOfComponent = new(StringComparer.Ordinal)
        {
            ["RaceParticipantData"] = "BC_RaceParticipant_C",
            ["RaceStateData"] = "BC_RacePlayerState_C",
            ["RaceSectorsPlayerData"] = "BC_RacePlayerState_C",
        };
        readonly Dictionary<long, Dictionary<string, int>> _orphanVotes = new();
        readonly Dictionary<long, RepObject> _merged = new();              // guid (or actor) -> merged state

        public Dictionary<long, Participant> Participants { get; } = new();   // by participant actor
        public Dictionary<long, Player> Players { get; } = new();             // by player-state actor
        public Dictionary<long, CarState> Cars { get; } = new();              // by RaceStateData guid
        public Dictionary<long, SortedDictionary<int, float>> CarSectors { get; } = new();   // sectors guid -> index -> time
        readonly Dictionary<long, long> _sectorsOwner = new();                // sectors guid -> RaceStateData guid

        public string? TravelTrackId { get; private set; }     // AAcrGameState.TravelTrackId
        public string? EventTrackId { get; private set; }      // RaceEventDataComponent.TrackId
        public string? PendingTrackId { get; private set; }    // RaceLobbyDataComponent.PendingTrackId

        // rally results component: three arrays, elements merged across deltas

        // ---- per-Feed outputs (cleared on every Feed) --------------------------------

        public List<ResultUpdate> ResultUpdates { get; } = new();
        public List<Participant> ParticipantUpdates { get; } = new();
        public List<Player> PlayerUpdates { get; } = new();
        public List<CarState> CarUpdates { get; } = new();
        public List<(long rsd, float time)> NewSectorTimes { get; } = new();
        public bool TrackChanged { get; private set; }
        /// <summary>Property blocks that decoded exactly / that did not (owner-only RPC
        /// traffic and unknown objects), for diagnostics.</summary>
        public int BlocksDecoded { get; private set; }
        public int BlocksSkipped { get; private set; }

        public void Reset()
        {
            Stream = new LobbyDecoder.BlockStream();
            _layoutByGuid.Clear(); _actorLayout.Clear(); _orphanVotes.Clear(); _merged.Clear();
            Participants.Clear(); Players.Clear(); Cars.Clear(); CarSectors.Clear(); _sectorsOwner.Clear();
            TravelTrackId = EventTrackId = PendingTrackId = null;
            ClearOutputs();
            BlocksDecoded = BlocksSkipped = 0;
        }

        void ClearOutputs()
        {
            ResultUpdates.Clear(); ParticipantUpdates.Clear(); PlayerUpdates.Clear();
            CarUpdates.Clear(); NewSectorTimes.Clear(); TrackChanged = false;
        }

        /// <summary>Participant whose id is <paramref name="id"/>, if known.</summary>
        public Participant? ParticipantById(string id)
            => Participants.Values.FirstOrDefault(p => p.Id == id);

        /// <summary>Driver name owning a RaceStateData guid: the component's outer is
        /// the player's PlayerState actor, whose ParticipantId names the driver.</summary>
        public string? DriverOfCar(long rsdGuid)
        {
            if (Cars.TryGetValue(rsdGuid, out var car) && car.OwnerActor != 0
                && Players.TryGetValue(car.OwnerActor, out var owner))
                return owner.ParticipantId ?? owner.Name;
            foreach (var p in Players.Values)
                if (p.RaceStateData == rsdGuid) return p.ParticipantId ?? p.Name;
            return null;
        }

        public void Feed(byte[] payload)
        {
            ClearOutputs();
            var blocks = Stream.Feed(payload);

            foreach (var (guid, path) in Stream.NewGuids)
            {
                if (path.StartsWith("Default__", StringComparison.Ordinal)) continue;   // an archetype, not an instance
                var layout = RepLayouts.ForWireName(path);
                if (layout != null) { _layoutByGuid[guid] = layout; _orphanVotes.Remove(guid); }
                if (ActorClassOfComponent.TryGetValue(path, out var actorClass) && Stream.Outers.TryGetValue(guid, out var owner)
                    && !_actorLayout.ContainsKey(owner) && RepLayouts.ForClass(actorClass) is RepLayout actorLayout)
                    _actorLayout[owner] = actorLayout;
            }
            foreach (var (actor, _, path) in Stream.OpenedActors)
                if (path != null && RepLayouts.ForWireName(path.Split('/', '.')[^1]) is RepLayout l)
                    _actorLayout[actor] = l;

            foreach (var block in blocks)
            {
                if (!block.HasRepLayout) continue;
                RepLayout? layout;
                long key;
                if (block.Guid == 0)
                {
                    // actor-level block: class from the spawn archetype, or inferred from
                    // the components the actor owns
                    key = block.Actor;
                    if (key == 0 || !_actorLayout.TryGetValue(key, out layout)) { BlocksSkipped++; continue; }
                }
                else
                {
                    key = block.Guid;
                    if (!_layoutByGuid.TryGetValue(key, out layout))
                    {
                        layout = InferOrphan(block);
                        if (layout == null) { BlocksSkipped++; continue; }
                    }
                }

                var obj = layout.Read(block);
                if (!obj.Complete || obj.HandlesRead == 0) { BlocksSkipped++; continue; }
                BlocksDecoded++;
                var state = Merge(key, obj);
                Apply(layout, key, block.Actor, obj, state);
            }
        }

        // A capture that starts mid-session misses the one-off export naming a
        // component. Its blocks still carry the numeric guid: the layout that decodes
        // three of them to the exact end, and no other layout, is the component's class.
        RepLayout? InferOrphan(LobbyDecoder.BlockStream.ContentBlock block)
        {
            if (Stream.Guids.ContainsKey(block.Guid) || block.BitLength < 40) return null;
            if (!_orphanVotes.TryGetValue(block.Guid, out var votes)) _orphanVotes[block.Guid] = votes = new();
            RepLayout? winner = null;
            foreach (var l in RepLayouts.All)
            {
                if (l.WireNames.Count == 0 || l.WireNames[0].StartsWith("Default__", StringComparison.Ordinal)) continue;
                var o = l.Read(block);
                if (o.Complete && o.HandlesRead > 0 && o.BitsLeft < 8)   // a few padding bits can trail a block
                {
                    int n = votes.GetValueOrDefault(l.ClassName) + 1;
                    votes[l.ClassName] = n;
                    if (n >= 3) winner = l;
                }
                else votes[l.ClassName] = 0;
            }
            if (winner == null) return null;
            if (votes.Count(v => v.Value >= 3) != 1) return null;   // ambiguous: wait for more blocks
            _layoutByGuid[block.Guid] = winner;
            _orphanVotes.Remove(block.Guid);
            return winner;
        }

        RepObject Merge(long key, RepObject delta)
        {
            if (!_merged.TryGetValue(key, out var state)) _merged[key] = state = new RepObject();
            foreach (var (path, value) in delta.Values)
            {
                if (value is RepArray arr && state.Values.TryGetValue(path, out var existing) && existing is RepArray prev)
                {
                    // arrays: merge element values (a delta carries only the changed ones)
                    var merged = new RepArray { Count = arr.Count };
                    foreach (var (i, e) in prev.Elements) if (i < arr.Count) merged.Elements[i] = e;
                    foreach (var (i, e) in arr.Elements)
                    {
                        if (!merged.Elements.TryGetValue(i, out var target)) merged.Elements[i] = target = new RepObject();
                        MergeInto(target, e);
                    }
                    state.Values[path] = merged;
                }
                else state.Values[path] = value;
            }
            return state;
        }

        static void MergeInto(RepObject target, RepObject delta)
        {
            foreach (var (path, value) in delta.Values)
            {
                if (value is RepArray arr && target.Values.TryGetValue(path, out var existing) && existing is RepArray prev)
                {
                    var merged = new RepArray { Count = arr.Count };
                    foreach (var (i, e) in prev.Elements) if (i < arr.Count) merged.Elements[i] = e;
                    foreach (var (i, e) in arr.Elements)
                    {
                        if (!merged.Elements.TryGetValue(i, out var t)) merged.Elements[i] = t = new RepObject();
                        MergeInto(t, e);
                    }
                    target.Values[path] = merged;
                }
                else target.Values[path] = value;
            }
        }

        // ---- per-class consumers --------------------------------------------------------

        void Apply(RepLayout layout, long key, long actor, RepObject delta, RepObject state)
        {
            switch (layout.ClassName)
            {
                case "BC_RaceParticipant_C": ApplyParticipantActor(key, delta, state); break;
                case "RaceParticipantDataComponent": ApplyParticipantData(key, delta, state); break;
                case "BC_RacePlayerState_C": ApplyPlayerState(key, delta, state); break;
                case "RaceStateData": ApplyRaceState(key, delta, state); break;
                case "RaceSectorsPlayerData": ApplySectors(key, delta); break;
                case "RaceEventRallyResultsComponent": ApplyResults(delta, state); break;
                case "BC_RaceGameState_C":
                    if (delta.GetString("TravelTrackId") is string travel && travel != TravelTrackId)
                    { TravelTrackId = travel; TrackChanged = true; }
                    break;
                case "RaceEventDataComponent":
                    if (delta.GetString("TrackId") is string track && track != EventTrackId)
                    { EventTrackId = track; TrackChanged = true; }
                    break;
                case "RaceLobbyDataComponent":
                    if (delta.GetString("PendingTrackId") is string pending && pending != PendingTrackId)
                    { PendingTrackId = pending; TrackChanged = true; }
                    break;
            }
        }

        Participant ParticipantFor(long actor)
        {
            if (!Participants.TryGetValue(actor, out var p)) Participants[actor] = p = new Participant { Actor = actor };
            return p;
        }

        void ApplyParticipantActor(long actor, RepObject delta, RepObject state)
        {
            var p = ParticipantFor(actor);
            if (state.GetString("ID") is string id) p.Id = id;
            if (state.Get<int>("Type") is int type) p.Type = type;
            if (state.Get<int>("Status") is int status) p.Status = status;
            if (state.Get<long>("RaceParticipantData") is long data) p.DataGuid = data;
            ParticipantUpdates.Add(p);
        }

        void ApplyParticipantData(long guid, RepObject delta, RepObject state)
        {
            // the component's outer is the participant actor
            if (!Stream.Outers.TryGetValue(guid, out var actor))
                actor = Participants.Values.FirstOrDefault(p => p.DataGuid == guid)?.Actor ?? 0;
            if (actor == 0) return;
            var p = ParticipantFor(actor);
            p.DataGuid = guid;
            string? Nat(string path)
            {
                var n = state.GetString(path);
                return n == null || n == "Other" || n.StartsWith("EName:", StringComparison.Ordinal) ? null : n;
            }
            p.Nation = Nat("DriverData.Nationality");
            p.CoDriverNation = Nat("CoDriverData.Nationality");
            p.DriverName = state.GetString("DriverData.Name");
            p.DriverSurname = state.GetString("DriverData.Surname");
            p.CoDriverName = state.GetString("CoDriverData.Name");
            p.CoDriverSurname = state.GetString("CoDriverData.Surname");
            p.CountryId = state.GetString("PlayerCountryId");
            if (state.GetString("CarId") is string car && car.Length > 0) p.CarId = car;
            if (state.Get<int>("RaceNumber") is int number) p.RaceNumber = number;
            ParticipantUpdates.Add(p);
        }

        void ApplyPlayerState(long actor, RepObject delta, RepObject state)
        {
            if (!Players.TryGetValue(actor, out var pl)) Players[actor] = pl = new Player { Actor = actor };
            if (state.GetString("PlayerNamePrivate") is string name) pl.Name = name;
            if (state.GetString("ParticipantId") is string pid) pl.ParticipantId = pid;
            if (state.GetString("UniqueID") is string uid && uid.Length > 0) pl.UniqueId = uid;
            if (state.GetString("CountryId") is string country) pl.CountryId = country;
            if (state.Get<int>("PlayerId") is int playerId) pl.PlayerId = playerId;
            if (state.Get<long>("RaceStateData") is long rsd && rsd != 0) pl.RaceStateData = rsd;
            if (state.Get<long>("RaceSectorsPlayerData") is long sec && sec != 0)
            {
                pl.RaceSectorsPlayerData = sec;
                if (pl.RaceStateData != 0) _sectorsOwner[sec] = pl.RaceStateData;
            }
            PlayerUpdates.Add(pl);
        }

        void ApplyRaceState(long guid, RepObject delta, RepObject state)
        {
            if (!Cars.TryGetValue(guid, out var car)) Cars[guid] = car = new CarState { Guid = guid };
            if (car.OwnerActor == 0 && Stream.Outers.TryGetValue(guid, out var owner)) car.OwnerActor = owner;
            if (state.Get<int>("RaceId") is int raceId) car.RaceId = raceId;
            if (state.Get<float>("RaceTime") is float rt) car.RaceTime = rt;
            if (state.Get<int>("Phase") is int phase) car.Phase = phase;
            if (state.Get<int>("Position") is int pos) car.Position = pos;
            if (state.Get<float>("DistanceOnMainSpline") is float dist) car.Distance = dist;
            if (state.Get<bool>("bRaceTimeValid") is bool valid) car.RaceTimeValid = valid;
            CarUpdates.Add(car);
        }

        void ApplySectors(long guid, RepObject delta)
        {
            var arr = delta.GetArray("SectorsRecords");
            if (arr == null) return;
            // owner car: through the player state that references both components, or
            // through the shared outer actor when the player state was not decoded
            if (!_sectorsOwner.TryGetValue(guid, out var rsd))
            {
                if (Stream.Outers.TryGetValue(guid, out var outer))
                    foreach (var (g, l) in _layoutByGuid)
                        if (l.ClassName == "RaceStateData" && Stream.Outers.TryGetValue(g, out var o2) && o2 == outer) { rsd = g; break; }
                if (rsd == 0) return;
                _sectorsOwner[guid] = rsd;
            }
            if (!CarSectors.TryGetValue(rsd, out var times)) CarSectors[rsd] = times = new();
            foreach (var (i, e) in arr.Elements)
                if (e.Get<float>("Time") is float t && t > 0f && (!times.TryGetValue(i, out var known) || Math.Abs(known - t) > 0.0005f))
                {
                    times[i] = t;
                    NewSectorTimes.Add((rsd, t));
                }
        }

        // Every entry is rebuilt from the MERGED state of its array element and never
        // accumulated per index: the merged state mirrors the replicated object exactly
        // (an index reused after the array shrank restarts from defaults, so a sector
        // list the server never re-sent is genuinely empty), whereas an object kept per
        // index would inherit its previous occupant's times — a newcomer appended where
        // a leaver used to sit would show up with that leaver's result.
        void ApplyResults(RepObject delta, RepObject state)
        {
            // live per-participant results of the race in progress
            if (state.GetArray("ParticipantsPartialResults") is RepArray partial && delta.GetArray("ParticipantsPartialResults") is RepArray partialDelta)
                foreach (var (i, _) in partialDelta.Elements)
                    if (partial.Elements.TryGetValue(i, out var e) && ReadResult(e, "") is RallyResult r)
                        ResultUpdates.Add(new ResultUpdate(ResultSource.Partial, r));

            // per-participant session results: one entry per race, final (bDNF/bDQ)
            if (state.GetArray("ParticipantsResults") is RepArray session && delta.GetArray("ParticipantsResults") is RepArray sessionDelta)
                foreach (var (i, _) in sessionDelta.Elements)
                {
                    if (!session.Elements.TryGetValue(i, out var entry)) continue;
                    if (entry.GetString("ParticipantId") is not string participant) continue;   // element seen only as a delta
                    if (entry.GetArray("Results.Results") is not RepArray races) continue;      // FRaceParticipantRallySessionResults.Results
                    foreach (var (_, race) in races.Elements)
                    {
                        var r = new RallyResult { ParticipantId = participant };
                        FillResult(r, race, "");
                        ResultUpdates.Add(new ResultUpdate(ResultSource.Session, r));
                    }
                }

            if (state.GetArray("BestParticipantsResults") is RepArray best && delta.GetArray("BestParticipantsResults") is RepArray bestDelta)
                foreach (var (i, _) in bestDelta.Elements)
                    if (best.Elements.TryGetValue(i, out var e) && ReadResult(e, "Result.") is RallyResult r)
                        ResultUpdates.Add(new ResultUpdate(ResultSource.Best, r));
        }

        static RallyResult? ReadResult(RepObject element, string prefix)
        {
            if (element.GetString("ParticipantId") is not string pid) return null;   // element seen only as a delta
            var r = new RallyResult { ParticipantId = pid };
            FillResult(r, element, prefix);
            return r;
        }

        static void FillResult(RallyResult r, RepObject element, string prefix)
        {
            if (element.Get<int>(prefix + "RaceId") is int raceId) r.RaceId = raceId;
            if (element.GetString(prefix + "CarId") is string car && car.Length > 0) r.CarId = car;
            if (element.Get<float>(prefix + "RallyTimes.PenaltyTotalTime") is float pen) r.Penalty = pen;
            if (element.Get<bool>(prefix + "bDNF") is bool dnf) r.Dnf = dnf;
            if (element.Get<bool>(prefix + "bDQ") is bool dq) r.Dq = dq;
            if (element.GetArray(prefix + "RallyTimes.Sectors") is RepArray sectors)
            {
                r.SectorCount = sectors.Count;
                foreach (var (i, s) in sectors.Elements)
                    if (i < sectors.Count && s.Get<float>("Time") is float t && t > 0f) r.Sectors[i] = t;
            }
        }
    }
}
