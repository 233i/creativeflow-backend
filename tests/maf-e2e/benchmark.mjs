import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync, mkdirSync } from "node:fs";
import { account, admin, configureProviders, until, hub, fixtureCallCount, fixtureCallsSince, node, edge, workflow, origin } from "./client.mjs";

// 单引擎候选使用固定输入及统计口径，仅访问合成任务。
const label = process.env.CREATIVEFLOW_MAF_BENCHMARK_LABEL ?? "maf-only";
assert.match(label, /^[a-z0-9-]+$/);
const soak = process.env.CREATIVEFLOW_MAF_SOAK === "1";
const rounds = soak ? 18 : 3;
const warmups = soak ? 34 : 2;
const root = process.env.CREATIVEFLOW_MAF_TEST_ROOT;
assert.match(root ?? "", /^\/(private\/)?tmp\/creativeflow-maf-[\w.]+$/);
const pid = Number(process.env.CREATIVEFLOW_MAF_API_PID);
assert.ok(Number.isInteger(pid) && pid > 1);
assert.match(execFileSync("ps", ["-p", String(pid), "-o", "command="], { encoding: "utf8" }), /CreativeFlow\.Api\.dll/);
const cwd = execFileSync("lsof", ["-a", "-p", String(pid), "-d", "cwd", "-Fn"], { encoding: "utf8" });
assert.ok(cwd.includes(`${root.replace(/^\/tmp\//, "/private/tmp/")}/data`) || cwd.includes(`${root}/data`));
assert.equal((await (await fetch(`${origin}/api/v1/system/info`)).json()).environment, "MigrationBaseline");
const user = await account();
const providers = await configureProviders(await admin());
const connection = await hub(user);
const tools = new Map(), notifications = new Map(), errors = [];
connection.on("AgentToolCall", call => {
  const previous = tools.get(call.runId) ?? [];
  previous.push(call.toolUseId); tools.set(call.runId, previous);
  void connection.invoke("SubmitToolResult", call.runId, call.toolUseId, "{}", false).catch(error => errors.push(error.message));
});
connection.on("AgentEvent", event => {
  if (event.sequence <= 0) return;
  const previous = notifications.get(event.runId) ?? [];
  previous.push(Math.max(0, Date.now() - Date.parse(event.sentAt))); notifications.set(event.runId, previous);
});
const upload = new FormData();
upload.set("file", new Blob([readFileSync(`${root}/data/fixture.png`)], { type: "image/png" }), "fixture.png");
const asset = await user.request("/api/v1/canvas/assets", "POST", upload);
const graph = { nodes: Array.from({ length: 20 }, (_, index) => node(`n${index}`, index ? "imageOutput" : "image", index ? {} : { assetUrl: asset.url })),
  edges: Array.from({ length: 19 }, (_, index) => edge(`n${index}`, "image", `n${index + 1}`, "image")) };
const profileNames = ["agent-text", "agent-two-tools-three-model-rounds", "workflow-20-nodes"];
const records = [];
let peakRssKiB = 0;
const memorySamples = [];
const sampleMemory = () => {
  const value = Number(execFileSync("ps", ["-p", String(pid), "-o", "rss="], { encoding: "utf8" }).trim());
  assert.ok(Number.isFinite(value) && value > 0);
  peakRssKiB = Math.max(value, peakRssKiB);
  const atMs = Date.now();
  if (atMs - (memorySamples.at(-1)?.atMs ?? 0) >= 5000) memorySamples.push({ atMs, rssKiB: value });
};
sampleMemory();
const memory = setInterval(sampleMemory, 500);
const start = Date.now();
async function execute(profile, round, concurrency, index, warmup = false) {
  const marker = `maf-bench-${profile}-${round}-${concurrency}-${index}-${crypto.randomUUID()}`;
  const submitAt = performance.now();
  let result, id, acceptMs;
  // 工作流图不包含 Agent 的 prompt marker；比较这段并发批次的全部出站，
  // 避免按不存在的 marker 过滤后，把任何意外模型调用都误记为零。
  // 增量读取保留全部出站断言，但不让日益增长的历史 JSON 解析阻塞基准客户端事件循环。
  const callStart = await fixtureCallCount();
  if (profile === "workflow-20-nodes") {
    const definition = await workflow(user, graph);
    const submittedAt = performance.now();
    const run = await user.request(`/api/v1/workflows/${definition.id}/runs`, "POST", {}, 202);
    acceptMs = performance.now() - submittedAt; id = run.id;
    result = await until(() => user.request(`/api/v1/workflows/${definition.id}/runs/${id}`), value => ["succeeded", "failed"].includes(value.status));
    assert.equal(result.nodes.length, 20);
    assert.ok(result.nodes.every(item => item.status === "succeeded"));
  } else {
    const canvas = await user.request("/api/v1/canvas", "POST", { name: marker, graph: { nodes: [], edges: [] } }, 201);
    const session = await user.request("/api/v1/agent/sessions", "POST", { canvasId: canvas.id });
    const prompt = `${profile === "agent-text" ? "" : "[benchmark-multi]"} ${marker}`;
    const submittedAt = performance.now();
    const run = await user.request(`/api/v1/agent/sessions/${session.id}/runs`, "POST", { providerId: providers.chat.id, modelKey: "maf-chat", prompt });
    acceptMs = performance.now() - submittedAt; id = run.id;
    await connection.invoke("AttachAgentRun", id, canvas.id);
    result = await until(() => user.request(`/api/v1/agent/runs/${id}`), value => ["succeeded", "failed"].includes(value.status));
  }
  assert.equal(result.status, "succeeded", JSON.stringify(result));
  const captured = await fixtureCallsSince(callStart);
  const calls = (profile === "workflow-20-nodes" ? captured : captured.filter(call => JSON.stringify(call.body).includes(marker)))
    .filter(call => call.method === "POST");
  const expectedModels = profile === "workflow-20-nodes" ? 0 : profile === "agent-text" ? 1 : 3;
  const expectedTools = profile === "agent-two-tools-three-model-rounds" ? 4 : 0;
  assert.equal(calls.length, expectedModels);
  assert.equal((tools.get(id) ?? []).length, expectedTools);
  assert.equal(new Set(tools.get(id) ?? []).size, expectedTools);
  const startedAt = Date.parse(result.startedAt), createdAt = Date.parse(result.createdAt);
  const record = { id, firstModelAt: calls[0]?.atUtc ?? null, profile, round, concurrency, index, warmup, acceptMs,
    queueMs: startedAt - createdAt, runtimeMs: Date.parse(result.finishedAt) - startedAt,
    dispatchMs: (profile === "workflow-20-nodes" ? Date.parse(result.nodes[0].startedAt) : calls[0].atUtc) - startedAt,
    eventMs: notifications.get(id) ?? [], models: calls.length, tools: expectedTools,
    clientTotalMs: performance.now() - submitAt };
  if (!warmup) records.push(record);
}
const summarize = values => {
  const sorted = [...values].sort((a, b) => a - b);
  const percentile = q => sorted.length ? Math.round(sorted[Math.max(0, Math.ceil(sorted.length * q) - 1)] * 1000) / 1000 : null;
  return { count: sorted.length, p50: percentile(0.5), p95: percentile(0.95), p99: percentile(0.99), max: sorted.at(-1) ?? null };
};
try {
  // 微基线为 6 次预热 / 180 次实测；耐久模式为 102 次预热 / 1080 次实测，输入和原有限流不变。
  for (const profile of profileNames) {
    for (let index = 0; index < warmups; index++) await execute(profile, 0, 1, index, true);
    console.log(JSON.stringify({ warmup: profile, completed: warmups }));
  }
  for (let round = 1; round <= rounds; round++) {
    for (const concurrency of [1, 4]) for (const profile of profileNames) {
      for (let index = 0; index < concurrency * 4; index += concurrency) {
        await Promise.all(Array.from({ length: concurrency }, (_, offset) => execute(profile, round, concurrency, index + offset)));
      }
      console.log(JSON.stringify({ round, concurrency, profile, completed: records.length }));
    }
  }
  assert.deepEqual(errors, []);
  // 单引擎检查业务行存在且无新绑定，实际实现由程序集/DI 测试验证。
  for (const kind of ["agent", "workflow"]) {
    const selected = records.filter(item => (item.profile === "workflow-20-nodes") === (kind === "workflow")).map(item => item.id);
    selected.forEach(id => assert.match(id, /^[a-f0-9-]{36}$/));
    const selection = selected.map(id => `'${id}'`).join(",");
    const query = `SELECT count(*) FROM ${kind}_run run WHERE run_id IN (${selection}) AND NOT EXISTS (SELECT 1 FROM ${kind}_run_runtime binding WHERE binding.run_id=run.run_id)`;
    const count = Number(execFileSync("psql", ["-h", "127.0.0.1", "-p", "55439", "-U", "creativeflow_maf_test", "-d", "creativeflow_maf_e2e", "-At", "-c",
      query], { encoding: "utf8" }).trim());
    assert.equal(count, selected.length, `${kind} benchmark must exercise the selected runtime`);
  }
  // Agent 的 HTTP 契约没有 startedAt/finishedAt；一次性从本测试库读取原始计时，
  // 不能为方便基准扩展生产响应，也不能把 NaN 序列化成 null 后当作通过。
  const ids = records.filter(item => item.profile !== "workflow-20-nodes").map(item => item.id);
  for (const id of ids) assert.match(id, /^[a-f0-9-]{36}$/);
  const timings = JSON.parse(execFileSync("psql", ["-h", "127.0.0.1", "-p", "55439", "-U", "creativeflow_maf_test", "-d", "creativeflow_maf_e2e", "-At", "-c",
    `SELECT json_agg(json_build_object('id',run_id,'created',created_at,'started',started_at,'finished',finished_at)) FROM agent_run WHERE run_id IN (${ids.map(id => `'${id}'`).join(",")})`], { encoding: "utf8" }));
  for (const item of records) {
    if (item.profile !== "workflow-20-nodes") {
      const timing = timings.find(value => value.id === item.id);
      assert.ok(timing);
      item.queueMs = Date.parse(timing.started) - Date.parse(timing.created);
      item.runtimeMs = Date.parse(timing.finished) - Date.parse(timing.started);
      item.dispatchMs = item.firstModelAt - Date.parse(timing.started);
      assert.ok(item.eventMs.length >= 2, `${item.profile}: missing persisted Agent notifications`);
    }
    for (const key of ["acceptMs", "queueMs", "runtimeMs", "dispatchMs"]) assert.ok(Number.isFinite(item[key]) && item[key] >= 0, `${item.profile}: invalid ${key}`);
    assert.ok(item.eventMs.every(value => Number.isFinite(value) && value >= 0));
  }
  const summaries = [];
  for (const concurrency of [1, 4]) for (const profile of profileNames) {
    const matching = records.filter(item => item.profile === profile && item.concurrency === concurrency);
    summaries.push({ profile, concurrency, count: matching.length,
      ...Object.fromEntries(["acceptMs", "queueMs", "dispatchMs", "runtimeMs"].map(key => [key, summarize(matching.map(item => item[key]))])),
      eventMs: summarize(matching.flatMap(item => item.eventMs)) });
  }
  const report = { schemaVersion: 1, captureMode: "incremental-v2", generatedAt: new Date().toISOString(), elapsedMs: Date.now() - start, apiPid: pid,
    engines: { agent: "maf-only", workflow: "maf-only" },
    warmupRuns: warmups * profileNames.length, measuredRuns: records.length, rounds, peakRssKiB, memorySamples, summaries, records };
  mkdirSync("artifacts/maf/performance", { recursive: true });
  const reportPath = `artifacts/maf/performance/${label}.json`;
  writeFileSync(reportPath, JSON.stringify(report, null, 2));
  console.log(JSON.stringify({ measuredRuns: records.length, peakRssKiB, report: reportPath }));
} finally {
  clearInterval(memory);
  await connection.stop();
}
