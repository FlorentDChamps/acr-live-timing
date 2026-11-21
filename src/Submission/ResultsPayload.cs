using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using ACRLiveTiming.Content;
using ACRLiveTiming.Decode;

namespace ACRLiveTiming.Submission;

/// <summary>
/// Writes an <c>acrlt-results/1.0</c> document from a snapshot: for one
/// destination (its <c>eventId</c> and <c>startedAfterPairing</c>), or as a file
/// export without destination fields. The bytes are built once and sent or saved
/// as they are.
/// </summary>
public static class ResultsPayload
{
    public const string SchemaVersion = "acrlt-results/1.0";

    private static readonly JsonWriterOptions Options = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static byte[] ForDestination(ResultsSnapshot snapshot, Association destination, DateTime generatedAtUtc)
        => Write(snapshot, destination, generatedAtUtc);

    public static byte[] ForFile(ResultsSnapshot snapshot, DateTime generatedAtUtc)
        => Write(snapshot, null, generatedAtUtc);

    private static byte[] Write(ResultsSnapshot snapshot, Association? destination, DateTime generatedAtUtc)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, Options))
        {
            w.WriteStartObject();
            w.WriteString("schemaVersion", SchemaVersion);
            if (destination != null) w.WriteString("eventId", destination.EventId);
            w.WriteString("runId", snapshot.RunId);
            w.WriteString("generatedAt", Iso(generatedAtUtc));
            w.WriteString("submissionMode", destination != null ? "operator-confirmed" : "file-export");
            w.WriteString("appVersion", AppInfo.Version);
            w.WriteNumber("group", snapshot.Group);
            if (snapshot.GroupName != null) w.WriteString("groupName", snapshot.GroupName);

            w.WriteStartArray("stages");
            for (int i = 0; i < snapshot.Stages.Count; i++)
            {
                var stage = snapshot.Stages[i];
                w.WriteStartObject();
                w.WriteNumber("order", i + 1);
                if (stage.RouteId != null) w.WriteString("routeId", stage.RouteId); else w.WriteNull("routeId");
                w.WriteString("name", stage.Name);
                if (stage.LengthKm is double km) w.WriteNumber("lengthKm", Math.Round(km, 3));
                if (destination != null) w.WriteBoolean("startedAfterPairing", destination.StartedAfterPairing(stage));
                if (stage.StartedAtUtc is DateTime started) w.WriteString("startedAt", Iso(started));
                if (stage.InGameStartTime != null) w.WriteString("inGameStartTime", stage.InGameStartTime);
                if (stage.Weather != null) w.WriteString("weather", stage.Weather);
                if (stage.CalendarIndex is int index) w.WriteNumber("calendarOrder", index + 1);
                if (stage.Conditions != null) WriteConditions(w, stage.Conditions);
                if (stage.Forecast is { Count: > 0 } forecast) WriteForecast(w, forecast);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            if (snapshot.Calendar != null) WriteCalendar(w, snapshot.Calendar);

            w.WriteStartArray("participants");
            foreach (var driver in snapshot.Drivers)
            {
                w.WriteStartObject();
                WriteNullable(w, "steamId64", driver.SteamId64);
                WriteNullable(w, "eosPuid", driver.EosPuid);
                w.WriteString("usernameAtRun", driver.UsernameAtRun);
                WriteNullable(w, "nation", driver.Nation);
                WriteNullable(w, "nationName", driver.NationName);
                w.WriteStartArray("stages");
                for (int i = 0; i < driver.Cells.Count; i++)
                    WriteCell(w, i + 1, driver.Cells[i]);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void WriteCell(Utf8JsonWriter w, int order, SnapshotCell cell)
    {
        w.WriteStartObject();
        w.WriteNumber("order", order);
        switch (cell.Status)
        {
            case CellStatus.Finished:
                w.WriteString("status", "finished");
                w.WriteNumber("timeMs", cell.TimeMs ?? 0);
                w.WriteNumber("penaltyMs", cell.PenaltyMs ?? 0);
                WriteSectors(w, cell.SectorsMs);
                WriteNullable(w, "carId", cell.CarId);
                WriteNullable(w, "carName", cell.CarName);
                break;
            case CellStatus.Dnf:
                w.WriteString("status", "dnf");
                w.WriteNull("timeMs");
                WriteSectors(w, cell.SectorsMs);
                WriteNullable(w, "carId", cell.CarId);
                WriteNullable(w, "carName", cell.CarName);
                break;
            default:
                w.WriteString("status", "not_started");
                w.WriteNull("timeMs");
                break;
        }
        w.WriteEndObject();
    }

    // The weather options a stage is run with, under the game's own names.
    private static void WriteConditions(Utf8JsonWriter w, StageWeatherOptions o)
    {
        w.WriteStartObject("conditions");
        if (o.StartClock is string start) w.WriteString("startTime", start);
        w.WriteString("weatherType", Type(o.Type));
        w.WriteBoolean("randomWeatherType", o.RandomType);
        w.WriteString("targetWeatherType", Type(o.TargetType));
        w.WriteBoolean("randomTargetWeatherType", o.RandomTargetType);
        w.WriteString("targetWeatherProbability", StageWeatherOptions.Name(StageWeatherOptions.ProbabilityValues, o.TargetProbability));
        w.WriteString("weatherPersistency", StageWeatherOptions.Name(StageWeatherOptions.PersistencyValues, o.Persistency));
        w.WriteBoolean("dynamicWeather", o.Dynamic);
        w.WriteString("timeSpeed", StageWeatherOptions.Name(StageWeatherOptions.TimeSpeedValues, o.TimeSpeed));
        w.WriteNumber("startingWetness", Math.Round(o.Wetness, 3));
        w.WriteNumber("startingAccumulatedSnow", Math.Round(o.Snow, 3));
        w.WriteString("startingGripLevel", StageWeatherOptions.Name(StageWeatherOptions.GripValues, o.Grip));
        if (o.Seed is int seed) w.WriteNumber("seed", seed);
        w.WriteEndObject();
    }

    private static void WriteForecast(Utf8JsonWriter w, IReadOnlyList<ForecastSlot> slots)
    {
        w.WriteStartArray("forecast");
        foreach (var slot in slots)
        {
            w.WriteStartObject();
            w.WriteString("time", TimeSpan.FromSeconds(Math.Clamp(slot.Time, 0f, 86399f)).ToString(@"hh\:mm", CultureInfo.InvariantCulture));
            w.WriteString("weatherType", Type(slot.Type));
            w.WriteNumber("temperature", Math.Round(slot.Temperature, 1));
            w.WriteNumber("cloudCover", Math.Round(slot.CloudCover, 3));
            w.WriteNumber("rain", Math.Round(slot.Rain, 3));
            w.WriteNumber("snow", Math.Round(slot.Snow, 3));
            w.WriteNumber("fog", Math.Round(slot.Fog, 3));
            w.WriteNumber("wind", Math.Round(slot.Wind, 3));
            w.WriteNumber("lightning", Math.Round(slot.Lightning, 3));
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteCalendar(Utf8JsonWriter w, RallyCalendar calendar)
    {
        w.WriteStartObject("calendar");
        w.WriteString("presetId", calendar.PresetId);
        if (calendar.Custom) w.WriteBoolean("customPreset", true);
        if (calendar.CustomName.Length > 0) w.WriteString("customPresetName", calendar.CustomName);
        w.WriteStartArray("stages");
        for (int i = 0; i < calendar.Stages.Count; i++)
        {
            var stage = calendar.Stages[i];
            w.WriteStartObject();
            w.WriteNumber("order", i + 1);
            w.WriteString("routeId", stage.RouteId);
            w.WriteString("name", ContentCatalog.StageName(stage.RouteId));
            if (ContentCatalog.StageLengthKm(stage.RouteId) is double km) w.WriteNumber("lengthKm", Math.Round(km, 3));
            w.WriteNumber("day", stage.Day);
            if (stage.PreRaceZone.Length > 0) w.WriteString("preRaceZone", stage.PreRaceZone);
            WriteConditions(w, stage.Weather);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static string Type(int type)
        => WeatherForecast.TypeValues.ElementAtOrDefault(type) ?? type.ToString(CultureInfo.InvariantCulture);

    private static void WriteSectors(Utf8JsonWriter w, List<long> sectors)
    {
        w.WriteStartArray("sectorsMs");
        foreach (long ms in sectors) w.WriteNumberValue(ms);
        w.WriteEndArray();
    }

    private static void WriteNullable(Utf8JsonWriter w, string name, string? value)
    {
        if (value == null) w.WriteNull(name); else w.WriteString(name, value);
    }

    private static string Iso(DateTime utc)
        => Association.AsUtc(utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
