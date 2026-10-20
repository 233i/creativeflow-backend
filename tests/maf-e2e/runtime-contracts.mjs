import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { once } from "node:events";
import { mkdirSync, writeFileSync } from "node:fs";
import { startBaselineApi } from "../../scripts/maf/start-baseline-api.mjs";
import { origin, until } from "./client.mjs";

// 单引擎只验收一次真实组合，不把相同测试重复计成四套引擎验证。
await assert.rejects(fetch(`${origin}/health/live`, { signal: AbortSignal.timeout(1000) }));
const results = [];
{
  // 故意保留过时开关，证明它们不能让新二进制走另一套运行器或绑定写入。
  process.env.RuntimeMigration__Agent = "Legacy";
  process.env.RuntimeMigration__Workflow = "Legacy";
  const api = startBaselineApi();
  const started = Date.now();
  try {
    await until(async () => {
      try { return await (await fetch(`${origin}/health/ready`)).json(); } catch { return null; }
    }, value => value?.status === "healthy");
    const tests = spawn(process.execPath, ["--test", "tests/maf-e2e/contracts.test.mjs"], { env: process.env, stdio: "inherit" });
    const [code] = await once(tests, "exit");
    assert.equal(code, 0, "MAF-only HTTP and SignalR contracts");
    results.push({ engine: "maf-only", retiredFlagsIgnored: true, passed: true, elapsedMs: Date.now() - started });
    console.log(JSON.stringify(results.at(-1)));
  } finally {
    if (api.exitCode === null && api.signalCode === null) {
      const stopped = once(api, "exit"); api.kill("SIGTERM"); await stopped;
    }
  }
}
mkdirSync("artifacts/maf", { recursive: true });
writeFileSync("artifacts/maf/runtime-contracts.json", JSON.stringify({ generatedAt: new Date().toISOString(), results }, null, 2));
