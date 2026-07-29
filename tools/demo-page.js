// Builds the README screenshots without a live lobby: bakes an anonymised state into
// a standalone copy of the web page (fictional pseudonyms, RFC 5737 documentation IP).
// The page renders it from #statecache, exactly as a "Save page as" copy would.
//
//   node tools/demo-page.js src/Web/index.html demo.html
//   node -e "require('http').createServer((q,s)=>{s.end(require('fs').readFileSync('demo.html'))}).listen(8790)"
//
// then, with EDGE=".../msedge.exe" and COMMON="--headless=new --hide-scrollbars
// --force-device-scale-factor=2 --default-background-color=0e1116":
//
//   $EDGE $COMMON --window-size=1180,925 --screenshot=docs/screenshot.png \
//         http://localhost:8790/
//   $EDGE $COMMON --window-size=330,545 --screenshot=docs/overlay-classification.png \
//         "http://localhost:8790/?view=classification&bg=88"
//   $EDGE $COMMON --window-size=900,96 --screenshot=docs/overlay-progress.png \
//         "http://localhost:8790/?view=progress&bg=88"
//
// The overlays are transparent in the app; they are shot on the page background so
// the PNGs stay readable on both GitHub themes. Edge caches by URL — add a dummy
// query param when re-shooting after an edit. Window height must leave some slack
// under the content or the progression markers render unpositioned.
const fs = require("fs");

const SRC = process.argv[2];
const OUT = process.argv[3];

const PCT = 0.2;

const STAGES = [
  { id: "run1", name: "SS1 MonteCarloS2Sisteron", group: 1, groupName: "Alpine Rally" },
  { id: "run2", name: "SS2 AlsaceS4Saverne", group: 1, groupName: "Alpine Rally" },
  { id: "run3", name: "SS3 WalesS3HafrenNorth", group: 2, groupName: "Forest Rally" },
  { id: "run4", name: "SS4 GreeceS4Loutraki", group: 2, groupName: "Forest Rally" },
];

// times in seconds; null = no time yet (still on stage), "dnf" = started, never finished
const D = [
  ["Vosgien",      "France",        "SkodaFabiaRSRally2",   [312.418, 288.902, 331.774, 305.166]],
  ["nordkapp",     "Norway",        "FordFiestaRally2",     [314.902, 290.331, 329.408, 307.844]],
  ["MistralDrift", "France",        "CitroenC3Rally2",      [317.226, 292.115, 334.902, 309.221]],
  ["K_Vainio",     "Finland",       "HyundaiI20NRally2",    [311.884, 291.007, 336.418, "dnf"]],
  ["ArdennesRB",   "Belgium",       "SkodaFabiaRSRally2",   [321.774, 296.882, 338.115, 313.008]],
  ["pinewood",     "UnitedKingdom", "VolkswagenPoloGTIR5",  [324.331, 299.446, 341.226, 316.774]],
  ["Tramontana",   "Spain",         "Peugeot208Rally4",     [338.115, 312.774, 358.331, null]],
  ["alpaka",       "Germany",       "FordFiestaRally2",     [327.008, 301.226, 344.902, null]],
  ["Sudtirol_92",  "Italy",         "CitroenC3Rally2",      [333.446, 308.902, 351.115, null]],
  ["RookieLine",   "Poland",        "RenaultClioRally5",    [402.882, 371.008, null,    null]],
];

// Only the ungated per-cell data, exactly what the host publishes: gating, penalty
// substitution, totals, ranking and ordering are all the page's job. Emitting a
// pre-computed board here would mean reimplementing those rules in this fixture and
// keeping them in sync by hand — which is how the screenshots silently lost their
// totals once the page grew a field the fixture did not know about.
const rows = D.map(([driver, nation, car, times]) => ({
  driver, nation,
  // "dnf" on the RUNNING stage means the car sits in Retire/Disqualify right now —
  // the only signal that lets the page mark the current column. On a past stage the
  // abandon is inferred from the closed column instead, so no flag would be needed.
  retired: times[times.length - 1] === "dnf",
  rawCells: times.map(t =>
    typeof t === "number" ? { t, f: true, s: 6 } : (t === "dnf" ? { t: 0, f: false, s: 3 } : null)),
  // Mirrors RowView.Cars: one car token per stage, aligned to rawCells.
  cars: times.map(t => t === null ? null : car),
}));

// live progression on SS4: cars still out on the stage + those already through
const WINDOW_KM = 9.4;
const progress = [
  { name: "Vosgien",      named: true, dist: 9400, finished: true,  out: false, pos: 1 },
  { name: "nordkapp",     named: true, dist: 9400, finished: true,  out: false, pos: 2 },
  { name: "MistralDrift", named: true, dist: 9400, finished: true,  out: false, pos: 3 },
  { name: "alpaka",       named: true, dist: 8120, finished: false, out: false, pos: 7 },
  { name: "Sudtirol_92",  named: true, dist: 7460, finished: false, out: false, pos: 9 },
  { name: "Tramontana",   named: true, dist: 6890, finished: false, out: false, pos: 6 },
  { name: "K_Vainio",     named: true, dist: 4310, finished: false, out: true,  pos: 4 },
  { name: "RookieLine",   named: true, dist: 3255, finished: false, out: false, pos: 10 },
];

const view = {
  allStages: STAGES.map(s => ({ ...s, discarded: false })),
  rows,
  pct: PCT,
  server: "203.0.113.42:9600",
  state: "Connected",
  phase: "Racing",
  currentStage: STAGES[3].name,
  title: "ACR Rally Championship — Round 3",
  description: "Live timing for the Sunday evening lobby. Open to spectators.",
  stageStart: "17:40",
  stageWeather: "Light rain · 11.5°C",
  progress,
  progressWindowKm: WINDOW_KM,
  hasRaceState: true,
  finishGating: true,
  version: "1.0.0",
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
fs.writeFileSync(OUT, html, "utf8");
console.log("wrote", OUT, html.length, "bytes,", rows.length, "rows");
