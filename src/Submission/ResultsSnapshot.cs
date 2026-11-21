using ACRLiveTiming.Decode;

namespace ACRLiveTiming.Submission;

public enum CellStatus { Finished, Dnf, NotStarted }

/// <summary>One stage of a selection, with what is needed to describe it to a
/// server. <see cref="StartTimestamp"/> is the Stopwatch time the run started, when
/// it started during this run of the app (null for a stage of a loaded session).</summary>
public sealed class SnapshotStage
{
    public string ColumnId { get; init; } = "";
    public string Label { get; init; } = "";
    public string? RouteId { get; init; }       // raw wire route, never the catalog id
    public string Name { get; init; } = "";
    public double? LengthKm { get; init; }
    public long? StartTimestamp { get; init; }
    public DateTime? StartedAtUtc { get; init; }
    public string? InGameStartTime { get; init; }
    public string? Weather { get; init; }
    public int? CalendarIndex { get; init; }                  // place in the weekend's calendar
    public StageWeatherOptions? Conditions { get; init; }    // weather options it was run with
    public IReadOnlyList<ForecastSlot>? Forecast { get; init; }
}

public sealed class SnapshotCell
{
    public CellStatus Status { get; init; }
    public long? TimeMs { get; init; }
    public long? PenaltyMs { get; init; }
    public List<long> SectorsMs { get; init; } = [];
    public long? RallyPenaltyMs { get; init; }   // added by the rally weekend's classification
    public long? RallyTimeMs { get; init; }      // the whole time that classification counts
    public string? CarId { get; init; }
    public string? CarName { get; init; }
}

public sealed class SnapshotDriver
{
    public string? SteamId64 { get; init; }
    public string? EosPuid { get; init; }
    public string UsernameAtRun { get; init; } = "";
    public string? Nation { get; init; }
    public string? NationName { get; init; }
    public List<SnapshotCell> Cells { get; init; } = [];   // aligned with Stages
}

/// <summary>
/// The facts of one selection (a rally group, or every ticked stage), taken from
/// the board under its lock. <see cref="Problems"/> lists what blocks a send;
/// <see cref="Warnings"/> what the preview only reports.
/// </summary>
public sealed class ResultsSnapshot
{
    public string RunId { get; init; } = "";
    public int Group { get; init; }
    public string? GroupName { get; init; }
    public RallyCalendar? Calendar { get; init; }   // the rally weekend the group was run in
    public List<SnapshotStage> Stages { get; init; } = [];
    public List<SnapshotDriver> Drivers { get; init; } = [];
    public List<string> Problems { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}
