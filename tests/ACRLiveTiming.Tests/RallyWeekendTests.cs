using System.Text.Json;
using ACRLiveTiming.Model;
using ACRLiveTiming.Submission;
using Xunit;

namespace ACRLiveTiming.Tests;

public class RallyWeekendTests
{
    private static Dictionary<string, int> Groups(SessionMatrix m)
        => m.BuildView().AllStages.ToDictionary(stage => stage.Id, stage => stage.Group);

    private static SessionMatrix Columns(params string[] ids)
    {
        var m = new SessionMatrix();
        foreach (string id in ids) m.AddColumn(id, "SS AlsaceS4SaverneFullForward");
        return m;
    }

    [Fact]
    public void TheStagesOfAWeekendMakeOneRally()
    {
        var m = Columns("run1", "run2", "run3", "run4");
        m.JoinWeekend("run1", "a", -1, 0);
        m.JoinWeekend("run2", "a", -1, 0);
        m.JoinWeekend("run3", "b", -1, 0);

        var groups = Groups(m);
        Assert.Equal(groups["run1"], groups["run2"]);
        Assert.NotEqual(0, groups["run1"]);
        Assert.NotEqual(groups["run1"], groups["run3"]);
        Assert.Equal(0, groups["run4"]);
    }

    [Fact]
    public void TheCalendarEndsOneRallyAndStartsTheNext()
    {
        // Joined on the last stage of a three-stage weekend, which the lobby then
        // runs again from its first stage.
        var m = Columns("run1", "run2", "run3", "run4", "run5");
        m.JoinWeekend("run1", "a", 2, 3);
        m.JoinWeekend("run2", "a", 0, 3);
        m.JoinWeekend("run3", "a", 1, 3);
        m.JoinWeekend("run4", "a", 2, 3);
        m.JoinWeekend("run5", "a", 0, 3);

        var groups = Groups(m);
        Assert.Equal(0, groups["run1"]);
        Assert.NotEqual(0, groups["run2"]);
        Assert.Equal(groups["run2"], groups["run3"]);
        Assert.Equal(groups["run2"], groups["run4"]);
        Assert.NotEqual(0, groups["run5"]);
        Assert.NotEqual(groups["run2"], groups["run5"]);
    }

    [Fact]
    public void TheHostsUngroupingStands()
    {
        var m = Columns("run1", "run2", "run3");
        m.JoinWeekend("run1", "a", -1, 0);
        m.JoinWeekend("run2", "a", -1, 0);
        m.UngroupStages(["run2"]);
        m.JoinWeekend("run2", "a", -1, 0);   // later packets of the same stage
        m.JoinWeekend("run3", "a", -1, 0);

        var groups = Groups(m);
        Assert.Equal(0, groups["run2"]);
        Assert.NotEqual(0, groups["run3"]);
        Assert.NotEqual(groups["run1"], groups["run3"]);
    }

    [Fact]
    public void TheWeekendsClassificationIsKeptPerDriverAndStage()
    {
        var m = Columns("run1", "run2");
        m.JoinWeekend("run1", "a", 0, 3);
        m.JoinWeekend("run2", "a", 1, 3);
        Fixtures.Finish(m, "run1", "Driver One", [100.0, 200.0], 0);
        Fixtures.Finish(m, "run2", "Driver One", [90.0, 180.0], 0);
        Fixtures.Finish(m, "run2", "Driver Two", [95.0, 190.0], 0);
        // Driver Two missed the first stage: the classification counts it. Unknown
        // drivers and stages without a column are left out.
        m.SetWeekendResults("a", [("Driver One", 1, 30.0, 210.0), ("Driver Two", 0, 300.0, 500.0),
            ("Nobody", 1, 10.0, 200.0), ("Driver One", 2, 5.0, 100.0)]);

        var rows = m.BuildView().Rows;
        var one = rows.Single(r => r.Driver == "Driver One");
        Assert.Null(one.RawCells[0]!.P);
        Assert.Equal(30.0, one.RawCells[1]!.P);
        Assert.Equal(210.0, one.RawCells[1]!.C);
        var missed = rows.Single(r => r.Driver == "Driver Two").RawCells[0];
        Assert.NotNull(missed);
        Assert.True(missed.R);
        Assert.Equal(500.0, missed.C);

        var root = JsonDocument.Parse(ResultsPayload.ForFile(m.BuildSubmission(Groups(m)["run1"]), DateTime.UtcNow)).RootElement;
        var participants = root.GetProperty("participants").EnumerateArray().ToList();
        var cells = participants.Single(p => p.GetProperty("usernameAtRun").GetString() == "Driver One").GetProperty("stages");
        Assert.False(cells[0].TryGetProperty("rallyPenaltyMs", out _));
        Assert.Equal(30000, cells[1].GetProperty("rallyPenaltyMs").GetInt64());
        Assert.Equal(210000, cells[1].GetProperty("rallyTimeMs").GetInt64());
        var two = participants.Single(p => p.GetProperty("usernameAtRun").GetString() == "Driver Two").GetProperty("stages");
        Assert.Equal("not_started", two[0].GetProperty("status").GetString());
        Assert.Equal(500000, two[0].GetProperty("rallyTimeMs").GetInt64());
    }
}
