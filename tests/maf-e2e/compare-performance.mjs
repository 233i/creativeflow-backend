import assert from "node:assert/strict";
import { readFileSync, writeFileSync } from "node:fs";

// 阈值来自迁移前冻结的基线，不能因为候选结果不达标而放宽。
const legacy = JSON.parse(readFileSync(process.argv[2] ?? "artifacts/maf/performance/legacy-current.json"));
const maf = JSON.parse(readFileSync(process.argv[3] ?? "artifacts/maf/performance/maf-current.json"));
assert.equal(legacy.schemaVersion, 1);
assert.equal(maf.schemaVersion, 1);
assert.equal(legacy.measuredRuns, 180);
assert.equal(maf.measuredRuns, 180);
assert.equal(legacy.warmupRuns, maf.warmupRuns);
const results = [];
for (const original of legacy.summaries) {
  const candidate = maf.summaries.find(item => item.profile === original.profile && item.concurrency === original.concurrency);
  assert.ok(candidate);
  assert.equal(original.count, candidate.count);
  for (const metric of ["acceptMs", "queueMs", "dispatchMs", "runtimeMs", "eventMs"]) {
    const baseline = original[metric].p95, actual = candidate[metric].p95;
    if (baseline === null) { assert.equal(actual, null); continue; }
    assert.ok(Number.isFinite(baseline) && Number.isFinite(actual));
    const limit = metric === "queueMs" ? baseline + 500 : Math.max(baseline * 1.25, baseline + (metric === "runtimeMs" ? 20 : 10));
    results.push({ profile: original.profile, concurrency: original.concurrency, metric, legacy: baseline, maf: actual, limit, passed: actual <= limit });
  }
}
const rssLimit = legacy.peakRssKiB * 1.25 + 32768;
results.push({ metric: "peakRssKiB", legacy: legacy.peakRssKiB, maf: maf.peakRssKiB, limit: rssLimit, passed: maf.peakRssKiB <= rssLimit });
for (const report of [legacy, maf]) {
  assert.equal(report.records.length, 180);
  for (const row of report.records) {
    assert.equal(row.models, row.profile === "workflow-20-nodes" ? 0 : row.profile === "agent-text" ? 1 : 3);
    assert.equal(row.tools, row.profile === "agent-two-tools-three-model-rounds" ? 4 : 0);
  }
}
const failures = results.filter(item => !item.passed);
writeFileSync(process.argv[4] ?? "artifacts/maf/performance/comparison.json", JSON.stringify({ generatedAt: new Date().toISOString(), passed: failures.length === 0, results }, null, 2));
console.log(JSON.stringify({ passed: failures.length === 0, checks: results.length, failures }));
assert.equal(failures.length, 0, "MAF must meet the frozen performance gates.");
