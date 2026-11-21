using System.Text.Json;
using ACRLiveTiming.Decode;
using ACRLiveTiming.Model;
using ACRLiveTiming.Submission;
using Xunit;

namespace ACRLiveTiming.Tests;

public class StageConditionsTests
{
    private const string First = "MonteCarloS1BolleneCut1Forward";
    private const string Second = "MonteCarloS2SisteronCut1Reverse";
    private const string Third = "MonteCarloS1BolleneCut2Reverse";

    private static StageWeatherOptions Weather(float start, int seed, int target = 0)
        => new() { StartTime = start, Seed = seed, TargetType = target };

    private static RallyCalendar Calendar() => new("MontecarloWeekendShort", "", false,
    [
        new CalendarStage(First, 0, "ServiceParkDefault", Weather(28800, 11837)),
        new CalendarStage(Second, 0, "NoZone", Weather(54000, 15582, target: 5)),
        new CalendarStage(Third, 0, "ServiceParkDefault", Weather(64800, 910))
    ]);

    private static List<ForecastSlot> Forecast(float start)
        => [.. Enumerable.Range(1, 4).Select(i => new ForecastSlot(start + 600 * i, 600 * i, 0.1f, 0.2f, 0f, 0f, 0.3f, 0f, 2.1f, 0))];

    // The first stage of the weekend is run, then the game loads the second one.
    private static SessionMatrix Weekend()
    {
        var m = new SessionMatrix();
        var calendar = Calendar();
        m.SetRaceSettings(calendar.Stages[0].Weather, calendar, 0, First);
        m.SetForecast(Forecast(28800));
        m.SetLobbyPhase("Start sequence");
        m.AddColumn("run1", "SS1 " + First);
        m.JoinWeekend("run1", "a", 0, 3);
        Fixtures.Finish(m, "run1", "Driver One", [100.0, 200.0], 0);
        m.SetLobbyPhase("Results");
        return m;
    }

    [Fact]
    public void TheNextStageComesFromTheCalendarOnceTheLastOneIsOver()
    {
        var m = Weekend();
        var view = m.BuildView();
        Assert.Equal(1, view.CurrentGroup);
        var next = view.NextStage;
        Assert.NotNull(next);
        Assert.Equal(1, next.Index);
        Assert.Equal(3, next.Count);
        Assert.Equal(Second, next.RouteId);
        Assert.Equal("15:00", next.Weather.Start);
        Assert.Equal("WT_LIGHT_RAIN", next.Weather.TargetType);
        Assert.False(next.ServicePark);
        Assert.Empty(next.Forecast);   // still the first stage's forecast

        // the game loads the second stage: its settings, then its forecast
        var calendar = Calendar();
        m.SetRaceSettings(calendar.Stages[1].Weather, calendar, 1, Second);
        m.SetForecast(Forecast(54000));
        next = m.BuildView().NextStage;
        Assert.NotNull(next);
        Assert.Equal(4, next.Forecast.Count);
        Assert.Equal("15:10", next.Forecast[0].Time);

        // while the second stage runs, the next one is the third, without a forecast
        m.SetLobbyPhase("Start sequence");
        m.AddColumn("run2", "SS2 " + Second);
        m.JoinWeekend("run2", "a", 1, 3);
        m.SetLobbyPhase("Racing");
        next = m.BuildView().NextStage;
        Assert.NotNull(next);
        Assert.Equal(2, next.Index);
        Assert.True(next.ServicePark);
        Assert.Empty(next.Forecast);
        Assert.Equal(3, next.Rally.Count);
        Assert.Equal("run1", next.Rally[0].ColumnId);
        Assert.Equal("run2", next.Rally[1].ColumnId);
        Assert.Null(next.Rally[2].ColumnId);
    }

    [Fact]
    public void ARallyIsNamedAfterItsWeekend()
    {
        var m = Weekend();
        Assert.Equal("Monte Carlo Weekend Short", m.BuildView().AllStages[0].GroupName);

        // the lobby runs the weekend again: a second rally of the same name
        m.SetLobbyPhase("Start sequence");
        m.AddColumn("run2", "SS2 " + Second);
        m.JoinWeekend("run2", "a", 1, 3);
        m.AddColumn("run3", "SS3 " + First);
        m.JoinWeekend("run3", "a", 0, 3);
        var stages = m.BuildView().AllStages;
        Assert.Equal("Monte Carlo Weekend Short", stages[1].GroupName);
        Assert.Equal("Monte Carlo Weekend Short 2", stages[2].GroupName);
    }

    [Fact]
    public void AnotherWeekendStartsAnotherRallyFromTheSamePlace()
    {
        var m = Weekend();
        // a new weekend, its weather drawn anew, from its first stage again
        var other = Calendar() with { PresetId = "SwedenWeekendShort" };
        m.SetRaceSettings(other.Stages[0].Weather, other, 0, First);
        m.SetLobbyPhase("Start sequence");
        m.AddColumn("run2", "SS2 " + First);
        m.JoinWeekend("run2", "a", 0, 3);
        var stages = m.BuildView().AllStages;
        Assert.NotEqual(stages[0].Group, stages[1].Group);
    }

    [Fact]
    public void AWeekendRunAgainStartsAnotherRally()
    {
        var m = Weekend();
        // the same calendar relaunched after its first stage: the game emptied
        // its classification, so the decoder names another run of the weekend
        m.SetLobbyPhase("Start sequence");
        m.AddColumn("run2", "SS2 " + First);
        m.JoinWeekend("run2", "b", 0, 3);
        var stages = m.BuildView().AllStages;
        Assert.NotEqual(stages[0].Group, stages[1].Group);
    }

    [Fact]
    public void TheNextStageHasNoForecastBeforeItLoads()
    {
        // the second stage starts at the same time of day as the first
        var calendar = Calendar() with
        {
            Stages = [.. Calendar().Stages.Select((s, i) => i == 1 ? s with { Weather = Weather(28800, 15582) } : s)]
        };
        var m = new SessionMatrix();
        m.SetRaceSettings(calendar.Stages[0].Weather, calendar, 0, First);
        m.SetForecast(Forecast(28800));
        m.SetLobbyPhase("Start sequence");
        m.AddColumn("run1", "SS1 " + First);
        m.JoinWeekend("run1", "a", 0, 3);
        m.SetLobbyPhase("Results");
        var next = m.BuildView().NextStage;
        Assert.NotNull(next);
        Assert.Equal(1, next.Index);
        Assert.Empty(next.Forecast);
        Assert.Null(next.Rally[1].Forecast);
    }

    [Fact]
    public void AStageRunBeforeTheAppStartedIsNotTheNextOne()
    {
        // the app starts during the last stage: settings, no column
        var m = new SessionMatrix();
        m.SetLobbyPhase("Racing");
        var calendar = Calendar();
        m.SetRaceSettings(calendar.Stages[2].Weather, calendar, 2, Third);
        m.SetLobbyPhase("Results");
        Assert.Null(m.BuildView().NextStage);

        // the second one instead: the third comes next, not yet loaded
        m = new SessionMatrix();
        m.SetLobbyPhase("Racing");
        m.SetRaceSettings(calendar.Stages[1].Weather, calendar, 1, Second);
        m.SetLobbyPhase("Results");
        var next = m.BuildView().NextStage;
        Assert.NotNull(next);
        Assert.Equal(2, next.Index);
        Assert.False(next.Loaded);
    }

    [Fact]
    public void TheHeaderKeepsTheRunningStagesConditions()
    {
        var m = Weekend();
        Assert.False(m.BuildView().NextStage!.Loaded);   // the first stage's settings still

        var calendar = Calendar();
        m.SetRaceSettings(calendar.Stages[1].Weather, calendar, 1, Second);
        Assert.True(m.BuildView().NextStage!.Loaded);

        m.SetLobbyPhase("Start sequence");
        m.AddColumn("run2", "SS2 " + Second);
        m.JoinWeekend("run2", "a", 1, 3);
        m.SetLobbyPhase("Racing");
        Assert.False(m.BuildView().NextStage!.Loaded);
    }

    [Fact]
    public void EachStageKeepsTheForecastItStartedWith()
    {
        var m = Weekend();
        var view = m.BuildView();
        var current = view.CurrentConditions;
        Assert.NotNull(current);
        Assert.Equal(0, current.Index);
        Assert.Equal("08:00", current.Weather.Start);
        Assert.Equal(4, current.Forecast.Count);
        var rally = view.NextStage!.Rally;
        Assert.Equal(4, rally[0].Forecast!.Count);
        Assert.Null(rally[1].Forecast);   // not loaded yet

        var calendar = Calendar();
        m.SetRaceSettings(calendar.Stages[1].Weather, calendar, 1, Second);
        m.SetForecast(Forecast(54000));
        rally = m.BuildView().NextStage!.Rally;
        Assert.Equal("08:10", rally[0].Forecast![0].Time);
        Assert.Equal("15:10", rally[1].Forecast![0].Time);
        Assert.Null(rally[2].Forecast);
    }

    [Fact]
    public void TheCalendarListsTheStagesStillToCome()
    {
        var cal = Assert.Single(Weekend().BuildView().Calendars);
        Assert.Equal(1, cal.Group);
        Assert.Equal("run1", cal.Stages[0].ColumnId);
        Assert.Null(cal.Stages[1].ColumnId);
        Assert.Null(cal.Stages[2].ColumnId);
        Assert.True(cal.Stages[2].ServicePark);
    }

    [Fact]
    public void ARallyIsSentWithItsCalendarAndEachStagesConditions()
    {
        var root = JsonDocument.Parse(ResultsPayload.ForFile(Weekend().BuildSubmission(1), DateTime.UtcNow)).RootElement;
        var stage = root.GetProperty("stages")[0];
        Assert.Equal(1, stage.GetProperty("calendarOrder").GetInt32());
        Assert.Equal("08:00", stage.GetProperty("inGameStartTime").GetString());
        var conditions = stage.GetProperty("conditions");
        Assert.Equal("08:00", conditions.GetProperty("startTime").GetString());
        Assert.Equal("WT_CLEAR", conditions.GetProperty("weatherType").GetString());
        Assert.Equal("Optimal", conditions.GetProperty("startingGripLevel").GetString());
        Assert.Equal(11837, conditions.GetProperty("seed").GetInt32());
        Assert.Equal(4, stage.GetProperty("forecast").GetArrayLength());
        Assert.Equal("08:10", stage.GetProperty("forecast")[0].GetProperty("time").GetString());

        var calendar = root.GetProperty("calendar");
        Assert.Equal("MontecarloWeekendShort", calendar.GetProperty("presetId").GetString());
        Assert.Equal(3, calendar.GetProperty("stages").GetArrayLength());
        var second = calendar.GetProperty("stages")[1];
        Assert.Equal(Second, second.GetProperty("routeId").GetString());
        Assert.Equal("NoZone", second.GetProperty("preRaceZone").GetString());
        Assert.Equal("WT_LIGHT_RAIN", second.GetProperty("conditions").GetProperty("targetWeatherType").GetString());
    }

    [Fact]
    public void EveryForecastSlotIsRead()
    {
        // two slots as the game sends them: handles k*11+1 .. k*11+11, the last one
        // a 192-bit location
        var w = new BitWriter();
        w.Bits(0, 3);   // whatever precedes the table
        for (int k = 0; k < 2; k++)
        {
            int h = k * 11;
            w.Packed(h + 1); w.Float(28800 + 600 * (k + 1));
            w.Packed(h + 2); w.Float(600 * (k + 1));
            float[] conditions = [0.1f, 0.2f, 0f, 0.05f, 0.3f, 0f, 6.2f - k];
            for (int j = 0; j < 7; j++) { w.Packed(h + 3 + j); w.Float(conditions[j]); }
            w.Packed(h + 10); w.Bits(5, 4);
            w.Packed(h + 11); w.Bits(0, 192);
        }
        w.Packed(0);

        var slots = WeatherForecast.Slots(w.Bytes());
        Assert.NotNull(slots);
        Assert.Equal(2, slots.Count);
        Assert.Equal(29400f, slots[0].Time);
        Assert.Equal(5, slots[1].Type);
        Assert.Equal(5.2f, slots[1].Temperature, 3);
        Assert.Equal(0.2f, slots[0].Rain, 3);
    }

    [Fact]
    public void AFasterTimeSpreadsTheForecastOut()
    {
        // time running fast: slots of a hundred in-game minutes, up to 400
        var w = new BitWriter();
        for (int k = 0; k < 4; k++)
        {
            int h = k * 11;
            w.Packed(h + 1); w.Float(36000 + 6000 * (k + 1));
            w.Packed(h + 2); w.Float(6000 * (k + 1));
            for (int j = 0; j < 7; j++) { w.Packed(h + 3 + j); w.Float(j == 6 ? 11.5f : 0.1f); }
            w.Packed(h + 10); w.Bits(0, 4);
            w.Packed(h + 11); w.Bits(0, 192);
        }
        w.Packed(0);
        var slots = WeatherForecast.Slots(w.Bytes());
        Assert.NotNull(slots);
        Assert.Equal(4, slots.Count);

        var m = new SessionMatrix();
        m.SetRaceSettings(Weather(36000, 19918) with { TimeSpeed = 3 }, null, -1, First);
        m.SetForecast(slots);
        var next = m.BuildView().NextStage;
        Assert.NotNull(next);
        Assert.Equal(4, next.Forecast.Count);
        Assert.Equal("11:40", next.Forecast[0].Time);

        // a table for a stage starting at another time is not this one's
        m = new SessionMatrix();
        m.SetRaceSettings(Weather(39600, 10906) with { TimeSpeed = 3 }, null, -1, Second);
        m.SetForecast(slots);
        Assert.Empty(m.BuildView().NextStage!.Forecast);

        // nor the stage before's, when the next one starts at the same time
        m = new SessionMatrix();
        m.SetRaceSettings(Weather(36000, 19918) with { TimeSpeed = 3 }, null, -1, First);
        m.SetForecast(slots);
        m.SetRaceSettings(Weather(36000, 2471) with { TimeSpeed = 3 }, null, -1, Second);
        Assert.Empty(m.BuildView().NextStage!.Forecast);
    }

    private sealed class BitWriter
    {
        private readonly List<bool> _bits = [];
        public void Bits(ulong value, int count) { for (int i = 0; i < count; i++) _bits.Add(((value >> i) & 1) != 0); }
        public void Float(float value) => Bits((uint)BitConverter.SingleToInt32Bits(value), 32);
        public void Packed(int value)
        {
            do
            {
                int part = value & 0x7f;
                value >>= 7;
                Bits((ulong)((part << 1) | (value != 0 ? 1 : 0)), 8);
            } while (value != 0);
        }
        public byte[] Bytes()
        {
            var bytes = new byte[(_bits.Count + 7) / 8 + 8];
            for (int i = 0; i < _bits.Count; i++) if (_bits[i]) bytes[i >> 3] |= (byte)(1 << (i & 7));
            return bytes;
        }
    }
}
