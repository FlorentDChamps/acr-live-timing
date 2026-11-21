namespace ACRLiveTiming.Decode;

/// <summary>
/// Weather options a stage is run with, as the race settings replicate them
/// (FWeatherOptions). A field the settings never sent keeps the game's default:
/// the values below are the ones a lobby leaves unsent while its calendar holds
/// them (clear, medium probability, standard persistency, realistic time, optimal
/// grip). <see cref="StartTime"/> and <see cref="Seed"/> have no known default:
/// null until received.
/// </summary>
public sealed record StageWeatherOptions
{
    public static readonly string[] ProbabilityValues = ["Low", "Medium", "High", "Maximum"];
    public static readonly string[] PersistencyValues = ["Zero", "Decreased", "Standard", "Increased"];
    public static readonly string[] TimeSpeedValues =
        ["WT_SPEEDFIX", "WT_SPEEDREALISTIC", "WT_SPEEDACCELERATED", "WT_SPEEDFAST", "WT_SPEEDVERYFAST", "WT_SPEEDUNREALISTIC"];
    public static readonly string[] TimeSpeedNames = ["Fixed", "Realistic", "Accelerated", "Fast", "Very fast", "Unrealistic"];
    public static readonly string[] GripValues = ["Dirty", "Slow", "Green", "Fast", "Optimal"];

    public int Type { get; init; }                 // EWeatherType at the start
    public bool RandomType { get; init; }
    public int TargetType { get; init; }           // the weather it can turn to
    public bool RandomTargetType { get; init; }
    public int TargetProbability { get; init; } = 1;
    public int Persistency { get; init; } = 2;
    public bool Dynamic { get; init; }
    public float? StartTime { get; init; }         // in-game seconds of the day
    public int TimeSpeed { get; init; } = 1;
    public int? Seed { get; init; }
    public float Wetness { get; init; }            // 0..1
    public float Snow { get; init; }               // accumulated snow, 0..1
    public int Grip { get; init; } = 4;

    /// <summary>"HH:mm", or null when the start time was not received.</summary>
    public string? StartClock => StartTime is float t && t >= 0f && t < 86400f
        ? TimeSpan.FromSeconds(t).ToString(@"hh\:mm") : null;

    public static string Name(string[] values, int index)
        => index >= 0 && index < values.Length ? values[index] : index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Read the options under <paramref name="prefix"/> ("WeatherOptions.")
    /// of a decoded state; null when none of them was received.</summary>
    public static StageWeatherOptions? From(RepObject o, string prefix)
    {
        if (!o.Values.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal))) return null;
        var d = new StageWeatherOptions();
        return new StageWeatherOptions
        {
            Type = o.Get<int>(prefix + "Preset.WeatherType") ?? d.Type,
            RandomType = o.Get<bool>(prefix + "Preset.bRandom") ?? d.RandomType,
            TargetType = o.Get<int>(prefix + "TargetPreset.WeatherType") ?? d.TargetType,
            RandomTargetType = o.Get<bool>(prefix + "TargetPreset.bRandom") ?? d.RandomTargetType,
            TargetProbability = o.Get<int>(prefix + "TargetWeatherProbability") ?? d.TargetProbability,
            Persistency = o.Get<int>(prefix + "WeatherPersistency") ?? d.Persistency,
            Dynamic = o.Get<bool>(prefix + "bDynamicWeather") ?? d.Dynamic,
            StartTime = o.Get<float>(prefix + "StartingTime"),
            TimeSpeed = o.Get<int>(prefix + "TimeSpeed") ?? d.TimeSpeed,
            Seed = o.Get<int>(prefix + "Seed"),
            Wetness = o.Get<float>(prefix + "StartingWetness") ?? d.Wetness,
            Snow = o.Get<float>(prefix + "StartingAccumulatedSnow") ?? d.Snow,
            Grip = o.Get<int>(prefix + "StartingGripLevel") ?? d.Grip,
        };
    }
}

/// <summary>One stage of a rally weekend's calendar. <c>PreRaceZone</c> is what
/// precedes it: <c>ServiceParkDefault</c> (a service park) or <c>NoZone</c>.</summary>
public sealed record CalendarStage(string RouteId, int Day, string PreRaceZone, StageWeatherOptions Weather);

/// <summary>A rally weekend's calendar (FRallyWeekendCalendar): its stages in
/// running order.</summary>
public sealed record RallyCalendar(string PresetId, string CustomName, bool Custom, IReadOnlyList<CalendarStage> Stages)
{
    public bool SameAs(RallyCalendar? other)
        => other != null && other.PresetId == PresetId && other.CustomName == CustomName
           && other.Custom == Custom && other.Stages.SequenceEqual(Stages);

    // by value, stages included: the same calendar received again is equal
    public bool Equals(RallyCalendar? other) => SameAs(other);
    public override int GetHashCode() => HashCode.Combine(PresetId, CustomName, Custom, Stages.Count);
}

/// <summary>One slot of the stage weather forecast (FWeatherEvolutionForecastData):
/// <c>Time</c> in in-game seconds of the day, <c>Span</c> the horizon in seconds,
/// the local conditions (0..1, temperature in °C) and the closest weather type.</summary>
public sealed record ForecastSlot(float Time, float Span, float CloudCover, float Rain, float Snow,
    float Fog, float Wind, float Lightning, float Temperature, int Type);
