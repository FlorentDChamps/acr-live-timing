namespace ACRLiveTiming.Submission;

public enum CellStatus { Finished, Dnf, NotStarted }

/// <summary>One stage of a selection, with what is needed to describe it to a
/// server. <see cref="StartTimestamp"/> is the Stopwatch time the run started.</summary>
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
}

public sealed class SnapshotCell
{
    public CellStatus Status { get; init; }
    public long? TimeMs { get; init; }
    public long? PenaltyMs { get; init; }
    public List<long> SectorsMs { get; init; } = [];
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
    public List<SnapshotStage> Stages { get; init; } = [];
    public List<SnapshotDriver> Drivers { get; init; } = [];
    public List<string> Problems { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}
