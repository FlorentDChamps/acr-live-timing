using System.Text.Json.Serialization;
using ACRLiveTiming.Decode;

namespace ACRLiveTiming.Model
{
    // ---- serializable view sent to the web page (System.Text.Json) ----------

    // Ungated per-cell wire data (one per AllStages column, null if the driver has no
    // entry there). This is ALL the timing the host publishes: reveal gating, penalty
    // substitution, totals and ranking are the page's job, computed from this block
    // under whatever settings the viewer is using. Shipping a pre-rendered board too
    // would mean two implementations of the same rules kept in sync by hand.
    // T=measured time, F=matched a real finish-timer peak (IsFinished), S=sector count,
    // R=the driver's car never reached the finish phases on this stage (retired,
    // disqualified, or vanished mid-run) — the page must never promote their last
    // split to a final via sector-count fallback.
    public sealed class RawCell
    {
        public double T { get; set; }
        public bool F { get; set; }
        public int S { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool R { get; set; }
    }

    public sealed class RowView
    {
        public string Driver { get; set; } = "";
        public bool Retired { get; set; }     // car is in Retire/Disqualify on the RUNNING stage
        public string? Nation { get; set; }   // token, e.g. "France" (null if unknown)
        public List<RawCell?> RawCells { get; set; } = new();  // ungated, aligned to AllStages
        // CarId per stage, aligned to AllStages. The page selects and de-duplicates
        // these client-side, so its list follows the viewer's selected stages.
        public List<string?> Cars { get; set; } = new();
    }

    public sealed class StageInfo
    {
        public string Id { get; set; } = "";      // stable column key (UI checkboxes)
        public string Name { get; set; } = "";    // display label, e.g. "SS1 AlsaceS4Saverne"
        public bool Discarded { get; set; }
        public int Group { get; set; }             // 0 = ungrouped; positive values are host group ids
        public string GroupName { get; set; } = ""; // host-set rally name (empty = default "Rally N")
    }

    public sealed class CarProgressView
    {
        public string Name { get; set; } = "";  // driver, or "Car N" until identified
        public bool Named { get; set; }         // false => placeholder label
        public double Dist { get; set; }        // metres along the stage spline (live)
        public bool Finished { get; set; }      // crossed the line (frozen at the finish)
        public bool Out { get; set; }           // retired / disqualified
        public int Pos { get; set; }            // live rally standing (h7; -1 = unknown)
    }

    public sealed class MatrixView
    {
        public List<StageInfo> AllStages { get; set; } = new();  // every run column (for UI checkboxes)
        public List<RowView> Rows { get; set; } = new();         // arrival order; the page sorts
        public double Pct { get; set; }
        public string State { get; set; } = "";
        public string Phase { get; set; } = "";        // lobby FSM phase ("Racing", "Results", …)
        public string CurrentStage { get; set; } = ""; // last known current-stage label
        public string Title { get; set; } = "";         // operator-set page title (empty = hidden)
        public string Description { get; set; } = "";    // operator-set page description (empty = hidden)
        public string StageStart { get; set; } = "";   // IN-GAME time of day of the stage start (HH:mm)
        public string StageWeather { get; set; } = ""; // forecast for the stage ("Light rain · 0.5°C")
        public List<CarProgressView> Progress { get; set; } = new(); // live cars, sorted by dist desc
        public double ProgressWindowKm { get; set; }    // width of the sliding progression window (km)
        // Host defaults + regime flag, so a web viewer's settings panel can seed its
        // controls and its "reset" can restore the host's configuration.
        public bool HasRaceState { get; set; }   // finish-timer stream exists (enables finish gating)
        public bool FinishGating { get; set; }   // host's finish-gating setting (viewer default)
        public string Version { get; set; } = "";  // host app version, shown in the page footer
    }

    /// <summary>
    /// Session-wide classification: drivers (rows) × run columns. Each run of a
    /// stage is its own column, keyed by a stable id ("run1", "run2", …) and shown
    /// with a label ("SS1 &lt;stage&gt;"). Times are final decoded seconds. A driver
    /// missing a run is counted at the per-run penalty cap (slowest completed time ×
    /// (1 + Pct)) and flagged Substituted. Total = sum of effective
    /// times, re-sorted every update. Thread-safe; raises <see cref="Changed"/>.
    /// </summary>
    public sealed class SessionMatrix
    {
        /// <summary>Float32 tolerance (seconds) for binding a car marker (NetGUID) to a
        /// driver by matching one of its split/finish times to a result row's raw.
        /// Deliberately tighter than <see cref="RaceStateTracker.FinishMatchTolerance"/>:
        /// a false match here prints the wrong driver's name on a car, so it must be
        /// near-exact. Two drivers within this tolerance are left unbound (anonymous)
        /// rather than guessed — see the ambiguity guard in TryNameCar.</summary>
        const double NameMatchTolerance = 0.05;

        readonly object _lock = new();
        readonly List<string> _columnOrder = new();                        // run ids, in order
        readonly Dictionary<string, string> _labels = new();               // id -> display label
        // id -> driver -> (display total, raw stage time, sector count). A driver is
        // revealed once their raw time matches a real finish (RaceStateData timer peak),
        // or, when no finish data is available, once their sector count reaches the
        // column max (fallback gating).
        readonly Dictionary<string, Dictionary<string, (double time, double raw, int sectors)>> _times = new();
        readonly List<string> _driverOrder = new();
        // penalty-free finish times (raw) reported by the RaceStateData timer; a result
        // is a real FINISH iff its raw time matches one of these. Each entry is tagged
        // with the column that was current when it was detected (null = observed before
        // any column existed, matches anywhere): without the tag, a DNF's last split
        // landing within tolerance of ANOTHER stage's finish would read as a finish.
        // _hasRaceState = the timer component exists at all: if true we gate
        // strictly on finishes (and reveal nothing before the first car crosses the line,
        // avoiding the pre-first-finisher flash); if false (older capture with no timer)
        // we fall back to sector-count gating.
        readonly List<(double time, string? column)> _finishTimes = new();
        // columnId -> drivers whose car never reached the finish phases by the time the
        // run rolled (Retire/Disqualify, or vanished mid-stage — rage quit / disconnect).
        // Snapshot taken once per column in ResetCarProgress; lets the page's fallback
        // gating and DNF display work on CLOSED stages, where _carsLive is long gone.
        readonly Dictionary<string, HashSet<string>> _dnfByColumn = new();
        bool _hasRaceState;
        // ---- live progression (RaceStateData h9, one entry per car NetGUID) ----
        // guid -> live state for the CURRENT run (cleared on every run start)
        sealed class CarState { public double Dist; public int Phase = -1; public int Pos = -1; public double Raised; public DateTime LastSeen; public bool Retired; }
        readonly Dictionary<long, CarState> _carsLive = new();
        // guid -> driver name, learned when one of the car's times (sector split or
        // finish, both penalty-free) matches a result row's raw uniquely. Splits give
        // the name at the FIRST sector; car actors are respawned per stage (new
        // NetGUIDs), so the binding is re-learned each run the same way.
        readonly Dictionary<long, string> _carNames = new();
        readonly Dictionary<long, List<double>> _carPendingRaws = new(); // not-yet-matched car times
        readonly Dictionary<string, List<double>> _seenRaws = new();     // driver -> raws, CURRENT run
        // previous run's raws: a car's sectors array is re-broadcast at SPAWN with the
        // PREVIOUS stage's splits still in it — matching against these names every
        // returning driver on the starting grid, before anyone moves.
        Dictionary<string, List<double>> _seenRawsPrev = new();
        readonly Dictionary<long, int> _carSeq = new();                  // guid -> "Car N" number
        long _version;
        readonly HashSet<string> _discarded = new();                       // ids
        readonly Dictionary<string, int> _groups = new();                  // id -> group id (0/absent = ungrouped)
        readonly Dictionary<int, string> _groupNames = new();              // group id -> host-set label
        readonly Dictionary<string, string?> _nations = new();
        // Latest known car is carried to the next stage when its first time arrives;
        // the per-column map preserves the car actually used on each stage.
        readonly Dictionary<string, string> _lastCars = new();
        readonly Dictionary<string, Dictionary<string, string>> _carsByColumn = new();
        readonly Dictionary<string, (string steamId, string eosPuid)> _identities = new();
        // Results may initially arrive before the PlayerState identity block. Until
        // then the display name is its own key; once the stable account IDs arrive,
        // all accumulated data is migrated to an account key. Aliases keep resolving
        // to that same key, so a later pseudo change updates the label, not the row.
        readonly Dictionary<string, string> _driverKeysByName = new();
        readonly Dictionary<string, string> _driverLabels = new();
        const string AccountKeyPrefix = "\u001faccount:";
        double _pct = 0.50;
        double _progressWindowKm = 0.8;   // MAX span of the auto-fitting progression window (km)
        bool _finishGating = true;   // true: hide splits, reveal only real finishes
                                     //       (RaceStateData timer). false: sector-gate
                                     //       (older behaviour — reveal on full sector chain).

        public string ServerLabel { get; set; } = "";
        public string StateLabel { get; set; } = "";
        string _lobbyPhase = "";
        // Kept independently of the result columns: a manual reset clears those
        // columns while the game may not re-broadcast the current map name.
        string _currentStage = "";
        string _stageStart = "";
        string _stageWeather = "";
        string _pageTitle = "";        // operator-set page heading (persists across resets)
        string _pageDescription = "";  // operator-set page blurb (persists across resets)

        public event Action? Changed;

        /// <summary>Monotonic change counter, bumped on every <see cref="Changed"/>.
        /// Lets callers detect "did anything change across this call?" (replay pacing)
        /// or validate a cached serialization without subscribing to the event.</summary>
        public long Version => Interlocked.Read(ref _version);

        void RaiseChanged()
        {
            Interlocked.Increment(ref _version);
            Changed?.Invoke();
        }

        // Caller holds _lock.
        string ResolveDriverKey(string displayName)
        {
            if (_driverKeysByName.TryGetValue(displayName, out var key)) return key;
            _driverKeysByName[displayName] = displayName;
            _driverLabels.TryAdd(displayName, displayName);
            return displayName;
        }

        // Caller holds _lock. Fold the temporary/name key into a stable account key.
        void MergeDriver(string from, string to)
        {
            if (from == to) return;
            int position = _driverOrder.IndexOf(from);
            if (position >= 0)
            {
                _driverOrder.RemoveAt(position);
                if (!_driverOrder.Contains(to)) _driverOrder.Insert(Math.Min(position, _driverOrder.Count), to);
            }
            foreach (var column in _times.Values)
                if (column.Remove(from, out var old))
                    if (!column.TryGetValue(to, out var current)
                        || old.sectors > current.sectors
                        || (old.sectors == current.sectors && old.time > current.time + 0.01))
                        column[to] = old;

            if (_nations.Remove(from, out var nation) && !_nations.ContainsKey(to)) _nations[to] = nation;
            if (_lastCars.Remove(from, out var lastCar) && !_lastCars.ContainsKey(to)) _lastCars[to] = lastCar;
            foreach (var dnf in _dnfByColumn.Values)
                if (dnf.Remove(from)) dnf.Add(to);
            foreach (var cars in _carsByColumn.Values)
                if (cars.Remove(from, out var car) && !cars.ContainsKey(to)) cars[to] = car;
            MergeRaws(_seenRaws, from, to);
            MergeRaws(_seenRawsPrev, from, to);
            foreach (var carId in _carNames.Where(p => p.Value == from).Select(p => p.Key).ToList())
                _carNames[carId] = to;
            if (_identities.Remove(from, out var identity) && !_identities.ContainsKey(to)) _identities[to] = identity;
            foreach (var alias in _driverKeysByName.Where(p => p.Value == from).Select(p => p.Key).ToList())
                _driverKeysByName[alias] = to;
            _driverLabels.Remove(from);
        }

        // Caller holds _lock.
        static void MergeRaws(Dictionary<string, List<double>> sets, string from, string to)
        {
            if (!sets.Remove(from, out var source)) return;
            if (!sets.TryGetValue(to, out var target)) sets[to] = target = new List<double>();
            foreach (var raw in source)
                if (!target.Exists(existing => Math.Abs(existing - raw) < 0.01)) target.Add(raw);
        }

        /// <summary>Lobby FSM phase for the page header ("Racing", "Results", …).</summary>
        public void SetLobbyPhase(string phase)
        {
            lock (_lock)
            {
                if (_lobbyPhase == phase) return;
                _lobbyPhase = phase;
            }
            RaiseChanged();
        }

        /// <summary>IN-GAME time of day the stage starts at ("HH:mm", from the
        /// replicated weather timeline; "" = not broadcast in this lobby).</summary>
        public void SetStageStart(string start)
        {
            lock (_lock)
            {
                if (_stageStart == start) return;
                _stageStart = start;
            }
            RaiseChanged();
        }

        /// <summary>Stage weather forecast label ("Light rain · 0.5°C"; "" = none
        /// broadcast). From the replicated MainWeatherForecaster table.</summary>
        public void SetStageWeather(string weather)
        {
            lock (_lock)
            {
                if (_stageWeather == weather) return;
                _stageWeather = weather;
            }
            RaiseChanged();
        }

        /// <summary>Create a new run column (no-op if the id already exists).</summary>
        public void AddColumn(string id, string label)
        {
            lock (_lock)
            {
                if (_times.ContainsKey(id)) return;
                _times[id] = new Dictionary<string, (double, double, int)>();
                _labels[id] = label;
                _columnOrder.Add(id);
                _currentStage = label;
            }
            RaiseChanged();
        }

        /// <summary>Update a run column's display label (e.g. once the stage name lands).</summary>
        public void SetLabel(string id, string label)
        {
            lock (_lock)
            {
                if (!_labels.TryGetValue(id, out var current) || current == label) return;
                _labels[id] = label;
                if (_columnOrder.Count > 0 && _columnOrder[^1] == id) _currentStage = label;
            }
            RaiseChanged();
        }

        public bool ColumnHasResults(string id)
        {
            lock (_lock) return _times.TryGetValue(id, out var column) && column.Count > 0;
        }

        /// <summary>Record a driver's latest identity data. A car is attached to the
        /// current race column only while racing; service-park changes are retained
        /// for the next column instead of rewriting the stage just completed.</summary>
        public void SetDriverInfo(string? columnId, string driver, string? nation, string? car)
        {
            bool changed = false;
            lock (_lock)
            {
                var key = ResolveDriverKey(driver);
                _nations.TryGetValue(key, out var currentNation);
                if (currentNation != nation)
                {
                    _nations[key] = nation;
                    changed = true;
                }
                if (!string.IsNullOrWhiteSpace(car))
                {
                    if (!_lastCars.TryGetValue(key, out var last) || last != car)
                    {
                        _lastCars[key] = car;
                        changed = true;
                    }
                    if (columnId != null)
                    {
                        if (!_carsByColumn.TryGetValue(columnId, out var columnCars))
                            _carsByColumn[columnId] = columnCars = new Dictionary<string, string>();
                        if (!columnCars.TryGetValue(key, out var stageCar) || stageCar != car)
                        {
                            columnCars[key] = car;
                            changed = true;
                        }
                    }
                }
            }
            if (changed) RaiseChanged();
        }

        /// <summary>Associate a result/display name with the stable account IDs
        /// received in its PlayerState identity block. The account key also prevents
        /// a pseudo change from splitting one driver into multiple result rows.</summary>
        public void SetDriverIdentity(string driver, string steamId, string eosPuid)
        {
            if (string.IsNullOrWhiteSpace(driver) || string.IsNullOrWhiteSpace(steamId)
                || string.IsNullOrWhiteSpace(eosPuid)) return;
            bool changed = false;
            lock (_lock)
            {
                var currentKey = ResolveDriverKey(driver);
                var accountKey = AccountKeyPrefix + steamId + "|" + eosPuid;
                // Only fold a temporary name-key into the account key. When the name
                // already resolves to a DIFFERENT account (two players sharing a
                // pseudonym, or one renamed to another's pseudonym), merging would
                // meld two real players' results — leave that row untouched and just
                // repoint the alias to the most recent claimant.
                if (currentKey == accountKey || !currentKey.StartsWith(AccountKeyPrefix, StringComparison.Ordinal))
                    MergeDriver(currentKey, accountKey);
                _driverKeysByName[driver] = accountKey;
                if (!_driverLabels.TryGetValue(accountKey, out var label) || label != driver)
                {
                    _driverLabels[accountKey] = driver;
                    changed = true;
                }
                if (!_identities.TryGetValue(accountKey, out var current)
                    || current.steamId != steamId || current.eosPuid != eosPuid)
                {
                    _identities[accountKey] = (steamId, eosPuid);
                    changed = true;
                }
            }
            if (changed) RaiseChanged();
        }

        // Caller holds _lock. A stage may begin without the CarId re-replicating when
        // the driver keeps the same car, so inherit the last known value at first split.
        bool AttachLastCar(string columnId, string driver)
        {
            if (!_lastCars.TryGetValue(driver, out var car)) return false;
            if (!_carsByColumn.TryGetValue(columnId, out var columnCars))
                _carsByColumn[columnId] = columnCars = new Dictionary<string, string>();
            if (columnCars.ContainsKey(driver)) return false;
            columnCars[driver] = car;
            return true;
        }

        public void AddResult(string columnId, string driver, double time, double raw, int sectors)
        {
            bool changed = false;
            lock (_lock)
            {
                driver = ResolveDriverKey(driver);
                if (!_times.TryGetValue(columnId, out var column))
                {
                    column = new Dictionary<string, (double, double, int)>();
                    _times[columnId] = column;
                    _labels[columnId] = columnId;
                    _columnOrder.Add(columnId);
                    changed = true;
                }
                if (!_driverOrder.Contains(driver)) { _driverOrder.Add(driver); changed = true; }
                changed |= AttachLastCar(columnId, driver);
                // Prefer the furthest sector reached. Within the same sector keep the
                // largest cumulative total (latest penalty/result update). Sector count
                // must win over value: a structurally valid S1 can still carry a stale
                // larger float which must never block the later S2/S3 final.
                if (!column.TryGetValue(driver, out var prev)
                    || sectors > prev.sectors
                    || (sectors == prev.sectors && time > prev.time + 0.01))
                {
                    column[driver] = (time, raw, sectors);
                    changed = true;
                }
            }
            if (changed) RaiseChanged();
        }

        /// <summary>Add a standings row as soon as a driver's first split is observed,
        /// without storing that partial as a stage result. The time cell remains empty
        /// until the normal finish-gating path reveals a complete result.</summary>
        public void MarkDriverStarted(string driver)
        {
            bool changed = false;
            lock (_lock)
            {
                driver = ResolveDriverKey(driver);
                if (!_driverOrder.Contains(driver))
                {
                    _driverOrder.Add(driver);
                    changed = true;
                }
            }
            if (changed) RaiseChanged();
        }

        /// <summary>
        /// Feed the naming scratch: one (driver, penalty-free cumulative raw) pair —
        /// 1-sector partials INCLUDED (the table path never sees those). Each new raw
        /// retries the pending unnamed cars, so a car is bound at the driver's FIRST
        /// split. Raws belong to the current run (results always stream for the run
        /// in progress; stale rebroadcasts land in the prev-run scratch semantics
        /// anyway via the run-start roll).
        /// </summary>
        public void AddSeenRaw(string driver, double raw)
        {
            if (raw <= 0) return;
            bool changed = false;
            lock (_lock)
            {
                driver = ResolveDriverKey(driver);
                if (!_seenRaws.TryGetValue(driver, out var seen))
                    _seenRaws[driver] = seen = new List<double>();
                if (!seen.Exists(r => Math.Abs(r - raw) < 0.01))
                {
                    seen.Add(raw);
                    foreach (var carId in _carPendingRaws.Keys.ToList())
                        if (TryNameCar(carId)) changed = true;
                }
            }
            if (changed) RaiseChanged();
        }

        /// <summary>
        /// Merge newly detected raw finish times into the accumulated set. The tracker
        /// reports each finish once, in the packet that detects it, so
        /// <paramref name="times"/> is usually empty and the dedup scan only runs on
        /// new entries. Once seen, a finish sticks until the session resets.
        /// <paramref name="present"/> records whether a RaceStateData timer stream
        /// exists at all (false => the view falls back to sector-gating).
        /// </summary>
        public void SetFinishTimes(List<double> times, bool present)
        {
            bool changed = false;
            lock (_lock)
            {
                if (present != _hasRaceState) { _hasRaceState = present; changed = true; }
                // Tag with the current run's column (last opened: StartNewRun adds the
                // column before any finish of that run can be detected). Dedup within
                // the same tag only — the same raw value on two different stages is
                // two distinct finishes.
                var column = _columnOrder.Count > 0 ? _columnOrder[^1] : null;
                foreach (var t in times)
                    if (!_finishTimes.Exists(f => f.column == column
                        && Math.Abs(f.time - t) < RaceStateTracker.FinishMatchTolerance))
                    {
                        _finishTimes.Add((t, column));
                        changed = true;
                    }
            }
            if (changed) RaiseChanged();
        }

        // raw time matches a real finish (RaceStateData timer peak) of THIS column
        // within tolerance (null-tagged peaks predate any column and match anywhere)
        bool IsFinished(string columnId, double raw) => _finishTimes.Exists(f =>
            (f.column == null || f.column == columnId)
            && Math.Abs(f.time - raw) < RaceStateTracker.FinishMatchTolerance);

        /// <summary>
        /// Push this packet's live per-car updates (spline distance + race phase).
        /// Change notifications are throttled: a car must advance ≥20 m (or change
        /// phase) to bump the version, so the 1 Hz web poll re-serializes at most
        /// that often instead of on every replication delta.
        /// </summary>
        public void UpdateCarProgress(IReadOnlyList<(long id, float dist, int phase, int pos)> updates)
        {
            bool changed = false;
            lock (_lock)
            {
                foreach (var (id, dist, phase, pos) in updates)
                {
                    if (!_carsLive.TryGetValue(id, out var car))
                    {
                        _carsLive[id] = car = new CarState();
                        changed = true;
                    }
                    if (!_carSeq.ContainsKey(id)) _carSeq[id] = _carSeq.Count + 1;
                    if (phase != car.Phase) { car.Phase = phase; changed = true; }
                    // STICKY retired flag: after Retire/Disqualify the phase moves on
                    // (None/Inited/Resetting once the player is back in the lobby) while
                    // the actor keeps replicating — recomputing "out" from the current
                    // phase would un-retire the frozen car and let it pin the progress
                    // bar's tail edge. Only actually racing again (Pre..Post) clears it.
                    if (phase == RaceStateWire.PhaseRetire || phase == RaceStateWire.PhaseDisqualify)
                    {
                        if (!car.Retired) { car.Retired = true; changed = true; }
                    }
                    else if (phase >= RaceStateWire.PhasePre && phase <= RaceStateWire.PhasePost)
                    {
                        if (car.Retired) { car.Retired = false; changed = true; }
                    }
                    if (pos > 0) car.Pos = pos;
                    car.LastSeen = DateTime.UtcNow;
                    car.Dist = dist;
                    if (Math.Abs(car.Dist - car.Raised) >= 20) { car.Raised = car.Dist; changed = true; }
                    // an ident-named driver enters the standings once their car has
                    // actually RUN (past the start line) — not at spawn: idle lobby
                    // members / spectators also have a named car actor, and a row for
                    // someone who never starts would pollute the table
                    if (dist > 50 && _carNames.TryGetValue(id, out var drv) && !_driverOrder.Contains(drv))
                    {
                        _driverOrder.Add(drv);
                        changed = true;
                    }
                }
            }
            if (changed) RaiseChanged();
        }

        /// <summary>
        /// Record a car's penalty-free time — a cumulative sector split or the finish,
        /// tagged with the car's NetGUID — and try to bind the car to a driver name:
        /// the value matches exactly ONE driver's result raw of the current run.
        /// Unmatched times are kept — the result row may simply not have arrived yet
        /// (AddResult retries the match).
        /// </summary>
        public void AddCarTime(long id, double raw)
        {
            bool changed;
            lock (_lock)
            {
                if (_carNames.ContainsKey(id)) return;
                if (!_carPendingRaws.TryGetValue(id, out var raws))
                {
                    // bound the guid count too: actors respawn with fresh NetGUIDs
                    // every stage, so never-named orphans accrue run after run —
                    // evict the oldest entry (roughly insertion order)
                    if (_carPendingRaws.Count >= 512)
                        _carPendingRaws.Remove(_carPendingRaws.Keys.First());
                    _carPendingRaws[id] = raws = new List<double>();
                }
                raws.Add(raw);
                if (raws.Count > 64) raws.RemoveAt(0);   // bound a never-named car
                changed = TryNameCar(id);
            }
            if (changed) RaiseChanged();
        }

        /// <summary>
        /// Bind a car marker to a driver from the spawn-time identity block (the
        /// steamid+pseudo strings on the car's owner PlayerState channel) — a
        /// deterministic bind, available BEFORE the start, unlike the time-match of
        /// <see cref="AddCarTime"/> which needs a first sector split. An existing
        /// binding for the guid is upgraded if the name differs (the identity name is
        /// the full EOS display name; a time-match may have bound the short persona).
        /// </summary>
        public void NameCarFromIdent(long id, string driver)
        {
            bool changed = false;
            lock (_lock)
            {
                driver = ResolveDriverKey(driver);
                if (!_carNames.TryGetValue(id, out var cur) || cur != driver)
                {
                    // a driver drives ONE car per run: drop a stale guid bound to the
                    // same name (previous stage's actor, or a rejoin)
                    foreach (var stale in _carNames.Where(p => p.Value == driver && p.Key != id)
                                                   .Select(p => p.Key).ToList())
                        _carNames.Remove(stale);
                    _carNames[id] = driver;
                    _carPendingRaws.Remove(id);
                    // NB: the driver is NOT added to the standings here — ident naming
                    // covers everyone with a car actor, including lobby members who
                    // never start. UpdateCarProgress promotes them to a standings row
                    // once the car actually runs.
                    changed = true;
                }
            }
            if (changed) RaiseChanged();
        }

        /// <summary>Roll the naming scratch and drop the live car states for a new
        /// run — the game respawns the car actors per stage (fresh NetGUIDs). The
        /// ending run's raws are KEPT one more run (see <see cref="_seenRawsPrev"/>),
        /// and pending car times survive: the next stage's cars spawn BEFORE the
        /// run-start event, and their spawn-time sectors arrays must still match.</summary>
        public void ResetCarProgress()
        {
            bool changed;
            lock (_lock)
            {
                changed = _carsLive.Count > 0;
                // The ending run is now definitively over: any named car that never
                // reached the finish phases is a DNF for that column — including cars
                // whose actor vanished mid-run (rage quit / disconnect emits no
                // Retire phase, the entry just stops updating). Snapshot it before
                // the live states are dropped; a car that FINISHED and then went back
                // to the lobby is already in Ended..Post here (the phase only regresses
                // once the next run's respawn replaces the actor, which is after this).
                if (_carsLive.Count > 0 && _columnOrder.Count > 0)
                {
                    var column = _columnOrder[^1];
                    if (!_dnfByColumn.TryGetValue(column, out var dnf))
                        _dnfByColumn[column] = dnf = new HashSet<string>();
                    foreach (var kv in _carsLive)
                        if (!(kv.Value.Phase >= RaceStateWire.PhaseEnded && kv.Value.Phase <= RaceStateWire.PhasePost)
                            && _carNames.TryGetValue(kv.Key, out var drv))
                            dnf.Add(drv);
                }
                _carsLive.Clear();
                _seenRawsPrev = new Dictionary<string, List<double>>(_seenRaws);
                _seenRaws.Clear();
            }
            if (changed) RaiseChanged();
        }

        // under _lock — bind car -> driver if its pending times match exactly ONE
        // driver's raws of the current OR previous run (±NameMatchTolerance: both
        // sides decode the same f32; uniqueness kills the rare same-split-time
        // collision — the next split retries). Returns true if a binding was made.
        bool TryNameCar(long id)
        {
            if (_carNames.ContainsKey(id) || !_carPendingRaws.TryGetValue(id, out var raws))
                return false;
            string? match = null;
            foreach (var set in new[] { _seenRaws, _seenRawsPrev })
                foreach (var kv in set)
                    foreach (var seen in kv.Value)
                        foreach (var raw in raws)
                            if (Math.Abs(seen - raw) < NameMatchTolerance)
                            {
                                if (match != null && match != kv.Key) return false;  // ambiguous
                                match = kv.Key;
                            }
            if (match == null) return false;
            // a driver drives ONE car per run: drop a stale guid previously bound to
            // the same name (previous stage's actor, or a rejoin)
            foreach (var stale in _carNames.Where(p => p.Value == match)
                                           .Select(p => p.Key).ToList())
                _carNames.Remove(stale);
            _carNames[id] = match;
            _carPendingRaws.Remove(id);
            // surface the driver in the standings the moment their marker is named,
            // even before they finish a stage: they get a row (penalty-substituted
            // cells until a real time lands), so the leaderboard mirrors the grid.
            if (!_driverOrder.Contains(match)) _driverOrder.Add(match);
            return true;
        }

        public void Reset()
        {
            lock (_lock)
            {
                _times.Clear();
                _labels.Clear();
                _columnOrder.Clear();
                _driverOrder.Clear();
                _discarded.Clear();
                _groups.Clear();
                _groupNames.Clear();
                _nations.Clear();
                _lastCars.Clear();
                _carsByColumn.Clear();
                _identities.Clear();
                _driverKeysByName.Clear();
                _driverLabels.Clear();
                _finishTimes.Clear();
                _dnfByColumn.Clear();
                _hasRaceState = false;
                _carsLive.Clear();
                _carNames.Clear();
                _carPendingRaws.Clear();
                _seenRaws.Clear();
                _seenRawsPrev = new Dictionary<string, List<double>>();
                _carSeq.Clear();
                _lobbyPhase = "";
                _currentStage = "";
                _stageStart = "";
                _stageWeather = "";
            }
            RaiseChanged();
        }

        /// <summary>
        /// Clear the results/timeline (leaderboard, columns, finish times, lobby/stage
        /// labels) AND the pre-reset live markers (<c>_carsLive</c>) so no stage data
        /// from before the reset keeps going out on /state. KEEP only the per-driver
        /// identity enrichment that cannot be reacquired mid-session: nation, latest
        /// car and account IDs (<c>_nations</c>/<c>_lastCars</c>/<c>_identities</c>), plus the pseudo↔car-marker
        /// bindings (<c>_carNames</c>/<c>_carSeq</c>)
        /// and the car-naming scratch (<c>_seenRaws</c>/<c>_carPendingRaws</c>). Those are
        /// learned only from the join burst (nation) and the channel-open export (car
        /// NetGUID), which do NOT repeat mid-session — so a full <see cref="Reset"/> in the
        /// middle of a lobby loses them until players reconnect. This soft reset is for the
        /// manual "Reset session" button: wipe the board, retain what can't be reacquired.
        /// The current stage label is also retained: the game sends it only on map
        /// changes, so clearing it would leave the freshly reset board unnamed.
        /// The kept bindings re-attach to the next replicated car states, so the emptied
        /// progression repopulates already-named the moment the stage resumes.
        /// </summary>
        public void ResetKeepIdentity()
        {
            lock (_lock)
            {
                _times.Clear();
                _labels.Clear();
                _columnOrder.Clear();
                _driverOrder.Clear();
                _discarded.Clear();
                _groups.Clear();
                _groupNames.Clear();
                _carsByColumn.Clear();
                _finishTimes.Clear();
                _dnfByColumn.Clear();
                _hasRaceState = false;
                _carsLive.Clear();   // drop pre-reset live positions (not published post-reset)
                _lobbyPhase = "";
                _stageStart = "";
                _stageWeather = "";
            }
            RaiseChanged();
        }

        public void SetDiscarded(string id, bool discard)
        {
            lock (_lock)
            {
                if (discard) _discarded.Add(id);
                else _discarded.Remove(id);
            }
            RaiseChanged();
        }

        /// <summary>Put the selected stage columns in one new group. Group ids are
        /// session-local labels for the UI/page; timings and totals are unaffected.</summary>
        public void GroupStages(IEnumerable<string> ids)
        {
            var selected = ids.Distinct().Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
            if (selected.Count == 0) return;
            lock (_lock)
            {
                var valid = selected.Where(id => _columnOrder.Contains(id)).ToList();
                if (valid.Count == 0) return;
                int next = _groups.Count == 0 ? 1 : _groups.Values.Max() + 1;
                foreach (var id in valid) _groups[id] = next;
                _groupNames[next] = $"Rally {next}";
                RemoveUnusedGroups();
            }
            RaiseChanged();
        }

        /// <summary>Set the published display name for a rally group. An empty name
        /// restores the stable default, <c>Rally N</c>.</summary>
        public void SetGroupName(int group, string? name)
        {
            string value = string.IsNullOrWhiteSpace(name) ? $"Rally {group}" : name.Trim();
            bool changed = false;
            lock (_lock)
            {
                if (!_groups.Values.Contains(group)) return;
                if (!_groupNames.TryGetValue(group, out var current) || current != value)
                {
                    _groupNames[group] = value;
                    changed = true;
                }
            }
            if (changed) RaiseChanged();
        }

        /// <summary>Remove the selected stage columns from whatever groups they are in.</summary>
        public void UngroupStages(IEnumerable<string> ids)
        {
            bool changed = false;
            lock (_lock)
            {
                foreach (var id in ids.Distinct()) changed |= _groups.Remove(id);
                if (changed) RemoveUnusedGroups();
            }
            if (changed) RaiseChanged();
        }

        // Single-stage rallies are valid too. Only clean up names whose group no
        // longer has any stage after an ungroup or a regroup operation.
        void RemoveUnusedGroups()
        {
            var used = _groups.Values.ToHashSet();
            foreach (var id in _groupNames.Keys.Where(id => !used.Contains(id)).ToList())
                _groupNames.Remove(id);
        }

        public double Pct
        {
            get { lock (_lock) return _pct; }
            set { lock (_lock) _pct = value; RaiseChanged(); }
        }

        /// <summary>Max span (km) of the auto-fitting live-progression window. The bar
        /// stretches to fit the running field [tail, leader]; this is the ceiling on
        /// that span (past it the leader stays right-pinned, further cars hidden).
        /// Operator-adjustable; served to the page.</summary>
        public double ProgressWindowKm
        {
            get { lock (_lock) return _progressWindowKm; }
            set { lock (_lock) _progressWindowKm = Math.Max(0.1, value); RaiseChanged(); }
        }

        /// <summary>
        /// true (default): reveal a cell only when its raw time matches a real finish
        /// (RaceStateData timer peak), hiding intermediate splits. false: reveal each
        /// driver's latest cumulative split as it arrives.
        /// </summary>
        public bool FinishGating
        {
            get { lock (_lock) return _finishGating; }
            set { lock (_lock) _finishGating = value; RaiseChanged(); }
        }

        /// <summary>Operator-set page title shown on the web page (empty = hidden).
        /// Page config, not session data: survives both resets, like <see cref="Pct"/>.</summary>
        public string PageTitle
        {
            get { lock (_lock) return _pageTitle; }
            set { var v = value ?? ""; lock (_lock) { if (_pageTitle == v) return; _pageTitle = v; } RaiseChanged(); }
        }

        /// <summary>Operator-set page description shown under the title (empty = hidden).</summary>
        public string PageDescription
        {
            get { lock (_lock) return _pageDescription; }
            set { var v = value ?? ""; lock (_lock) { if (_pageDescription == v) return; _pageDescription = v; } RaiseChanged(); }
        }

        public MatrixView BuildView()
        {
            lock (_lock)
            {
                var all = _columnOrder
                    .Select(id => new StageInfo
                    {
                        Id = id,
                        Name = _labels.TryGetValue(id, out var label) ? label : id,
                        Discarded = _discarded.Contains(id),
                        Group = _groups.TryGetValue(id, out var group) ? group : 0,
                        GroupName = _groups.TryGetValue(id, out group)
                            ? _groupNames.GetValueOrDefault(group, $"Rally {group}") : ""
                    })
                    .ToList();
                // Drivers whose car is currently retired/disqualified. This is a DIRECT
                // abandon signal, unlike the "column closed and a peer finished"
                // inference the page falls back on for past stages, so it lets the
                // RUNNING stage be marked DNF immediately. Scoped to the current run:
                // ResetCarProgress clears _carsLive at every run start, and the flag
                // clears itself if the car races again.
                var retired = new HashSet<string>();
                foreach (var kv in _carsLive)
                    if (kv.Value.Retired && _carNames.TryGetValue(kv.Key, out var rd))
                        retired.Add(rd);

                // One ungated row per driver: every column (INCLUDING discarded ones,
                // so a viewer can re-include a stage without a round trip), null where
                // the driver has no entry. No gating, no substitution, no ordering —
                // the page derives all of that. Row order here is arrival order and
                // carries no meaning; the page sorts.
                var rows = new List<RowView>(_driverOrder.Count);
                // Per-cell R: snapshot for closed columns (_dnfByColumn), live retired
                // state for the column of the run in progress (always the last one).
                var lastColumn = _columnOrder.Count > 0 ? _columnOrder[^1] : null;
                foreach (var driverKey in _driverOrder)
                {
                    var rawCells = new List<RawCell?>(_columnOrder.Count);
                    foreach (var id in _columnOrder)
                        rawCells.Add(_times[id].TryGetValue(driverKey, out var re)
                            ? new RawCell
                            {
                                T = re.time, F = IsFinished(id, re.raw), S = re.sectors,
                                R = (_dnfByColumn.TryGetValue(id, out var dnf) && dnf.Contains(driverKey))
                                    || (id == lastColumn && retired.Contains(driverKey))
                            }
                            : null);

                    var driver = _driverLabels.GetValueOrDefault(driverKey, driverKey);
                    _nations.TryGetValue(driverKey, out var nation);
                    var cars = new List<string?>(_columnOrder.Count);
                    foreach (var id in _columnOrder)
                        cars.Add(_carsByColumn.TryGetValue(id, out var columnCars)
                            && columnCars.TryGetValue(driverKey, out var car) ? car : null);
                    rows.Add(new RowView
                    {
                        Driver = driver,
                        Retired = retired.Contains(driverKey),
                        Nation = nation,
                        RawCells = rawCells,
                        Cars = cars
                    });
                }

                // live progression markers, leader first. A car that stopped
                // replicating mid-run (player left — its actor just dies, no Retire
                // phase) would freeze on the bar: prune unless it finished.
                var progress = new List<CarProgressView>(_carsLive.Count);
                var cutoff = DateTime.UtcNow.AddSeconds(-120);
                foreach (var kv in _carsLive)
                {
                    // every live car gets a marker (its POSITION is always useful);
                    // an as-yet-unidentified car shows the dot only, no name label —
                    // "Car N" is kept as a stable key/tooltip, not a shown label.
                    bool named = _carNames.TryGetValue(kv.Key, out var driverKey);
                    int ph = kv.Value.Phase;
                    bool fin = ph >= RaceStateWire.PhaseEnded && ph <= RaceStateWire.PhasePost;
                    if (!fin && kv.Value.LastSeen < cutoff) continue;
                    progress.Add(new CarProgressView
                    {
                        Name = named ? _driverLabels.GetValueOrDefault(driverKey!, driverKey!) : $"Car {_carSeq[kv.Key]}",
                        Named = named,
                        Dist = Math.Round(kv.Value.Dist, 1),
                        Finished = fin,
                        Out = kv.Value.Retired,
                        Pos = kv.Value.Pos
                    });
                }
                progress.Sort((a, b) => b.Dist.CompareTo(a.Dist));

                return new MatrixView
                {
                    AllStages = all,
                    Rows = rows,
                    Pct = _pct,
                    State = StateLabel,
                    Phase = _lobbyPhase,
                    CurrentStage = _currentStage,
                    StageStart = _stageStart,
                    StageWeather = _stageWeather,
                    Title = _pageTitle,
                    Description = _pageDescription,
                    Progress = progress,
                    ProgressWindowKm = _progressWindowKm,
                    HasRaceState = _hasRaceState,
                    FinishGating = _finishGating,
                    Version = AppInfo.Version
                };
            }
        }
    }
}
