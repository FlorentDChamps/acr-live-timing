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
    /// A newly-locked server whose IP differs from the last one resets the session
    /// (new lobby); reconnecting to the same IP keeps the accumulated history.
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
        volatile bool _infoRunning;
        // bumped on every session reset: an enrichment snapshot started before a reset
        // must not write the OLD lobby's nations/cars into the freshly reset matrix
        volatile int _resetGen;
        int _tickCounter;
        // streaming finish detector: fed every packet inline, so finishes are revealed
        // as the data flows (pace-independent) instead of on a wall-clock refresh.
        readonly RaceStateTracker _tracker = new();
        // streaming nation/car extractor: decodes each packet once (results scan +
        // content blocks) and retains only the marked blocks + the time->pseudo map —
        // no rolling payload buffer, no periodic full re-decode. It also produces the
        // per-packet driver results for the matrix (single scan).
        readonly NationCarTracker _natCar = new();
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
            if (ip != _lastServerIp)
            {
                _resetGen++;
                Matrix.Reset();
                ResetRunState();
                Log?.Invoke($"New server {ip}:{port} — classification reset.");
            }
            else
            {
                Log?.Invoke($"Reconnected to {ip}:{port} — history kept.");
            }
            ServerIp = ip;
            ServerPort = port;
            _lastServerIp = ip;
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

            // periodic nation/car refresh (background, ~every 6s when new data)
            if (++_tickCounter % 6 == 0) RefreshInfoAsync();
        }

        /// <summary>
        /// Resolve each driver's nation + car from the tracker's accumulated state on
        /// a background thread (the join is a vote over a few hundred retained blocks,
        /// not a re-decode), then push into the matrix. No-op if already running or
        /// nothing new since the last pass.
        /// </summary>
        public void RefreshInfoAsync()
        {
            // check + claim: Tick (UI thread) is the only caller, but keep the guard
            // so a slow snapshot can't overlap the next tick's.
            if (_infoRunning || !_natCar.Dirty) return;
            _infoRunning = true;
            int gen = _resetGen;
            Task.Run(() =>
            {
                try
                {
                    var info = _natCar.Snapshot();
                    if (gen != _resetGen) return;   // session was reset mid-snapshot
                    foreach (var kv in info)
                        Matrix.SetDriverInfo(kv.Key, kv.Value.nation, kv.Value.car);
                    // NB: finish detection is NOT here — it's streamed per packet in Feed
                    // via _tracker. This background pass is nation/car only.
                }
                catch { /* best-effort enrichment */ }
                finally { _infoRunning = false; }
            });
        }

        void Feed(byte[] payload)
        {
            // finish detection, streamed per packet: the tracker keeps its own decode
            // state, so it reports finishes the moment they occur, glued to the data
            // stream and independent of replay pacing (no wall-clock refresh). Cheap —
            // one packet parsed once, not the whole buffer re-decoded. Guarded: this runs
            // on the packet thread (sniffer callback), so a throw on a malformed packet
            // must not kill the feed — best-effort, skip finish update for this packet.
            try
            {
                var (newFinish, present) = _tracker.Feed(payload);   // this packet's only
                Matrix.SetFinishTimes(newFinish, present);
                // car-tagged times (sector splits + finishes) bind NetGUID -> driver
                // name by exact raw match; live spline distances feed the progression
                // bar. Splits name the marker at the first sector of each run.
                // spawn-time identity: the car's owner PlayerState replicates
                // steamid+pseudo in its spawn bunch each stage, so markers are named
                // at spawn — before the start. The full EOS display name also teaches
                // the persona alias (results carry the short form). Time-match naming
                // below stays as the fallback (e.g. lock-on mid-stage, spawn missed).
                foreach (var (carId, driver) in _tracker.NewCarNames)
                {
                    _natCar.LearnAlias(driver);
                    Matrix.NameCarFromIdent(carId, driver);
                }
                foreach (var (carId, time) in _tracker.NewSplitPairs)
                    Matrix.AddCarTime(carId, time);
                foreach (var (carId, time) in _tracker.NewFinishPairs)
                    Matrix.AddCarTime(carId, time);
                if (_tracker.Progress.Count > 0)
                    Matrix.UpdateCarProgress(_tracker.Progress);
            }
            catch { /* best-effort finish detection */ }

            // the 8 bit-shifted views + FStrings are computed ONCE per packet and
            // shared by every consumer below (run-start detection, stage-name scan,
            // result scan) — they all need the same shifts.
            var (shifted, fstrs) = Primitives.ShiftScan(payload);
            var strings = fstrs[0].ConvertAll(f => f.Text);

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
            //   * base level name — byte-aligned, broadcast at map load and in later
            //     bursts. Authoritative base for the current run.
            //   * route VARIANT (…Cut2Reverse) — appears bit-shifted only, at exactly two
            //     moments: at JOIN, replicating the stage in progress (falls INSIDE the
            //     post-run-start window => it is the CURRENT run's route), and during a
            //     stage's results phase as the NEXT stage pre-loads, BEFORE its run-start
            //     event (falls OUTSIDE the window => it is the NEXT run's route).
            //   * a RESTART of the same stage broadcasts nothing at all (no map travel)
            //     => the route is inherited (see StartNewRun).
            var anchor = Names.StageNameIn(strings);
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
            // one pass over every shifted view serves both the route-variant scan
            // (shifts 1-7 only: variants never appear byte-aligned) and the lobby
            // FSM phase scan (all shifts: the transition tokens appear anywhere).
            string? fsm = null;
            for (int sh = 0; sh < 8; sh++)
            {
                foreach (var f in fstrs[sh])
                {
                    var s = f.Text;
                    if (sh > 0 && Names.IsVariant(s))
                    {
                        if (_stageScanBudget > 0) _routeVariant = s;
                        else _pendingVariant = s;
                    }
                    var phase = PhaseLabel(s);
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
            if (_currentBase != null)
            {
                // the variant labels the run only while it extends the current base; a
                // stale variant from another level is simply not displayed (never nulled:
                // the matching base may just not have arrived yet).
                string label = (_routeVariant != null
                                && _routeVariant.Length > _currentBase.Length
                                && _routeVariant.StartsWith(_currentBase, StringComparison.Ordinal))
                    ? _routeVariant : _currentBase;
                if (label != _currentStage)
                {
                    _currentStage = label;
                    Matrix.SetLabel(runKey, RunLabel(_runIndex, label));
                }
            }

            // one scan serves both: per-driver results for the matrix AND the nation/car
            // tracker's accumulators (time->pseudo map + marked content blocks). On a
            // provisional (mid-stage) run we still run the scan for its accumulators and
            // the naming scratch below, but publish NO results (the stage is uncounted).
            foreach (var (name, total, raw, sectors) in _natCar.Feed(payload, shifted, fstrs))
                if (!_provisional) Matrix.AddResult(runKey, name, total, raw, sectors);
            // naming scratch: every (name, raw) incl. 1-sector partials — binds a car
            // marker to its driver at the FIRST split. The table receives S1 only from
            // the separately validated results-component scan.
            // Kept even when provisional: recording pseudo↔car bindings early is the point.
            foreach (var (name, raw) in _natCar.NameRaws)
                Matrix.AddSeenRaw(name, raw);
            // A one-sector partial is enough to prove the driver started, but is not a
            // final time: add only the row/name here. AddResult remains sector/finish
            // gated and fills the cell later. Provisional mid-stage captures stay out
            // of the counted standings, as before.
            if (!_provisional)
                foreach (var name in _natCar.StartedNames)
                    Matrix.MarkDriverStarted(name);
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
            // keepIdentity: leave BOTH trackers running. _natCar keeps its nation/car
            // votes and name aliases; _tracker keeps the RaceStateData NetGUID map, so
            // the cars currently driving stay decoded (their channels opened before this
            // reset and will NOT re-export until they respawn next stage). Resetting them
            // here would strand nations and live progression exactly as a late capture
            // start does. It ALSO keeps the current stage identity: a restart of the same
            // stage re-broadcasts no name, so clearing it would strand the reset column as
            // a bare "SSn" until the lobby happens to travel to a different map.
            if (!keepIdentity)
            {
                _currentStage = null;
                _currentBase = null;
                _routeVariant = null;
                _tracker.Reset();
                _natCar.Reset();
            }
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
                _resetGen++;
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
