// Lightweight regression checks for the browser-side classification rules.
// Run with: node tools/test-classification.js
const assert = require("assert");
const fs = require("fs");
const path = require("path");

const page = fs.readFileSync(path.join(__dirname, "..", "src", "Web", "index.html"), "utf8");
const start = page.indexOf("function computeView(");
const end = page.indexOf("// the view actually rendered", start);
assert(start >= 0 && end > start, "computeView source not found");
const computeView = Function(page.slice(start, end) + "\nreturn computeView;")();

const stages = [1, 2, 3, 4].map(n => ({ id: `run${n}`, name: `SS${n}` }));
const finished = t => ({ t, f: true, s: 4 });
const retired = progress => ({ t: progress, f: false, s: 2, r: true });
const row = (driver, values) => ({
  driver,
  retired: false,
  rawCells: values.map(value => value == null ? null
    : value.kind === "dnf" ? retired(value.progress) : finished(value)),
  cars: values.map(value => value == null ? null : "TestCar")
});
const dnf = progress => ({ kind: "dnf", progress });

const view = {
  allStages: stages,
  hasRaceState: true,
  rows: [
    row("Fast finisher", [90, 100, 110, 120]),
    row("Slow finisher", [100, 110, 120, 130]),
    row("Alpha tied DNF", [80, 90, dnf(999), 70]),
    row("Echo tied DNF", [80, 90, dnf(333), null]),
    row("Later DNF", [85, 90, dnf(222), 60]),
    row("One-stage DNF", [75, dnf(111), 50, 50]),
    row("Missed-stage DNF", [80, null, 70, 60]),
    row("Zero-stage DNF", [dnf(null), null, null, null]),
    row("Never started", [null, null, null, null])
  ]
};
const cfg = {
  discarded: new Set(),
  groups: new Map(),
  penalty: 300,
  dnfNoRejoin: true,
  hideSplits: true
};

const result = computeView(view, cfg, false);
const byName = new Map(result.rows.map(value => [value.driver, value]));
assert.deepStrictEqual(result.rows.map(value => value.driver), [
  "Fast finisher", "Slow finisher", "Alpha tied DNF", "Echo tied DNF",
  "Later DNF", "One-stage DNF", "Missed-stage DNF", "Zero-stage DNF"
]);
// a driver who started none of the selected stages is not listed
assert.strictEqual(byName.has("Never started"), false);
assert.strictEqual(byName.get("Fast finisher").status, "finisher");
assert.strictEqual(byName.get("Alpha tied DNF").status, "dnf");
assert.strictEqual(byName.get("Alpha tied DNF").completedBeforeDnf, 2);
assert.strictEqual(byName.get("Alpha tied DNF").timeBeforeDnf, 170);
assert.strictEqual(byName.get("Alpha tied DNF").total, 170);
assert.strictEqual(byName.get("Alpha tied DNF").cells[3].real, 70);
assert.strictEqual(byName.get("Alpha tied DNF").cells[3].counted, false);
assert.strictEqual(byName.get("Alpha tied DNF").rank, byName.get("Echo tied DNF").rank);
assert.strictEqual(byName.get("Later DNF").rank, 5);
assert.strictEqual(byName.get("Missed-stage DNF").cells[1].dnf, true);
assert.strictEqual(byName.get("Missed-stage DNF").completedBeforeDnf, 1);
assert.strictEqual(byName.get("Zero-stage DNF").status, "dnf");
assert.strictEqual(byName.get("Alpha tied DNF").pointEligible, true);

const withoutDnfStage = computeView(view, {
  ...cfg, discarded: new Set(["run3"])
}, false);
const reclassified = withoutDnfStage.rows.find(value => value.driver === "Alpha tied DNF");
assert.strictEqual(reclassified.status, "finisher");
assert.strictEqual(reclassified.total, 240);

const duringStage = computeView({ ...view, phase: "Racing", rows: [
  row("Current runner", [90, 100, 110, null]),
  row("Current finisher", [95, 105, 115, 120])
] }, cfg, false);
assert.strictEqual(duringStage.rows.find(value => value.driver === "Current runner").total, 300);

const legacy = computeView(view, { ...cfg, dnfNoRejoin: false }, false);
assert.strictEqual(legacy.rows.find(value => value.driver === "Alpha tied DNF").status, "classified");
assert(legacy.rows.find(value => value.driver === "Alpha tied DNF").total > 170);

const atResults = computeView({ ...view, phase: "Results", rows: [
  row("Present finisher", [90, 100, 110, 120]),
  row("Missed final stage", [95, 105, 115, null])
] }, cfg, false);
assert.strictEqual(atResults.rows.find(value => value.driver === "Missed final stage").status, "dnf");


// A stage without a time counts the slowest finish plus the DNF penalty (s), as
// the game does in a rally weekend; a weekend penalty on a finish counts in the
// total, never in the stage time.
const weekend = computeView({ allStages: stages.slice(0, 2), hasRaceState: true, rows: [
  { driver: "Quick", rawCells: [{ t: 100, f: true, s: 4, p: 30 }, { t: 200, f: true, s: 4 }], cars: [] },
  { driver: "Slow", rawCells: [{ t: 150, f: true, s: 4 }, { t: 260, f: true, s: 4 }], cars: [] },
  { driver: "Out", rawCells: [{ t: 120, f: false, s: 2, r: true, p: 300 }, null], cars: [] }
] }, { discarded: new Set(), groups: new Map(), penalty: 300, dnfNoRejoin: false, hideSplits: true }, false);
const weekendBy = new Map(weekend.rows.map(value => [value.driver, value]));
assert.strictEqual(weekendBy.get("Quick").total, 330);
assert.strictEqual(weekendBy.get("Quick").cells[0].effective, 100);
assert.strictEqual(weekendBy.get("Slow").total, 410);
assert.strictEqual(weekendBy.get("Out").total, 450 + 560);
assert.deepStrictEqual(weekend.rows.map(value => value.driver), ["Quick", "Slow", "Out"]);

// The slowest finish is taken without its own penalty, as the game does.
const penalised = computeView({ allStages: stages.slice(0, 1), hasRaceState: true, rows: [
  { driver: "Fast", rawCells: [{ t: 100, f: true, s: 4 }], cars: [] },
  { driver: "Penalised", rawCells: [{ t: 157, q: 27, f: true, s: 4 }], cars: [] },
  { driver: "Out", rawCells: [{ t: 60, f: false, s: 1, r: true }], cars: [] }
] }, { discarded: new Set(), groups: new Map(), penalty: 300, dnfNoRejoin: false, hideSplits: true }, false);
assert.strictEqual(penalised.rows.find(value => value.driver === "Out").total, 130 + 300);

// A finish counts its own time, penalties included, even past the DNF time.
const slowFinish = computeView({ allStages: stages.slice(0, 1), hasRaceState: true, rows: [
  { driver: "Only finisher", rawCells: [{ t: 140, q: 40, f: true, s: 4 }], cars: [] },
  { driver: "Out", rawCells: [{ t: 60, f: false, s: 1, r: true }], cars: [] }
] }, { discarded: new Set(), groups: new Map(), penalty: 30, dnfNoRejoin: false, hideSplits: true }, false);
const onlyFinisher = slowFinish.rows.find(value => value.driver === "Only finisher");
assert.strictEqual(onlyFinisher.total, 140);
assert.strictEqual(onlyFinisher.cells[0].substituted, false);
assert.strictEqual(slowFinish.rows.find(value => value.driver === "Out").total, 130);

// In a rally weekend the game's classification decides what each stage counts
// (c), whatever the DNF penalty setting says.
const game = computeView({ allStages: stages.slice(0, 1), hasRaceState: true, rows: [
  { driver: "Fast", rawCells: [{ t: 100, f: true, s: 4, p: 20, c: 120 }], cars: [] },
  { driver: "Out", rawCells: [{ t: 60, f: false, s: 1, r: true, p: 300, c: 415 }], cars: [] },
  { driver: "Missed", rawCells: [{ r: true, p: 300, c: 400 }], cars: [] }
] }, { discarded: new Set(), groups: new Map(), penalty: 30, dnfNoRejoin: false, hideSplits: true }, false);
const gameBy = new Map(game.rows.map(value => [value.driver, value]));
assert.strictEqual(gameBy.get("Fast").total, 120);
assert.strictEqual(gameBy.get("Fast").cells[0].effective, 100);
assert.strictEqual(gameBy.get("Out").total, 415);
assert.strictEqual(gameBy.get("Missed").total, 400);

console.log("classification tests passed");
