using ACRLiveTiming.Decode;
using ACRLiveTiming.Net;

namespace ACRLiveTiming.Model
{
    public enum SnifferState { Sniffing, Connected }

    /// <summary>
    /// Orchestrates sniff → detect → decode → matrix. State machine:
    ///   Sniffing  : feed every inbound packet to the detector until a server locks.
    ///   Connected : decode only that server's packets; on ConnectTimeout with no
    ///               packet, drop back to Sniffing (the web view is KEPT).
    /// Locking a different server never clears the classification (a host migration
    /// or relay change mid-rally must not lose the standings) — only the operator's
    /// Reset button does; it only restarts the decoder, whose NetGUIDs and channels
    /// belong to the previous connection. Reconnecting to the same server keeps the
    /// decoder too: a packet gap is not a new connection and nothing is re-exported.
    /// </summary>
    public sealed class Engine
    {
        const double ConnectTimeoutSec = 5.0;
        const int PreBufferMax = 4000;   // packets kept during search, replayed on lock
        const int StageScanWindow = 4000; // packets after a run start during which a route
                                          // variant replicates the CURRENT stage (join burst);
                                          // outside it a variant is the NEXT stage pre-loading
        // tail of the weather actor's replication header, immediately followed by
        // FWeatherTimeline.StartTime (f32, in-game seconds-of-day) then 0x24 then the
        // elapsed timeline seconds (f32). Empirically stable across all probed lobbies
        // (MonteCarlo 14:00, Alsace 09:00, Wales 08:00); absent from some older captures.
        static readonly byte[] WeatherAnchor = { 0xE4, 0x81, 0xBD, 0x29, 0x84, 0x63, 0x20 };

        public SessionMatrix Matrix { get; } = new();
        public SnifferState State { get; private set; } = SnifferState.Sniffing;
        public string? ServerIp { get; private set; }
        public int ServerPort { get; private set; }

        readonly ServerDetector _detector = new();
        // Serializes the packet thread (OnPacket/Feed) against UI-thread resets
        // (ManualReset, Tick's timeout path): the trackers and detector keep plain
        // Dictionary/HashSet state and must never be mutated concurrently.
        readonly object _sync = new();
        readonly Queue<(string ip, int port, byte[] payload)> _preBuffer = new();
        // layout-driven replication decoder: participants (nation, car), player
        // states (identity, display name, their car components), race states (timer,
        // phase, position, distance), sector records, the rally result arrays and the
        // game state's travel track — every value read from the replicated properties.
        readonly ReplicationDecoder _rep = new();
        // finish detection over the decoded race states (see OnCarUpdate)
        sealed class CarFinishState { public float Rt = float.NaN; public int Phase = -1; public List<double> Peaks = new(); }
        readonly Dictionary<long, CarFinishState> _carFinish = new();
        readonly Dictionary<long, string> _carNamed = new();   // RaceStateData guid -> driver pushed to the matrix
        readonly HashSet<string> _identified = new();          // display names whose account ids were pushed
        int _currentRaceId = -1;                               // RaceId of the run in progress (from the cars' race states)
        string? _currentBase;                            // byte-aligned base level (authoritative)
        string? _pendingBase;                            // base level pre-loaded for the NEXT run
        string? _routeVariant;                           // route variant of the CURRENT run (null = plain base)
        string? _pendingVariant;                         // variant pre-loaded for the NEXT run
        int _stageScanBudget;                            // packets left in the post-run-start window
        string? _currentStage;
        string? _lobbyPhase;                             // last FSM.Flow.* label pushed to the matrix
        int _runIndex;
        string? _runKey;
        // true: the current run was opened by the mid-stage-join fallback (no run-start
        // event seen). Such a stage is already underway — we missed its beginning — so it
        // must NOT be counted: no column, no results. We still decode its name/route (the
        // next, real stage inherits it if unchanged) and build the pseudo↔car/nation
        // bindings. The first real run-start event supersedes it.
        bool _provisional;
        string? _lastServerIp;
        int _lastServerPort;
        DateTime _lastPacketUtc = DateTime.UtcNow;

        public event Action<string>? Log;
        public event Action<SnifferState>? StateChanged;

        public Engine()
        {
            // Surface a suspected-encryption notice (game update) through the normal log.
            _detector.SuspectedEncrypted += msg => Log?.Invoke(msg);
        }

        public void OnPacket(string srcIp, int srcPort, byte[] payload)
        {
            lock (_sync)
            {
                if (State == SnifferState.Sniffing)
                {
                    // Buffer pre-lock packets: the stage-name FString and the first
                    // finishers are emitted at map load, before the detector has locked
                    // on (it needs a few signature packets). Without this they'd be
                    // dropped and the column would stay "?" with the first run missing.
                    _preBuffer.Enqueue((srcIp, srcPort, payload));
                    if (_preBuffer.Count > PreBufferMax) _preBuffer.Dequeue();

                    if (_detector.Feed(srcIp, srcPort, payload, out var ip, out var port))
                        Connect(ip, port);
                    return;
                }

                // Connected: only the locked server's packets drive the classification.
                if (srcIp != ServerIp || srcPort != ServerPort) return;
                _lastPacketUtc = DateTime.UtcNow;
                Feed(payload);
            }
        }

        void Connect(string ip, int port)
        {
            if (ip != _lastServerIp || port != _lastServerPort)
            {
                // A different server (host migration, relay change, next lobby): the
                // standings are KEPT; the new server re-exports every object at join, so
                // the decoder restarts on its NetGUID space while identities, nations and
                // cars already in the matrix stay bound to their account ids.
                RestartDecoder();
                Log?.Invoke($"New server {ip}:{port} — history kept, decoder restarted.");
            }
            else
            {
                Log?.Invoke($"Reconnected to {ip}:{port} — history kept.");
            }
            ServerIp = ip;
            ServerPort = port;
            _lastServerIp = ip;
            _lastServerPort = port;
            _lastPacketUtc = DateTime.UtcNow;
            Matrix.ServerLabel = $"{ip}:{port}";
            SetState(SnifferState.Connected);

            // Replay the buffered packets from this server through the decoder, in
            // order, so nothing emitted before the lock is lost (stage name, run 1).
            foreach (var (bufferedIp, bufferedPort, bufferedPayload) in _preBuffer)
                if (bufferedIp == ip && bufferedPort == port)
                    Feed(bufferedPayload);
            _preBuffer.Clear();
        }

        /// <summary>Call ~1×/s from a UI timer to detect a dropped connection.</summary>
        public void Tick()
        {
            lock (_sync)
            {
                if (State == SnifferState.Connected
                    && (DateTime.UtcNow - _lastPacketUtc).TotalSeconds > ConnectTimeoutSec)
                {
                    Log?.Invoke("Server connection lost — back to searching (web view kept).");
                    _detector.Reset();
                    SetState(SnifferState.Sniffing);
                }
            }
        }

        void Feed(byte[] payload)
        {
            // Structural decode of this packet's replicated properties. Guarded: this
            // runs on the packet thread (sniffer callback), so a throw on a malformed
            // packet must not kill the feed — the packet is simply skipped.
            try
            {
                _rep.Feed(payload);
                ApplyReplication();
            }
            catch { /* best-effort: a malformed packet loses its own updates only */ }

            // the 8 bit-shifted views + FStrings are computed ONCE per packet and
            // shared by every consumer below (run-start detection, stage-name and FSM
            // token scans, weather anchor) — they all need the same shifts.
            var (shifted, fstrs) = Primitives.ShiftScan(payload);
            var strings = new List<string>();
            foreach (var view in fstrs)
                foreach (var f in view)
                    strings.Add(f.Text);

            // A run-start event opens a new column — but only once the current run
            // has finishers, so repeated semaphore events don't spawn blank columns.
            if (Names.HasRunStart(strings))
            {
                // open a counted column when: no run yet, the current run is only
                // provisional (mid-stage join — always superseded by the first real
                // start), or the current run already has finishers (guards against
                // repeated semaphore events spawning blank columns).
                if (_runKey == null || _provisional || Matrix.ColumnHasResults(_runKey))
                    StartNewRun();
            }
            // data before any run-start event = we locked on mid-stage. Track it, but do
            // NOT count it (see StartProvisionalRun / _provisional).
            if (_runKey == null) StartProvisionalRun();
            var runKey = _runKey!;

            // stage/route labelling. Two token sources with different timing (probed on
            // all captures via the --stagescan harness):
            //   * base level name — the persistent level's object path, byte-aligned,
            //     broadcast at map load and in later bursts. Authoritative base for the
            //     current run.
            //   * route VARIANT (<level><Full|Short<n>|Cut<n>><Forward|Reverse>) — the
            //     game state's travel/pending track FName, replicated at JOIN (the stage
            //     in progress, INSIDE the post-run-start window => the CURRENT run's
            //     route), when the host picks the next stage in the results hub and at
            //     the service-park load (OUTSIDE the window => the NEXT run's route). It
            //     is NOT re-sent when the stage map itself loads, so a client that joined
            //     before the capture started cannot recover the first stage's route.
            //     Any bit shift, including byte-aligned.
            //   * a RESTART of the same stage broadcasts nothing at all (no map travel)
            //     => the route is inherited (see StartNewRun).
            // A stage's FString is normally byte-aligned, but a host capture showed
            // its initial route (`AlsaceS4SaverneShort1Forward`) only at bit shifts
            // 4 and 6. `strings` combines every aligned view, like the variant/FSM scans
            // below, so the first stage is labelled even when no byte-aligned base
            // level is replicated to this client.
            var anchor = Names.StageBaseIn(strings);
            if (anchor != null)
            {
                // The base name re-broadcasts when the NEXT map pre-loads, which happens
                // DURING the current stage's Results phase — before its run-start event.
                // Once the current run has finishers its stage is DONE, so a base arriving
                // now belongs to the next run: defer it (mirrors the route-variant
                // _pendingVariant path) instead of overwriting the finished stage's label.
                // Before any finisher, the base IS the current run's.
                if (_runKey != null && Matrix.ColumnHasResults(_runKey))
                    _pendingBase = anchor;
                else
                    _currentBase = anchor;
            }
            if (_stageScanBudget > 0) _stageScanBudget--;
            // the lobby FSM phase tokens appear at any shift
            string? fsm = null;
            for (int sh = 0; sh < 8; sh++)
            {
                foreach (var f in fstrs[sh])
                {
                    var phase = PhaseLabel(f.Text);
                    if (phase != null) fsm = phase;
                }
            }
            if (fsm != null && fsm != _lobbyPhase)
            {
                _lobbyPhase = fsm;
                Matrix.SetLobbyPhase(fsm);
            }

            // in-game stage start time, from the replicated weather timeline
            // (FWeatherTimeline: [anchor][StartTime f32][0x24][elapsed f32] — the
            // anchor is the tail of the weather actor's replication header, stable
            // across every probed lobby). Broadcast periodically and re-emitted for
            // the next stage during results, so last-seen-wins tracks the current run.
            for (int sh = 0; sh < 8; sh++)
            {
                var d = shifted[sh];
                for (int o = 0; o + WeatherAnchor.Length + 9 <= d.Length; o++)
                {
                    if (d[o] != 0xE4) continue;
                    bool match = true;
                    for (int j = 1; j < WeatherAnchor.Length; j++)
                        if (d[o + j] != WeatherAnchor[j]) { match = false; break; }
                    if (!match || d[o + 11] != 0x24) continue;
                    float tod = BitConverter.ToSingle(d, o + 7);
                    // NB: NaN fails BOTH comparisons — guard it explicitly or
                    // TimeSpan.FromSeconds throws (this path is outside the try/catch)
                    if (float.IsNaN(tod) || tod < 0f || tod >= 86400f) continue;
                    var ts = TimeSpan.FromSeconds(tod);
                    Matrix.SetStageStart($"{ts.Hours:00}:{ts.Minutes:00}");
                }
            }

            // stage weather forecast (rare broadcast: service park / next-stage
            // pre-load; describes the upcoming stage — last-seen-wins, like the
            // start time and the route variant)
            var wx = WeatherForecast.FirstSlot(payload);
            if (wx != null)
                Matrix.SetStageWeather(
                    $"{WeatherForecast.TypeNames[wx.Value.type]} · {wx.Value.temp:0.#}°C");
            // the variant labels the run while it drives the current level (level aliases
            // included: "Weles…" routes on a "Wales…" level); a stale variant from another
            // level is simply not displayed (never nulled: the matching base may just not
            // have arrived yet). Before any base has been seen the variant stands alone.
            string? label = _routeVariant != null
                            && (_currentBase == null || Names.VariantExtends(_routeVariant, _currentBase))
                ? _routeVariant : _currentBase;
            if (label != null && label != _currentStage)
            {
                _currentStage = label;
                Matrix.SetLabel(runKey, RunLabel(_runIndex, label));
            }

            ApplyResults(runKey);
        }

        /// <summary>
        /// Push what the decoder learned from this packet into the matrix, except the
        /// results (which need the run key, see <see cref="ApplyResults"/>): identities
        /// and display names, nation + car, car markers named at spawn, live progression,
        /// sector splits, finishes and the travel track.
        /// </summary>
        void ApplyReplication()
        {
            // PlayerState: account ids + EOS display name. The results arrays key on
            // the participant id (the persona), the board shows the display name:
            // registering both against the same account folds them into one row.
            foreach (var pl in _rep.PlayerUpdates)
            {
                if (pl.Name == null || pl.Account is not var (steamId, eosPuid)) continue;
                if (!_identified.Add(pl.Name + "\u0001" + pl.ParticipantId)) continue;
                if (pl.ParticipantId != null && pl.ParticipantId != pl.Name)
                    Matrix.SetDriverIdentity(pl.ParticipantId, steamId, eosPuid);
                Matrix.SetDriverIdentity(pl.Name, steamId, eosPuid);
            }

            // RaceParticipantData: nation + car, bound to the participant id. A garage
            // change during Results belongs to the NEXT stage; during a race the CarId
            // is tied to this exact run.
            string? carColumn = !_provisional && (_lobbyPhase is "Racing" or "Finishing") ? _runKey : null;
            foreach (var p in _rep.ParticipantUpdates)
                if (p.Id != null && (p.Nation != null || p.CarId != null))
                    Matrix.SetDriverInfo(carColumn, p.Id, p.Nation, p.CarId);

            // RaceStateData: the car marker is named through its PlayerState (deterministic,
            // at spawn); its timer + phase drive finish detection; distance/phase/position
            // feed the live progression bar.
            var progress = new List<(long id, float dist, int phase, int pos)>();
            var finishes = new List<double>();
            foreach (var car in _rep.CarUpdates)
            {
                // The race in progress: the cars carry its RaceId while racing (-1 when
                // idle). Last-wins, not max — the id restarts from 0 with every event the
                // host picks, so a rally can run 0,1,2 then 0 again.
                if (car.RaceId >= 0) _currentRaceId = car.RaceId;
                var driver = _rep.DriverOfCar(car.Guid);
                if (driver != null && (!_carNamed.TryGetValue(car.Guid, out var named) || named != driver))
                {
                    _carNamed[car.Guid] = driver;
                    Matrix.NameCarFromIdent(car.Guid, driver);
                }
                OnCarUpdate(car, finishes);
                if (!float.IsNaN(car.Distance) || car.Phase >= 0 || car.Position > 0)
                    progress.Add((car.Guid, float.IsNaN(car.Distance) ? 0f : car.Distance, car.Phase, car.Position));
            }
            if (progress.Count > 0) Matrix.UpdateCarProgress(progress);
            Matrix.SetFinishTimes(finishes, _rep.Cars.Count > 0);
            foreach (var (rsd, time) in _rep.NewSectorTimes)
                Matrix.AddCarSplit(rsd, time);

            // GameState.TravelTrackId: the route of the stage in progress when it arrives
            // inside the post-run-start window (join burst), otherwise the NEXT stage's
            // (host pick in the results hub, service-park load).
            if (_rep.TrackChanged && _rep.TravelTrackId is string travel && Names.IsVariant(travel))
            {
                if (_stageScanBudget > 0) _routeVariant = travel;
                else _pendingVariant = travel;
            }
        }

        /// <summary>
        /// Finish detection from a car's decoded race state. Two signals, in priority
        /// order: PHASE — the car enters Ended/EndSequence/Post: its current RaceTime IS
        /// the finish (exact, event-driven); RESET — the RaceTime drops to ~0 (next stage
        /// begins): the previous sample was that stage's finish, a safety net for a car
        /// that finishes and leaves before its end phase replicates. Deduplicated per car.
        /// </summary>
        void OnCarUpdate(ReplicationDecoder.CarState car, List<double> finishes)
        {
            if (!_carFinish.TryGetValue(car.Guid, out var st)) _carFinish[car.Guid] = st = new CarFinishState();
            float rt = car.RaceTime;
            bool rtValid = !float.IsNaN(rt) && rt >= 0f && rt < RaceStateWire.FinishMax;
            void Peak(double v)
            {
                if (st.Peaks.Exists(f => Math.Abs(f - v) < RaceStateWire.FinishMatchTolerance)) return;
                st.Peaks.Add(v);
                finishes.Add(v);
                Matrix.AddCarTime(car.Guid, v);
            }
            if (rtValid)
            {
                if (!float.IsNaN(st.Rt) && rt < st.Rt * 0.5f && st.Rt > RaceStateWire.FinishMin) Peak(st.Rt);
                st.Rt = rt;
            }
            if (car.Phase >= 0 && car.Phase != st.Phase)
            {
                bool wasEnd = st.Phase >= RaceStateWire.PhaseEnded && st.Phase <= RaceStateWire.PhasePost;
                bool isEnd = car.Phase >= RaceStateWire.PhaseEnded && car.Phase <= RaceStateWire.PhasePost;
                if (isEnd && !wasEnd && !float.IsNaN(st.Rt) && st.Rt > RaceStateWire.FinishMin) Peak(st.Rt);
                st.Phase = car.Phase;
            }
        }

        /// <summary>
        /// Rally results for the run in progress. The live array (one element per
        /// participant: cumulative sector times, penalty, car) fills the column as
        /// sectors are passed; the per-race session array carries the FINAL entry with
        /// its DNF/DQ flags. On a provisional (mid-stage) run nothing is published:
        /// the stage is uncounted, but the raws still feed the car-naming scratch.
        /// </summary>
        void ApplyResults(string runKey)
        {
            foreach (var (source, r) in _rep.ResultUpdates)
            {
                // no car race state decoded yet (joined mid-stage): the entries themselves
                // are the only hint, the highest id seen being the race in progress
                if (_currentRaceId < 0 && r.RaceId > _currentRaceId) _currentRaceId = r.RaceId;
                if (source == ReplicationDecoder.ResultSource.Best) continue;
                if (source == ReplicationDecoder.ResultSource.Session && r.RaceId != _currentRaceId) continue;
                var splits = r.Splits.Select(v => (double)v).ToList();
                foreach (var v in splits) Matrix.AddSeenRaw(r.ParticipantId, v);
                if (_provisional) continue;
                // the entry names the car for this exact race, even for a participant
                // whose RaceParticipantData was never received (joined before the capture)
                if (r.CarId != null) Matrix.SetDriverCar(runKey, r.ParticipantId, r.CarId);
                if (r.Sectors.Count > 0) Matrix.MarkDriverStarted(r.ParticipantId);
                if (r.Raw is float raw)
                    Matrix.AddResult(runKey, r.ParticipantId, raw + r.Penalty, raw, r.Sectors.Count, splits);
                if (source == ReplicationDecoder.ResultSource.Session && r.Dnf)
                    Matrix.MarkDriverDnf(runKey, r.ParticipantId);
            }
        }

        /// <summary>
        /// Open a PROVISIONAL run for a mid-stage join (locked on with no run-start
        /// event): the stage is already underway, so it is never counted — no column,
        /// no results (see the _provisional guard on AddResult). We still open the
        /// stage-scan window and reset the live markers so the name/route decode and the
        /// pseudo↔car/nation bindings accumulate; the first real run-start supersedes it
        /// and inherits the decoded stage name if the lobby stayed on the same stage.
        /// </summary>
        string StartProvisionalRun()
        {
            _provisional = true;
            _runKey = "provisional";
            _stageScanBudget = StageScanWindow;
            Matrix.ResetCarProgress();
            return _runKey;
        }

        string StartNewRun()
        {
            _provisional = false;
            _runIndex++;
            var key = "run" + _runIndex;
            _runKey = key;
            // consume the pre-loaded route: a variant broadcast during the previous
            // stage's results phase belongs to THIS run. Nothing pending = restart of
            // the same stage (nothing is re-broadcast) -> the route is inherited.
            if (_pendingBase != null)
            {
                _currentBase = _pendingBase;
                _pendingBase = null;
            }
            if (_pendingVariant != null)
            {
                _routeVariant = _pendingVariant;
                _pendingVariant = null;
            }
            // reopen the window: a variant seen inside it replicates the stage in
            // progress (join case) and applies to THIS run, not the next.
            _stageScanBudget = StageScanWindow;
            // new stage: drop the live markers (frozen at the previous finish); they
            // repopulate as the cars replicate. Name bindings survive.
            Matrix.ResetCarProgress();
            Matrix.AddColumn(key, RunLabel(_runIndex, _currentStage));
            return key;
        }

        static string RunLabel(int index, string? stage)
            => string.IsNullOrEmpty(stage) ? $"SS{index}" : $"SS{index} {stage}";

        /// <summary>
        /// Friendly label for a lobby FSM transition token, or null if not one.
        /// The full state machine (probed on all captures via --interscan):
        /// WaitData→Init→[Spectating]→ReadyToRace→GoToRace→InitRace→PreRace→
        /// BeginStartSequence→StartSequence→PreSemaphore→PreSemaphoreCompleted→
        /// StartSemaphore→Semaphore→Race→BeginEndSequence→EndSequence→
        /// EndSequenceCompleted→PostRace→Results→ResultsHub→[ForceNextRace]→
        /// ServicePark→…→Exit/ExitToMain.
        /// </summary>
        static string? PhaseLabel(string s)
        {
            if (!s.StartsWith("FSM.Flow.", StringComparison.Ordinal)) return null;
            switch (s.Substring(9).Trim())   // Trim: the game emits "FSM.Flow. PreRace"
            {
                case "WaitData":
                case "Init": return "Loading";
                case "Spectating": return "Spectating";
                case "ReadyToRace":
                case "GoToRace":
                case "InitRace":
                case "PreRace":
                case "ForceNextRace": return "Loading next stage";
                case "BeginStartSequence":
                case "StartSequence": return "Start sequence";
                case "PreSemaphore":
                case "PreSemaphoreCompleted":
                case "StartSemaphore":
                case "ForceSemaphore":
                case "Semaphore": return "Semaphore";
                case "Race": return "Racing";
                case "BeginEndSequence":
                case "EndSequence":
                case "EndSequenceCompleted": return "Finishing";
                case "PostRace": return "Post race";
                case "Results":
                case "ResultsHub": return "Results";
                case "ServicePark": return "Service park";
                case "Exit":
                case "ExitToMain": return "Closed";
                default: return null;
            }
        }

        void ResetRunState(bool keepIdentity = false)
        {
            // NB: do NOT clear _preBuffer here — Connect() calls this before replaying
            // the buffered pre-lock packets (which carry the stage name and run 1).
            _lobbyPhase = null;
            _pendingBase = null;
            _pendingVariant = null;
            _stageScanBudget = 0;
            _runIndex = 0;
            _runKey = null;
            _provisional = false;
            // keepIdentity: leave the decoder running. It keeps the NetGUID map, the
            // participants and player states, so the cars currently driving stay decoded
            // (their channels opened before this reset and will NOT re-export until they
            // respawn next stage). Resetting it here would strand nations and live
            // progression exactly as a late capture start does. It ALSO keeps the current
            // stage identity: a restart of the same stage re-broadcasts no name, so
            // clearing it would strand the reset column as a bare "SSn" until the lobby
            // happens to travel to a different map.
            if (!keepIdentity)
            {
                _currentStage = null;
                _currentBase = null;
                _routeVariant = null;
                RestartDecoder();
            }
        }

        /// <summary>Drop the decoder's per-connection state (NetGUID map, channels,
        /// decoded actors and components, finish/naming scratch). The matrix is untouched.</summary>
        void RestartDecoder()
        {
            _rep.Reset();
            _carFinish.Clear();
            _carNamed.Clear();
            _identified.Clear();
            _currentRaceId = -1;
        }

        /// <summary>Clear the session. <paramref name="keepIdentity"/> keeps the
        /// hard-to-reacquire enrichment (nations, name aliases, pseudo↔progression
        /// bindings, live markers) — used by the manual "Reset session" button. A full
        /// reset (default) is for a new capture / new server, where those identities no
        /// longer apply.</summary>
        public void ManualReset(bool keepIdentity = false)
        {
            lock (_sync)
            {
                if (keepIdentity) Matrix.ResetKeepIdentity();
                else Matrix.Reset();
                ResetRunState(keepIdentity);
            }
            Log?.Invoke(keepIdentity ? "Manual reset (identities kept)." : "Manual reset.");
        }

        void SetState(SnifferState s)
        {
            State = s;
            Matrix.StateLabel = s == SnifferState.Connected ? "Connected" : "Searching server…";
            StateChanged?.Invoke(s);
        }
    }
}
