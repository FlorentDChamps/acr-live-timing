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
        }

        sealed class Stage
        {
            public string Id { get; set; } = "";
            // the route name as the game replicates it (DT_TracksVariants row), which can
            // differ in spelling from the id ("Weles…" on the wire for Wales routes)
            [JsonPropertyName("wireId")]
            public string WireId { get; set; } = "";
            public string Name { get; set; } = "";
            public bool Known { get; set; }
        }

        sealed class Tables
        {
            public Dictionary<string, string> Stages = new(StringComparer.Ordinal);   // id or wireId -> name
            public Dictionary<string, string> Cars = new(StringComparer.Ordinal);
            public HashSet<string> Routes = new(StringComparer.Ordinal);              // every known id + wireId
            public Dictionary<string, string> Levels = new(StringComparer.Ordinal);   // wire level -> id level
            public Dictionary<string, string> RouteIds = new(StringComparer.Ordinal); // wireId -> id
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

        /// <summary>True for a CarId the catalog lists (DT_Cars row name).</summary>
        public static bool IsKnownCar(string id) => Data.Value.Cars.ContainsKey(id);

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
                    if (string.IsNullOrEmpty(stage.WireId) || stage.WireId == stage.Id) continue;
                    tables.Stages[stage.WireId] = stage.Name;
                    tables.Routes.Add(stage.WireId);
                    tables.RouteIds[stage.WireId] = stage.Id;
                    // the route's level under both spellings: "WelesS3HafrenNorth" is the
                    // level "WalesS3HafrenNorth" (the persistent level keeps the id spelling)
                    var wireLevel = Decode.Names.BaseOf(stage.WireId);
                    var idLevel = Decode.Names.BaseOf(stage.Id);
                    if (wireLevel != idLevel) tables.Levels[wireLevel] = idLevel;
                }
                foreach (var car in catalog.Cars)
                    if (!string.IsNullOrWhiteSpace(car.DisplayName)) tables.Cars[car.Id] = car.DisplayName;
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
