using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ACRLiveTiming.Content
{
    /// <summary>
    /// Read-only labels extracted from ACR's data tables. Wire identifiers remain the
    /// source of truth; this catalog only supplies a human-readable companion label.
    /// </summary>
    public static class ContentCatalog
    {
        sealed class Catalog
        {
            public List<Stage> Stages { get; set; } = new();
            public List<Car> Cars { get; set; } = new();
            public List<Country> Countries { get; set; } = new();
        }

        sealed class Country
        {
            public string Id { get; set; } = "";
            public string Name { get; set; } = "";
            public string? Iso { get; set; }                       // flag code; null: no flag (Other)
            public List<string> Aliases { get; set; } = new();     // other spellings a client replicates
        }

        sealed class Stage
        {
            public string Id { get; set; } = "";
            // the route name as the game replicates it (DT_TracksVariants row), which can
            // differ in spelling from the id ("Weles…" on the wire for Wales routes)
            [JsonPropertyName("wireId")]
            public string WireId { get; set; } = "";
            public string Name { get; set; } = "";
            [JsonPropertyName("lengthKm")]
            public double? LengthKm { get; set; }
            public bool Known { get; set; }
        }

        sealed class Tables
        {
            public Dictionary<string, string> Stages = new(StringComparer.Ordinal);   // id or wireId -> name
            public Dictionary<string, string> Cars = new(StringComparer.Ordinal);
            public HashSet<string> Routes = new(StringComparer.Ordinal);              // every known id + wireId
            public Dictionary<string, string> Levels = new(StringComparer.Ordinal);   // wire level -> id level
            public Dictionary<string, string> RouteIds = new(StringComparer.Ordinal); // wireId -> id
            public Dictionary<string, double> Lengths = new(StringComparer.Ordinal);  // id or wireId -> km
            // nationality token (any listed spelling, any casing) -> flag code / display name
            public Dictionary<string, string> CountryFlags = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> CountryNames = new(StringComparer.OrdinalIgnoreCase);
        }

        sealed class Car
        {
            public string Id { get; set; } = "";
            [JsonPropertyName("displayName")]
            public string DisplayName { get; set; } = "";
        }

        static readonly Regex StageLabel = new(@"^(?<prefix>SS\d+\s+)?(?<route>.+)$", RegexOptions.Compiled);
        static readonly Lazy<Tables> Data = new(Load);

        /// <summary>Display name of a route, by catalog id or wire spelling; the input
        /// unchanged when unknown.</summary>
        public static string StageName(string id)
            => Data.Value.Stages.GetValueOrDefault(id, id);

        public static string CarName(string id)
            => Data.Value.Cars.GetValueOrDefault(id, id);

        /// <summary>Flag code (ISO 3166-1 alpha-2, or a gb-* subdivision) of a nationality
        /// token as a client replicates it. The token is an FName registered on the
        /// player's own client, so its casing is theirs ("SwitzerlAnd", "FinlAnd") and an
        /// old profile keeps a pre-rename spelling: every spelling the catalog lists is
        /// matched without case. Null when unknown or flagless.</summary>
        public static string? CountryFlag(string token)
            => Data.Value.CountryFlags.TryGetValue(token, out var iso) ? iso : null;

        /// <summary>Display name of a nationality token; the token unchanged when unknown.</summary>
        public static string CountryName(string token)
            => Data.Value.CountryNames.GetValueOrDefault(token, token);

        /// <summary>Route length in km (catalog id or wire spelling), null when unknown
        /// or when only the level is known.</summary>
        public static double? StageLengthKm(string routeId)
            => Data.Value.Lengths.TryGetValue(routeId, out var km) ? km : null;

        /// <summary>True for a route the catalog lists (catalog id or wire spelling).</summary>
        public static bool IsKnownRoute(string s) => Data.Value.Routes.Contains(s);

        /// <summary>The catalog (data-table) id of a route given its wire spelling
        /// ("WelesS3HafrenNorthCut1Reverse" → "WalesS3HafrenNorthCut1Reverse"); the
        /// input unchanged when it already is the id or is unknown.</summary>
        public static string CanonicalRouteId(string route)
            => Data.Value.RouteIds.GetValueOrDefault(route, route);

        /// <summary>Maps a level name as the game spells it in a route ("WelesS3HafrenNorth")
        /// to the level's own name ("WalesS3HafrenNorth"); identity when no alias.</summary>
        public static string CanonicalLevel(string level)
            => Data.Value.Levels.GetValueOrDefault(level, level);

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

        static Tables Load()
        {
            var tables = new Tables();
            try
            {
                var assembly = typeof(ContentCatalog).Assembly;
                var resource = assembly.GetManifestResourceNames()
                    .FirstOrDefault(name => name.EndsWith("acr-content.json", StringComparison.Ordinal));
                if (resource == null) return tables;
                using var stream = assembly.GetManifestResourceStream(resource);
                var catalog = stream == null ? null : JsonSerializer.Deserialize<Catalog>(stream,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (catalog == null) return tables;
                foreach (var stage in catalog.Stages)
                {
                    if (!stage.Known || string.IsNullOrWhiteSpace(stage.Name)) continue;
                    tables.Stages[stage.Id] = stage.Name;
                    tables.Routes.Add(stage.Id);
                    if (stage.LengthKm is double km && km > 0) tables.Lengths[stage.Id] = km;
                    if (string.IsNullOrEmpty(stage.WireId) || stage.WireId == stage.Id) continue;
                    tables.Stages[stage.WireId] = stage.Name;
                    tables.Routes.Add(stage.WireId);
                    if (stage.LengthKm is double wireKm && wireKm > 0) tables.Lengths[stage.WireId] = wireKm;
                    tables.RouteIds[stage.WireId] = stage.Id;
                    // the route's level under both spellings: "WelesS3HafrenNorth" is the
                    // level "WalesS3HafrenNorth" (the persistent level keeps the id spelling)
                    var wireLevel = Decode.Names.BaseOf(stage.WireId);
                    var idLevel = Decode.Names.BaseOf(stage.Id);
                    if (wireLevel != idLevel) tables.Levels[wireLevel] = idLevel;
                }
                foreach (var car in catalog.Cars)
                    if (!string.IsNullOrWhiteSpace(car.DisplayName)) tables.Cars[car.Id] = car.DisplayName;
                foreach (var country in catalog.Countries)
                {
                    if (string.IsNullOrWhiteSpace(country.Id)) continue;
                    var spellings = new List<string> { country.Id };
                    spellings.AddRange(country.Aliases);
                    if (!string.IsNullOrWhiteSpace(country.Name))
                    {
                        spellings.Add(country.Name);
                        spellings.Add(country.Name.Replace(" ", ""));
                    }
                    foreach (var spelling in spellings)
                    {
                        if (string.IsNullOrWhiteSpace(spelling)) continue;
                        tables.CountryNames.TryAdd(spelling, string.IsNullOrWhiteSpace(country.Name) ? country.Id : country.Name);
                        if (!string.IsNullOrWhiteSpace(country.Iso)) tables.CountryFlags.TryAdd(spelling, country.Iso);
                    }
                }
            }
            catch (JsonException)
            {
                // A bad catalog must never interrupt packet decoding; raw ids remain usable.
                return new Tables();
            }
            return tables;
        }
    }
}
