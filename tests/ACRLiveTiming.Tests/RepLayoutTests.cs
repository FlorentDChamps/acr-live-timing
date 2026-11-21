using ACRLiveTiming.Decode;
using Xunit;

namespace ACRLiveTiming.Tests;

public class RepLayoutTests
{
    [Fact]
    public void ARallyWeekendRepeatsTheRallyLayoutsThenAddsItsOwn()
    {
        var rally = RepLayouts.ForWireName("RaceEventRallyResults");
        var weekend = RepLayouts.ForWireName("RaceEventRallyWeekendResults");
        Assert.NotNull(rally);
        Assert.NotNull(weekend);
        Assert.Equal(rally.Cmds.Select(c => c.Path), weekend.Cmds.Take(rally.Cmds.Count).Select(c => c.Path));
        Assert.Equal("ParticipantsWeekendResults", weekend.Cmds[^1].Path);

        var data = RepLayouts.ForWireName("RaceEventData");
        var weekendData = RepLayouts.ForWireName("RaceEventRallyWeekendData");
        Assert.NotNull(data);
        Assert.NotNull(weekendData);
        Assert.Equal(data.Cmds.Select(c => c.Path), weekendData.Cmds.Take(data.Cmds.Count).Select(c => c.Path));
        Assert.Equal("RaceSubEventIndex", weekendData.Cmds[^1].Path);
    }

    [Fact]
    public void TheWeekendSettingsCarryTheCalendar()
    {
        var settings = RepLayouts.ForWireName("RaceEventRaceSettingsRallyWeekend");
        Assert.NotNull(settings);
        // handles as the game sends them: 10, 16-30 and 42 onwards
        Assert.Equal("RaceOptions.CarDamage.bDamagesEnabled", settings.Cmds[9].Path);
        Assert.Equal("LobbyOptions.MaxPlayers", settings.Cmds[11].Path);
        Assert.Equal("LobbyOptions.CarSelectionOption", settings.Cmds[12].Path);
        Assert.Equal("RacePenaltyOptions.RacePenalty", settings.Cmds[37].Path);
        Assert.Equal("WeatherOptions.Preset.WeatherType", settings.Cmds[15].Path);
        Assert.Equal("WeatherOptions.StartingGripLevel", settings.Cmds[29].Path);
        Assert.Equal("RallyWeekendCalendar.CalendarPresetId", settings.Cmds[41].Path);
        var subEvents = settings.Cmds[42];
        Assert.Equal("RallyWeekendCalendar.RallyWeekendSubEvents", subEvents.Path);
        Assert.Equal(33, subEvents.ElementCmds!.Count);
        Assert.Equal("TrackId", subEvents.ElementCmds[1].Path);
        Assert.Equal("PreRaceZoneConfigurationId", subEvents.ElementCmds[^1].Path);
        Assert.Equal("RallyWeekendCalendar.bIsCustomPreset", settings.Cmds[44].Path);

        // the other events' settings stop before the calendar
        var single = RepLayouts.ForWireName("RaceEventRaceSettingsSingleEvent");
        Assert.NotNull(single);
        Assert.Equal(41, single.Cmds.Count);
        Assert.Equal("WeatherOptions.StartingTime", single.Cmds[24].Path);
    }
}
