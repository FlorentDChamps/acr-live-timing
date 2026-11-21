using ACRLiveTiming.Model;
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
}
