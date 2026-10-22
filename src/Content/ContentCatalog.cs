using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ACRLiveTiming.Content;

/// <summary>
/// Read-only display labels for ACR content, from the embedded community catalog
/// (acr-content.json, schema 3). Wire identifiers remain the source of truth; this
/// catalog only supplies a human-readable companion label, a route's length and
/// track, and a nationality's flag.
/// <para>A catalog id is the identifier as the game spells it online or in its save
/// files, reduced (see <see cref="Reduce"/>); other spellings are listed as aliases.
/// Nationalities are the exception: their id is their flag code ("ch", "gb-wls", or
/// "other" for none) and every spelling the game uses is an alias. A value read from
/// the game is found by reducing it and looking for an item whose id or aliases hold
/// it — so casing variants ("HyundaiI20NRally2", "Hyundaii20NRally2", "SwitzerlAnd")
/// need nothing more.</para>
/// </summary>
public static partial class ContentCatalog
{
    private class Item
    {
        public string Id { get; set; } = "";
        // the name the game shows; the catalog's translated labels are not used
        public string Name { get; set; } = "";
        public List<string>? Aliases { get; set; }

        public string? Display => string.IsNullOrWhiteSpace(Name) ? null : Name;
    }

    private sealed class Stage : Item
    {
        public string Track { get; set; } = "";
        public double? LengthKm { get; set; }
    }

    private sealed class Country : Item
    {
        // false only for a nationality without a flag ("other"); the id is the flag code otherwise
        public bool? Flag { get; set; }
    }

    private sealed class Catalog
    {
        public List<Item> Tracks { get; set; } = [];
        public List<Stage> Stages { get; set; } = [];
        public List<Item> Cars { get; set; } = [];
        public List<Item> Weather { get; set; } = [];
        public List<Country> Countries { get; set; } = [];
    }

    private sealed class Tables
    {
        // reduced id or alias -> item, one table per list
        public Dictionary<string, Item> Tracks = new(StringComparer.Ordinal);
        public Dictionary<string, Stage> Stages = new(StringComparer.Ordinal);
        public Dictionary<string, Item> Cars = new(StringComparer.Ordinal);
        public Dictionary<string, Item> Weather = new(StringComparer.Ordinal);
        public Dictionary<string, Country> Countries = new(StringComparer.Ordinal);
    }

    [GeneratedRegex(@"^(?<prefix>SS\d+\s+)?(?<route>.+)$")]
    private static partial Regex StageLabel { get; }
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Lazy<Tables> Data = new(Load);

    /// <summary>A value reduced the catalog's way: decomposed (NFD), accents dropped,
    /// lower case, only [a-z0-9] kept. "WelesS3HafrenNorthCut1Forward" →
    /// "weless3hafrennorthcut1forward", "Group2/4" → "group24", "WT_CLEAR" → "wtclear".</summary>
    public static string Reduce(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            char lower = char.ToLowerInvariant(c);
            if (lower is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(lower);
        }
        return sb.ToString();
    }

    private static T? Find<T>(Dictionary<string, T> table, string? value) where T : class
        => string.IsNullOrEmpty(value) ? null : table.GetValueOrDefault(Reduce(value));

    /// <summary>Display name of a route as the game sends it; the input unchanged
    /// when unknown.</summary>
    public static string StageName(string route) => Find(Data.Value.Stages, route)?.Display ?? route;

    /// <summary>Display name of a CarId; the input unchanged when unknown.</summary>
    public static string CarName(string id) => Find(Data.Value.Cars, id)?.Display ?? id;

    /// <summary>Flag code (ISO 3166-1 alpha-2, or a gb-* subdivision) of a nationality
    /// token as a client replicates it. The token is an FName registered on the
    /// player's own client, so its casing is theirs ("SwitzerlAnd", "FinlAnd") and an
    /// old profile keeps a pre-rename spelling: the catalog lists every spelling as an
    /// alias. Null when unknown, or when the catalog says it has no flag ("other").</summary>
    public static string? CountryFlag(string token)
        => Find(Data.Value.Countries, token) is { Flag: not false } country ? country.Id : null;

    /// <summary>Display name of a nationality token; the token unchanged when unknown.</summary>
    public static string CountryName(string token) => Find(Data.Value.Countries, token)?.Display ?? token;

    /// <summary>Display name of a weather type, by its EWeatherType value
    /// ("WT_LIGHT_CLOUDS"); null when unknown.</summary>
    public static string? WeatherName(string enumValue) => Find(Data.Value.Weather, enumValue)?.Display;

    /// <summary>Route length in km, null when unknown or when only the level is known.</summary>
    public static double? StageLengthKm(string route)
        => Find(Data.Value.Stages, route)?.LengthKm is double km && km > 0 ? km : null;

    /// <summary>True for a route the catalog lists, in any of its spellings.</summary>
    public static bool IsKnownRoute(string s) => Find(Data.Value.Stages, s) != null;

    /// <summary>The track a level belongs to, by its name in any spelling the catalog
    /// lists ("WelesS3HafrenNorth" and "WalesS3HafrenNorth" are both the track
    /// "waless3hafrennorth"); the reduced name when the catalog does not know it.</summary>
    public static string TrackOfLevel(string level)
        => Find(Data.Value.Tracks, level)?.Id ?? Reduce(level);

    /// <summary>The track a route drives, from the catalog; null when the route is unknown.</summary>
    public static string? TrackOfRoute(string route) => Find(Data.Value.Stages, route)?.Track;

    /// <summary>Converts an internal column label such as "SS2 WalesS3…" while
    /// preserving the run prefix used by the timing board.</summary>
    public static string StageLabelName(string label)
    {
        var match = StageLabel.Match(label);
        if (!match.Success) return label;
        return match.Groups["prefix"].Value + StageName(match.Groups["route"].Value);
    }

    public static string RouteIdFromLabel(string label)
    {
        var match = StageLabel.Match(label);
        return match.Success ? match.Groups["route"].Value : label;
    }

    private static Tables Load()
    {
        var tables = new Tables();
        try
        {
            var assembly = typeof(ContentCatalog).Assembly;
            string? resource = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith("acr-content.json", StringComparison.Ordinal));
            if (resource == null) return tables;
            using var stream = assembly.GetManifestResourceStream(resource);
            var catalog = stream == null ? null : JsonSerializer.Deserialize<Catalog>(stream, JsonOptions);
            if (catalog == null) return tables;
            Index(tables.Tracks, catalog.Tracks);
            Index(tables.Stages, catalog.Stages);
            Index(tables.Cars, catalog.Cars);
            Index(tables.Weather, catalog.Weather);
            Index(tables.Countries, catalog.Countries);
        }
        catch (JsonException)
        {
            // A bad catalog must never interrupt packet decoding; raw ids remain usable.
            return new Tables();
        }
        return tables;
    }

    // Within a list a reduced name designates one item only, so the first one wins
    // should a broken catalog repeat it.
    private static void Index<T>(Dictionary<string, T> table, List<T> items) where T : Item
    {
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Id)) continue;
            table.TryAdd(Reduce(item.Id), item);
            foreach (string alias in item.Aliases ?? [])
                if (!string.IsNullOrWhiteSpace(alias)) table.TryAdd(Reduce(alias), item);
        }
    }
}
