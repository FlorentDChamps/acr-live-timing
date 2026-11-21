using ACRLiveTiming.Decode;
using ACRLiveTiming.Model;
using Xunit;

namespace ACRLiveTiming.Tests;

public class StageEndTests
{
    [Fact]
    public void ACarSpawnedForTheNextStageIsNotADnfOnTheEndingOne()
    {
        var m = new SessionMatrix();
        m.AddColumn("run1", "SS1 AlsaceS4SaverneFullForward");
        m.NameCarFromIdent(1, "Driver One");
        m.NameCarFromIdent(2, "Driver Two");
        // One drives off and never finishes; Two's car is already on the next
        // stage's start line when the run-start event arrives.
        m.UpdateCarProgress([(1, 800f, RaceStateWire.PhaseRunning, 1), (2, 0f, RaceStateWire.PhasePre, -1)]);
        m.ResetCarProgress();
        m.AddColumn("run2", "SS2 AlsaceS4SaverneFullReverse");
        Fixtures.Finish(m, "run2", "Driver Two", [100.0, 200.0], 0);

        var rows = m.BuildView().Rows;
        Assert.True(rows.Single(r => r.Driver == "Driver One").RawCells[0]?.R);
        Assert.Null(rows.Single(r => r.Driver == "Driver Two").RawCells[0]);
    }
}
