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
  pct: .5,
  dnfNoRejoin: true,
  hideSplits: true
};

const result = computeView(view, cfg, false);
const byName = new Map(result.rows.map(value => [value.driver, value]));
assert.deepStrictEqual(result.rows.map(value => value.driver), [
  "Fast finisher", "Slow finisher", "Alpha tied DNF", "Echo tied DNF",
  "Later DNF", "One-stage DNF", "Missed-stage DNF", "Zero-stage DNF", "Never started"
]);
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
assert.strictEqual(byName.get("Never started").status, "dns");
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

console.log("classification tests passed");
