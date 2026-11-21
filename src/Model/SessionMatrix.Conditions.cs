using System.Text.Json.Serialization;
using ACRLiveTiming.Content;
using ACRLiveTiming.Decode;

namespace ACRLiveTiming.Model;

/// <summary>Weather options of a stage, for the page.</summary>
public sealed class WeatherView
{
    public string Type { get; set; } = "";          // EWeatherType at the start, as the game names it
    public string Name { get; set; } = "";          // its display name
    public bool Random { get; set; }                // drawn at random by the game
    public string TargetType { get; set; } = "";    // the weather it can turn to
    public string TargetName { get; set; } = "";
    public bool RandomTarget { get; set; }
    public string Probability { get; set; } = "";   // of the change: Low, Medium, High, Maximum
    public string Persistency { get; set; } = "";   // Zero, Decreased, Standard, Increased
    public bool Dynamic { get; set; }
    public string TimeSpeed { get; set; } = "";     // display name
    public double Wetness { get; set; }             // 0..1
    public double Snow { get; set; }                // accumulated snow, 0..1
    public string Grip { get; set; } = "";          // Dirty, Slow, Green, Fast, Optimal
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Start { get; set; }              // in-game start time, HH:mm

    public static WeatherView From(StageWeatherOptions o) => new()
    {
        Type = WeatherForecast.TypeValues.ElementAtOrDefault(o.Type) ?? o.Type.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Name = TypeName(o.Type),
        Random = o.RandomType,
        TargetType = WeatherForecast.TypeValues.ElementAtOrDefault(o.TargetType) ?? o.TargetType.ToString(System.Globalization.CultureInfo.InvariantCulture),
        TargetName = TypeName(o.TargetType),
        RandomTarget = o.RandomTargetType,
        Probability = StageWeatherOptions.Name(StageWeatherOptions.ProbabilityValues, o.TargetProbability),
        Persistency = StageWeatherOptions.Name(StageWeatherOptions.PersistencyValues, o.Persistency),
        Dynamic = o.Dynamic,
        TimeSpeed = StageWeatherOptions.Name(StageWeatherOptions.TimeSpeedNames, o.TimeSpeed),
        Wetness = Math.Round(o.Wetness, 2),
        Snow = Math.Round(o.Snow, 2),
        Grip = StageWeatherOptions.Name(StageWeatherOptions.GripValues, o.Grip),
        Start = o.StartClock
    };

    internal static string TypeName(int type)
        => type >= 0 && type < WeatherForecast.TypeValues.Length ? WeatherForecast.TypeName(type) : type.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>One slot of a stage's weather forecast, for the page.</summary>
public sealed class ForecastView
{
    public string Time { get; set; } = "";   // in-game HH:mm
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public double Temp { get; set; }         // °C
    public double Cloud { get; set; }        // 0..1 for the five below
    public double Rain { get; set; }
    public double Snow { get; set; }
    public double Fog { get; set; }
    public double Wind { get; set; }

    public static List<ForecastView> From(IReadOnlyList<ForecastSlot>? slots)
        => slots == null ? [] : [.. slots.Select(s => new ForecastView
        {
            Time = TimeSpan.FromSeconds(Math.Clamp(s.Time, 0f, 86399f)).ToString(@"hh\:mm"),
            Type = WeatherForecast.TypeValues.ElementAtOrDefault(s.Type) ?? "",
            Name = WeatherView.TypeName(s.Type),
            Temp = Math.Round(s.Temperature, 1),
            Cloud = Math.Round(s.CloudCover, 2),
            Rain = Math.Round(s.Rain, 2),
            Snow = Math.Round(s.Snow, 2),
            Fog = Math.Round(s.Fog, 2),
            Wind = Math.Round(s.Wind, 2)
        })];
}

/// <summary>One stage of a rally weekend's calendar, with the column it was run
/// in (null: not run yet in this session).</summary>
public sealed class CalendarStageView
{
    public int Index { get; set; }
    public string RouteId { get; set; } = "";
    public string Name { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? LengthKm { get; set; }
    public int Day { get; set; }
    public bool ServicePark { get; set; }      // a service park comes before it
    public WeatherView Weather { get; set; } = new();
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ColumnId { get; set; }
    // the game's forecast, sent when the stage loads: known for the stages the app
    // saw loading, never for the ones to come
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ForecastView>? Forecast { get; set; }
}

/// <summary>The calendar of the rally weekend a rally group was run in.</summary>
public sealed class CalendarView
{
    public int Group { get; set; }
    public string PresetId { get; set; } = "";
    public List<CalendarStageView> Stages { get; set; } = [];
}

/// <summary>The stage that comes next: in a rally weekend, from the start of the
/// weekend on; otherwise once the previous stage is over.</summary>
public sealed class NextStageView
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Index { get; set; }            // in the weekend's calendar
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Count { get; set; }            // stages in the weekend's calendar
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RouteId { get; set; }
    public string Name { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? LengthKm { get; set; }
    public bool ServicePark { get; set; }
    // the game has loaded it: the stage start and weather of the page header are its own
    public bool Loaded { get; set; }
    public WeatherView Weather { get; set; } = new();
    public List<ForecastView> Forecast { get; set; } = [];
    // in a rally weekend, every stage of it: run (with its column) or to come
    public List<CalendarStageView> Rally { get; set; } = [];
}

public sealed partial class SessionMatrix
{
    // Race settings of the stage being run or loaded, the weekend's calendar and
    // the stage it is at, the route the game travels to, and the latest forecast
    // table. Between stages they describe the next one.
    private StageWeatherOptions? _stageSettings;
    private RallyCalendar? _calendar;
    private int _weekendStage = -1;
    // the stage the settings describe has started: they are no longer the next one
    private bool _settingsRan;
    private string? _travelRoute;
    private List<ForecastSlot>? _forecast;
    // id -> calendar of the weekend the stage was run in
    private readonly Dictionary<string, RallyCalendar> _columnCalendars = [];

    // a forecast's first slot is its horizon after the stage start
    private const float ForecastStartTolerance = 60f;

    /// <summary>Race settings from the decoder: the weather options of the stage
    /// being run (or, between stages, the next one), the rally weekend's calendar
    /// and the stage it is at (-1: unknown), and the route being travelled to.</summary>
    public void SetRaceSettings(StageWeatherOptions? settings, RallyCalendar? calendar, int weekendStage, string? travelRoute)
    {
        lock (_lock)
        {
            if (Equals(settings, _stageSettings) && Equals(calendar, _calendar)
                && weekendStage == _weekendStage && travelRoute == _travelRoute) return;
            // settings received during a run are that run's (the app started mid-stage);
            // the forecast table follows a stage's settings, so the one held is the
            // stage before's, even when both start at the same time
            if (settings?.Seed != _stageSettings?.Seed || settings?.StartTime != _stageSettings?.StartTime)
            {
                _settingsRan = IsRunPhase(_lobbyPhase);
                _forecast = null;
            }
            _stageSettings = settings;
            _calendar = calendar;
            _weekendStage = weekendStage;
            _travelRoute = travelRoute;
            if (CurrentColumnStart() is ColumnStart current)
            {
                current.Conditions = settings;
                current.Forecast = ForecastFor(settings?.StartTime) ?? current.Forecast;
            }
        }
        RaiseChanged();
    }

    /// <summary>The latest stage forecast table (every slot).</summary>
    public void SetForecast(List<ForecastSlot> slots)
    {
        lock (_lock)
        {
            if (_forecast != null && _forecast.SequenceEqual(slots)) return;
            _forecast = slots;
            if (CurrentColumnStart() is ColumnStart current && ForecastFor(current.Conditions?.StartTime) is { } forecast)
                current.Forecast = forecast;
        }
        RaiseChanged();
    }

    // Caller holds _lock. The forecast table when it is the one of a stage starting
    // at startTime: its first slot comes one horizon after the start (ten minutes
    // at the realistic time speed, more when time runs faster).
    private List<ForecastSlot>? ForecastFor(float? startTime)
    {
        if (_forecast is not { Count: > 0 } forecast || startTime is not float start) return null;
        float offset = ((forecast[0].Time - forecast[0].Span - start) % 86400f + 86400f + 43200f) % 86400f - 43200f;
        return Math.Abs(offset) <= ForecastStartTolerance ? forecast : null;
    }

    // Caller holds _lock.
    private void ClearConditions(bool keepRaceSettings)
    {
        _columnCalendars.Clear();
        if (keepRaceSettings) return;
        _stageSettings = null;
        _calendar = null;
        _weekendStage = -1;
        _settingsRan = false;
        _travelRoute = null;
        _forecast = null;
    }

    // Caller holds _lock. Each rally group run in a weekend, with that weekend's
    // calendar and the column each of its stages was run in.
    private List<CalendarView> BuildCalendars()
    {
        var views = new List<CalendarView>();
        foreach (int group in _groups.Values.Distinct().Order())
        {
            var columns = _columnOrder.Where(id => _groups.GetValueOrDefault(id) == group).ToList();
            string? withCalendar = columns.LastOrDefault(_columnCalendars.ContainsKey);
            if (withCalendar == null) continue;
            var calendar = _columnCalendars[withCalendar];
            var view = new CalendarView { Group = group, PresetId = calendar.PresetId };
            for (int i = 0; i < calendar.Stages.Count; i++)
            {
                var stage = calendar.Stages[i];
                view.Stages.Add(new CalendarStageView
                {
                    Index = i,
                    RouteId = stage.RouteId,
                    Name = ContentCatalog.StageName(stage.RouteId),
                    LengthKm = ContentCatalog.StageLengthKm(stage.RouteId),
                    Day = stage.Day,
                    ServicePark = IsServicePark(stage),
                    Weather = WeatherView.From(stage.Weather),
                    ColumnId = columns.LastOrDefault(id => _columnCalendars.GetValueOrDefault(id) == calendar
                                                           && _weekendOf.TryGetValue(id, out var w) && w.Stage == i)
                });
            }
            views.Add(view);
        }
        return views;
    }

    private static bool IsServicePark(CalendarStage stage)
        => stage.PreRaceZone.StartsWith("ServicePark", StringComparison.OrdinalIgnoreCase);

    private static bool IsRunPhase(string phase)
        => phase is "Start sequence" or "Semaphore" or "Racing" or "Finishing" or "Post race" or "Spectating";

    // Caller holds _lock. The next stage: in a rally weekend, the calendar's stage
    // after the one being run, or once it is over the one the game moves on to;
    // otherwise, between stages, the settings the game has moved on to.
    private NextStageView? BuildNextStage()
    {
        bool running = IsRunPhase(_lobbyPhase);
        string? last = _columnOrder.Count > 0 ? _columnOrder[^1] : null;
        if (_calendar is { Stages.Count: > 0 } calendar)
        {
            int lastStage = last != null && _weekendOf.TryGetValue(last, out var w)
                            && _columnCalendars.GetValueOrDefault(last) == calendar ? w.Stage : -1;
            // the stage run last: the settings' own once it has started, even
            // without a column (the app started during it)
            int ranStage = _settingsRan && _weekendStage >= 0 ? _weekendStage : lastStage;
            int after = ranStage >= 0 && ranStage + 1 < calendar.Stages.Count ? ranStage + 1 : -1;
            bool loaded = !running && !_settingsRan && _weekendStage >= 0
                          && (_weekendStage != lastStage || last == null);
            int index = loaded ? _weekendStage : after;
            if (index < 0) return null;
            var stage = calendar.Stages[index];
            return new NextStageView
            {
                Index = index,
                Count = calendar.Stages.Count,
                Loaded = loaded,
                RouteId = stage.RouteId,
                Name = ContentCatalog.StageName(stage.RouteId),
                LengthKm = ContentCatalog.StageLengthKm(stage.RouteId),
                ServicePark = IsServicePark(stage),
                Weather = WeatherView.From(stage.Weather),
                // the forecast comes with the stage's settings: until they are
                // loaded, the one kept is the previous stage's
                Forecast = loaded ? ForecastView.From(ForecastFor(stage.Weather.StartTime)) : [],
                Rally = RallyStages(calendar, index, last, loaded)
            };
        }
        if (running || _settingsRan || _stageSettings is not { } settings) return null;
        var ran = last != null && _columnStarts.TryGetValue(last, out var start) ? start.Conditions : null;
        if (ran != null && ran.StartTime == settings.StartTime && ran.Seed == settings.Seed) return null;
        string? lastRoute = last == null ? null : ContentCatalog.RouteIdFromLabel(_labels.GetValueOrDefault(last, last));
        string? route = _travelRoute != null && !string.Equals(_travelRoute, lastRoute, StringComparison.OrdinalIgnoreCase)
            ? _travelRoute : null;
        return new NextStageView
        {
            RouteId = route,
            Name = route == null ? "" : ContentCatalog.StageName(route),
            LengthKm = route == null ? null : ContentCatalog.StageLengthKm(route),
            Loaded = true,
            Weather = WeatherView.From(settings),
            Forecast = ForecastView.From(ForecastFor(settings.StartTime))
        };
    }

    // Caller holds _lock. Every stage of the weekend the next stage belongs to, with
    // the column each one already run was run in (the weekend started again: none)
    // and its forecast: the one its run started with, or the next stage's once the
    // game has loaded it.
    private List<CalendarStageView> RallyStages(RallyCalendar calendar, int next, string? last, bool loaded)
    {
        int group = last != null && _columnCalendars.GetValueOrDefault(last) == calendar
                    && _weekendOf.TryGetValue(last, out var w) && w.Stage < next
            ? _groups.GetValueOrDefault(last) : 0;
        var columns = group == 0 ? [] : _columnOrder.Where(id => _groups.GetValueOrDefault(id) == group).ToList();
        return [.. calendar.Stages.Select((stage, i) =>
        {
            string? column = i >= next ? null : columns.LastOrDefault(id => _columnCalendars.GetValueOrDefault(id) == calendar
                                                                           && _weekendOf.TryGetValue(id, out var cw) && cw.Stage == i);
            var forecast = column != null ? _columnStarts.GetValueOrDefault(column)?.Forecast
                : i == next && loaded ? ForecastFor(stage.Weather.StartTime) : null;
            return new CalendarStageView
            {
                Index = i,
                RouteId = stage.RouteId,
                Name = ContentCatalog.StageName(stage.RouteId),
                LengthKm = ContentCatalog.StageLengthKm(stage.RouteId),
                Day = stage.Day,
                ServicePark = IsServicePark(stage),
                Weather = WeatherView.From(stage.Weather),
                ColumnId = column,
                Forecast = forecast is { Count: > 0 } ? ForecastView.From(forecast) : null
            };
        })];
    }

    // Caller holds _lock. The conditions the stage of the last column was run in,
    // as the next stage shows them: its place in the weekend, the weather options
    // and the forecast it started with.
    private NextStageView? BuildCurrentConditions()
    {
        if (_columnOrder.Count == 0) return null;
        string last = _columnOrder[^1];
        if (_columnStarts.GetValueOrDefault(last) is not { Conditions: { } conditions } start) return null;
        string route = ContentCatalog.RouteIdFromLabel(_labels.GetValueOrDefault(last, last));
        var calendar = _columnCalendars.GetValueOrDefault(last);
        int stage = calendar != null && _weekendOf.TryGetValue(last, out var w) ? w.Stage : -1;
        return new NextStageView
        {
            Index = stage >= 0 ? stage : null,
            Count = stage >= 0 ? calendar!.Stages.Count : null,
            RouteId = route.Length > 0 ? route : null,
            Name = route.Length > 0 ? ContentCatalog.StageName(route) : "",
            LengthKm = route.Length > 0 ? ContentCatalog.StageLengthKm(route) : null,
            ServicePark = stage >= 0 && IsServicePark(calendar!.Stages[stage]),
            Weather = WeatherView.From(conditions),
            Forecast = ForecastView.From(start.Forecast)
        };
    }

    /// <summary>The name a new rally takes from the weekend it is run in: the
    /// host's name for a calendar of their own, else the preset's; numbered when
    /// another rally already has it.</summary>
    private string WeekendGroupName(int group)
    {
        if (_calendar == null) return $"Rally {group}";
        string name = _calendar.CustomName.Length > 0 ? _calendar.CustomName : ContentCatalog.WeekendName(_calendar.PresetId);
        if (name.Length == 0) return $"Rally {group}";
        string unique = name;
        for (int n = 2; _groupNames.Values.Contains(unique); n++) unique = $"{name} {n}";
        return unique;
    }

    // Caller holds _lock. The calendar a rally group was run in (null: none).
    private RallyCalendar? GroupCalendar(int group)
    {
        string? id = _columnOrder.LastOrDefault(c => _groups.GetValueOrDefault(c) == group && _columnCalendars.ContainsKey(c));
        return id == null ? null : _columnCalendars[id];
    }

    // Caller holds _lock. Where a stage sits in its weekend's calendar.
    private int? CalendarIndex(string id)
        => _columnCalendars.ContainsKey(id) && _weekendOf.TryGetValue(id, out var w) && w.Stage >= 0 ? w.Stage : null;
}
