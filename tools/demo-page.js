// Builds the README screenshots without a live lobby: bakes an anonymised state into
// a standalone copy of the web page (fictional pseudonyms, RFC 5737 documentation IP;
// stage and car names are the game's own, as the host resolves them from its content
// catalog). The page renders it from #statecache, exactly as a "Save page as" copy
// would. One copy per view:
//
//   node tools/demo-page.js src/Web/index.html demo/live.html live
//   node tools/demo-page.js src/Web/index.html demo/stages.html stages
//   node tools/demo-page.js src/Web/index.html demo/sectors.html sectors
//   node tools/demo-page.js src/Web/index.html demo/standings.html standings
//   python -m http.server 8790 --directory demo
//
// "live" is SS4 while it is being run (live sector board); the other three are the
// same session once SS4 is over, on the Stages, Sectors and Standings tabs. Then,
// with EDGE=".../msedge.exe" and COMMON="--headless=new --hide-scrollbars
// --force-device-scale-factor=2 --default-background-color=0e1116":
//
//   $EDGE $COMMON --window-size=1180,985 --screenshot=docs/screenshot.png \
//         http://localhost:8790/live.html
//   $EDGE $COMMON --window-size=1180,985 --screenshot=docs/stages.png \
//         http://localhost:8790/stages.html
//   (same for sectors.png and standings.png)
//   $EDGE $COMMON --window-size=330,545 --screenshot=docs/overlay-classification.png \
//         "http://localhost:8790/live.html?view=classification&bg=88"
//   $EDGE $COMMON --window-size=900,96 --screenshot=docs/overlay-progress.png \
//         "http://localhost:8790/live.html?view=progress&bg=88"
//
// The overlays are transparent in the app; they are shot on the page background so
// the PNGs stay readable on both GitHub themes. Edge caches by URL — add a dummy
// query param when re-shooting after an edit. Window height must leave some slack
// under the content or the progression markers render unpositioned, and the last
// row sits under the footer. An Edge already open takes the launch over and writes
// no screenshot: add --user-data-dir=<empty dir> to COMMON. The page polls /state;
// the static server answers 404, which the page ignores.
const fs = require("fs");

const SRC = process.argv[2];
const OUT = process.argv[3];
const MODE = process.argv[4] || "live";
if (!["live", "stages", "sectors", "standings"].includes(MODE))
  throw new Error("mode must be live, stages, sectors or standings");
const LIVE = MODE === "live";

const PCT = 0.2;

// Each stage's share of the stage time per sector (the last sector ends at the
// finish): the number of entries is the stage's number of sectors.
const STAGES = [
  { id: "run1", name: "SS1 Sisteron - Mézien", routeId: "MonteCarloS2SisteronCut1Forward", lengthKm: 7.2,
    group: 1, groupName: "Alpine Rally", profile: [0.34, 0.31, 0.35] },
  { id: "run2", name: "SS2 Forêt de Saverne", routeId: "AlsaceS4SaverneFullForward", lengthKm: 9.1,
    group: 1, groupName: "Alpine Rally", profile: [0.24, 0.27, 0.22, 0.27] },
  { id: "run3", name: "SS3 Cwmbiga - Afon Biga", routeId: "WalesS3HafrenNorthFullForward", lengthKm: 11.3,
    group: 2, groupName: "Forest Rally", profile: [0.26, 0.25, 0.27, 0.22] },
  { id: "run4", name: "SS4 Loutraki - Aghii Theodori", routeId: "GreeceS4LoutrakiFullForward", lengthKm: 10.2,
    group: 2, groupName: "Forest Rally", profile: [0.176, 0.178, 0.181, 0.182, 0.283] },
];
const CURRENT = STAGES.length - 1;

const CARS = {
  SkodaFabiaRSRally2: "Škoda Fabia RS Rally2",
  HyundaiI20NRally2: "Hyundai i20 Rally2",
  VWPoloGTIR5: "Volkswagen Polo GTI R5",
  Peugeot208Rally4: "Peugeot 208 Rally4",
};

// Stage times in seconds (penalty-free); "dnf" = started, never finished. The last
// stage holds the time once it is over; while it is run, only LIVE_SPLITS exist.
const D = [
  ["Vosgien",      "France",        "SkodaFabiaRSRally2", [312.418, 288.902, 331.774, 305.166]],
  ["nordkapp",     "Norway",        "HyundaiI20NRally2",  [314.902, 290.331, 329.408, 307.844]],
  ["MistralDrift", "France",        "VWPoloGTIR5",        [317.226, 292.115, 334.902, 309.221]],
  ["K_Vainio",     "Finland",       "HyundaiI20NRally2",  [311.884, 291.007, 336.418, "dnf"]],
  ["ArdennesRB",   "Belgium",       "SkodaFabiaRSRally2", [321.774, 296.882, 338.115, 313.008]],
  ["pinewood",     "UnitedKingdom", "VWPoloGTIR5",        [324.331, 299.446, 341.226, 316.774]],
  ["Tramontana",   "Spain",         "Peugeot208Rally4",   [338.115, 312.774, 358.331, 336.902]],
  ["alpaka",       "Germany",       "HyundaiI20NRally2",  [327.008, 301.226, 344.902, 318.446]],
  ["Sudtirol_92",  "Italy",         "SkodaFabiaRSRally2", [333.446, 308.902, 351.115, 327.115]],
  ["RookieLine",   "Poland",        "Peugeot208Rally4",   [402.882, 371.008, null,    391.226]],
];

// time penalties (s), added on top of the stage time as the game does
const PENALTIES = { pinewood: { run4: 10 } };

// SS4 while it is run: cumulative splits of the cars still out (K_Vainio retires
// after his third split); the others are already through.
const LIVE_SPLITS = {
  K_Vainio: [55.441, 112.830, 174.992],
  Tramontana: [53.880, 109.554, 166.228, 222.901],
  alpaka: [52.774, 107.991, 163.445, 219.117],
  Sudtirol_92: [54.115, 110.440, 168.774],
  RookieLine: [58.902, 120.226],
};

// Deterministic per-driver variation of the stage profile, so sectors differ.
function wobble(key) {
  let h = 2166136261;
  for (let i = 0; i < key.length; i++) h = Math.imul(h ^ key.charCodeAt(i), 16777619) >>> 0;
  return ((h % 2001) / 1000 - 1) * 0.03;   // within ±3 %
}
const ms = t => Math.round(t * 1000) / 1000;

// Cumulative splits ending at `total`, following `prefix` where the car was seen.
function chain(driver, stage, total, prefix = []) {
  const shares = stage.profile.map((share, k) => share * (1 + wobble(driver + stage.id + k)));
  const splits = prefix.slice();
  const rest = shares.slice(prefix.length);
  const restSum = rest.reduce((a, b) => a + b, 0);
  let at = prefix.length ? prefix[prefix.length - 1] : 0;
  rest.forEach((share, k) => {
    at = k === rest.length - 1 ? total : at + (total - (prefix.length ? prefix[prefix.length - 1] : 0)) * share / restSum;
    splits.push(ms(at));
  });
  return splits;
}
// each sector's own time, as the host publishes it next to the splits
const sectorsOf = splits => splits.map((t, i) => ms(t - (i > 0 ? splits[i - 1] : 0)));

// Only the ungated per-cell data, exactly what the host publishes: gating, penalty
// substitution, totals, ranking and ordering are all the page's job. Emitting a
// pre-computed board here would mean reimplementing those rules in this fixture and
// keeping them in sync by hand — which is how the screenshots silently lost their
// totals once the page grew a field the fixture did not know about.
// Mirrors RowView.NationFlag: the host resolves the nationality token to its flag
// code through the content catalog; the page only renders the code it receives.
const FLAGS = { France: "fr", Norway: "no", Finland: "fi", Belgium: "be", UnitedKingdom: "gb",
                Spain: "es", Germany: "de", Italy: "it", Poland: "pl" };

const rows = D.map(([driver, nation, car, times]) => {
  const rawCells = STAGES.map((stage, index) => {
    const t = times[index];
    const live = LIVE_SPLITS[driver];
    if (index === CURRENT && (t === "dnf" || (LIVE && live))) {
      if (!live) return null;
      // on the stage, or retired from it: the splits so far, no finish
      return { t: live[live.length - 1], f: false, s: live.length, splits: live, sectors: sectorsOf(live),
               r: t === "dnf" };
    }
    if (typeof t !== "number") return null;
    const splits = chain(driver, stage, t, index === CURRENT && live ? live : []);
    const penalty = (PENALTIES[driver] || {})[stage.id] || 0;
    return { t: ms(t + penalty), f: true, s: splits.length, splits, sectors: sectorsOf(splits) };
  });
  return {
    driver, nation, nationFlag: FLAGS[nation], nationName: nation,
    // "dnf" on the RUNNING stage means the car sits in Retire/Disqualify right now —
    // the only signal that lets the page mark the current column. On a past stage the
    // abandon is inferred from the closed column instead, so no flag would be needed.
    retired: times[CURRENT] === "dnf",
    rawCells,
    // Mirrors RowView.Cars / CarNames: one car per stage, aligned to rawCells.
    cars: rawCells.map(cell => cell ? car : null),
    carNames: rawCells.map(cell => cell ? CARS[car] : null),
  };
});

// progression on SS4: while it is run, cars still out on the stage + those through
const END = STAGES[CURRENT].lengthKm * 1000;
const WINDOW_KM = 10.2;
const out = LIVE
  ? { alpaka: 8810, Sudtirol_92: 8090, Tramontana: 7480, K_Vainio: 4680, RookieLine: 3530 }
  : { K_Vainio: 4680 };
const progress = D.map(([driver], index) => {
  const finished = !(driver in out);
  return { name: driver, named: true, dist: finished ? END : out[driver], finished,
           out: driver === "K_Vainio", pos: index + 1 };
});

const view = {
  allStages: STAGES.map(({ profile, ...s }) => ({ ...s, discarded: false })),
  rows,
  pct: PCT,
  server: "203.0.113.42:9600",
  state: "Connected",
  phase: LIVE ? "Racing" : "Results",
  currentStage: STAGES[CURRENT].name,
  currentStageName: STAGES[CURRENT].name.replace(/^SS\d+\s+/, ""),
  currentStageLengthKm: STAGES[CURRENT].lengthKm,
  title: "ACR Rally Championship — Round 3",
  description: "Live timing for the Sunday evening lobby. Open to spectators.",
  stageStart: "17:40",
  stageWeather: "Light rain · 11.5°C",
  progress,
  progressWindowKm: WINDOW_KM,
  hasRaceState: true,
  hideSplits: false,
  // no version: the footer then shows none, instead of one the screenshots would outlive
};

const json = JSON.stringify(view).replaceAll("<", "\\u003c");
let html = fs.readFileSync(SRC, "utf8");
const tag = '<script type="application/json" id="statecache">';
const i = html.indexOf(tag);
if (i < 0) throw new Error("statecache tag not found");
html = html.slice(0, i + tag.length) + json + html.slice(i + tag.length);

// Progression markers slide in from 0 via a CSS transition; a headless screenshot
// races it and catches them mid-flight. Kill transitions so first paint is final.
html = html.replace("</head>", "<style>*{transition:none !important}</style>\n</head>");
// Open the requested tab once the page has rendered the baked state.
const TAB = { sectors: "openSectors(null);",
              standings: 'activeTab = "standings"; render(displayedView(lastServer)); syncPanel(lastServer);' };
if (TAB[MODE]) html = html.replace("</body>", "<script>" + TAB[MODE] + "</script>\n</body>");
fs.writeFileSync(OUT, html, "utf8");
console.log("wrote", OUT, html.length, "bytes,", rows.length, "rows,", MODE);
