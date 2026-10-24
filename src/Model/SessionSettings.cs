namespace ACRLiveTiming.Model;

/// <summary>The organiser's choices on the board, kept in a session file next to
/// its packets: unticked stages, rally groups with their names, and the runIds the
/// results submission sends (so a loaded session sends under the same ones).
/// Stages are named by their column id ("run3"), which the packets rebuild the
/// same way.</summary>
public sealed class SessionSettings
{
    public List<string> Discarded { get; set; } = [];
    public Dictionary<string, int> Groups { get; set; } = [];
    public Dictionary<int, string> GroupNames { get; set; } = [];
    public Dictionary<int, string> GroupRunIds { get; set; } = [];
    public string? SessionRunId { get; set; }
}
