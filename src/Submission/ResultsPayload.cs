using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

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
                if (destination != null) w.WriteBoolean("startedAfterPairing", destination.StartedAfterPairing(stage.StartTimestamp));
                if (stage.StartedAtUtc is DateTime started) w.WriteString("startedAt", Iso(started));
                if (stage.InGameStartTime != null) w.WriteString("inGameStartTime", stage.InGameStartTime);
                if (stage.Weather != null) w.WriteString("weather", stage.Weather);
                w.WriteEndObject();
            }
            w.WriteEndArray();

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
        => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
