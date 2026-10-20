import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { once } from "node:events";
import { readFileSync } from "node:fs";
import { createHash } from "node:crypto";
import { resolve } from "node:path";
import { startBaselineApi } from "../../scripts/maf/start-baseline-api.mjs";
import { origin, until } from "./client.mjs";

// 性能对照使用显式冻结的同机 JSON 报告，不依赖发行前的私有二进制包。
const reference = process.env.CREATIVEFLOW_MAF_REFERENCE_REPORT;
assert.ok(reference, "Set CREATIVEFLOW_MAF_REFERENCE_REPORT to a frozen same-machine 180-run report.");
const referencePath = resolve(reference);
const referenceBytes = readFileSync(referencePath);
const referenceDigest = createHash("sha256").update(referenceBytes).digest("hex");
const referenceReport = JSON.parse(referenceBytes);
assert.equal(referenceReport.schemaVersion, 1);
assert.equal(referenceReport.measuredRuns, 180);
assert.equal(referenceReport.records.length, 180);
const candidate = process.env.CREATIVEFLOW_MAF_API_BINARY ?? resolve("src/CreativeFlow.Api/bin/Release/net10.0/CreativeFlow.Api.dll");
// 固定次数的独立复验保留各自报告，不能覆盖失败样本或循环挑选一次通过。
const attempt = process.env.CREATIVEFLOW_MAF_PERFORMANCE_ATTEMPT ?? "";
assert.match(attempt, /^(?:-[a-z0-9-]+)?$/);
const candidateLabel = `maf-only-current${attempt}`;
// 每档启动新宿主，保留原预热、负载和阈值；此时不并行构建、浏览器或其他验收。
await assert.rejects(fetch(`${origin}/health/live`, { signal: AbortSignal.timeout(1000) }));
const variants = [[candidateLabel, false]];
assert.notEqual(referencePath, resolve(`artifacts/maf/performance/${candidateLabel}.json`), "Never overwrite the frozen reference report.");
if (process.env.CREATIVEFLOW_MAF_INCLUDE_SOAK === "1") variants.push([`maf-only-soak${attempt}`, true]);
for (const [label, soak] of variants) {
  process.env.CREATIVEFLOW_MAF_API_BINARY = candidate;
  const api = startBaselineApi();
  try {
    await until(async () => {
      try { return await (await fetch(`${origin}/health/ready`)).json(); } catch { return null; }
    }, value => value?.status === "healthy");
    console.log(JSON.stringify({ phase: label, pid: api.pid }));
    const benchmark = spawn(process.execPath, ["tests/maf-e2e/benchmark.mjs"], {
      env: { ...process.env, CREATIVEFLOW_MAF_API_PID: String(api.pid), CREATIVEFLOW_MAF_BENCHMARK_LABEL: label, CREATIVEFLOW_MAF_SOAK: soak ? "1" : "0" }, stdio: "inherit",
    });
    const [code] = await once(benchmark, "exit");
    assert.equal(code, 0, `${label} must complete every synthetic task`);
    if (label === candidateLabel) {
      assert.equal(createHash("sha256").update(readFileSync(referencePath)).digest("hex"), referenceDigest, "Frozen reference report changed during the candidate run.");
      const comparison = spawn(process.execPath, ["tests/maf-e2e/compare-performance.mjs", referencePath, `artifacts/maf/performance/${candidateLabel}.json`, `artifacts/maf/performance/maf-only-comparison${attempt}.json`], { stdio: "inherit" });
      const [comparisonCode] = await once(comparison, "exit");
      assert.equal(comparisonCode, 0, "Stop before soak when the candidate fails the frozen microbenchmark gates");
    }
  } finally {
    if (api.exitCode === null && api.signalCode === null) {
      const stopped = once(api, "exit"); api.kill("SIGTERM"); await stopped;
    }
  }
}
