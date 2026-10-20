import test from "node:test";
import assert from "node:assert/strict";
import { once } from "node:events";
import { execFileSync } from "node:child_process";
import { mkdirSync, writeFileSync } from "node:fs";
import { startBaselineApi } from "../../scripts/maf/start-baseline-api.mjs";
import { account, admin, configureProviders, until, hub, fixtureCalls, node, edge, workflow, origin, delay, assertRuntime, runWorkflow } from "./client.mjs";

const queuedRecovery = process.env.CREATIVEFLOW_MAF_QUEUED_RECOVERY === "1";

function videoFor(runId) {
  assert.match(runId, /^[a-f0-9-]{36}$/);
  // 只读取本测试库的原视频检查点；业务 HTTP 本来就不暴露内部视频任务 ID。
  return execFileSync("psql", ["-h", "127.0.0.1", "-p", "55439", "-U", "creativeflow_maf_test", "-d", "creativeflow_maf_e2e", "-At", "-c",
    `SELECT job_id FROM video_job WHERE request->>'workflowRunId' = '${runId}' AND request->>'nodeId' = 'video' ORDER BY created_at DESC LIMIT 1`], { encoding: "utf8" }).trim();
}

// 必须独占测试 API 端口；只终止本测试亲自 spawn 的进程，不能接管日常开发宿主。
test("actual process crash preserves Agent interruption, video resume and image lease/checkpoint semantics", { timeout: 145000 }, async () => {
  await assert.rejects(fetch(`${origin}/health/live`, { signal: AbortSignal.timeout(1000) }), "Stop the owned fixture API before this test; never kill an unknown listener.");
  let api = startBaselineApi();
  let connection;
  const ready = () => until(async () => {
    try { return await (await fetch(`${origin}/health/ready`)).json(); } catch { return null; }
  }, value => value?.status === "healthy");
  const kill = async signal => { const closed = once(api, "exit"); api.kill(signal); await closed; };
  try {
    await ready();
    const user = await account();
    const providers = await configureProviders(await admin());
    connection = await hub(user);
    const agents = [];
    const scenarios = [["[hold]", "running"], ["[tool]", "awaiting_canvas"], ["[approval]", "awaiting_approval"]];
    if (queuedRecovery) scenarios.push(["[hold] second", "running"], ["queued-safe", "queued"]);
    for (const [marker, status] of scenarios) {
      const canvas = await user.request("/api/v1/canvas", "POST", { name: "Crash fixture", graph: { nodes: [], edges: [] } }, 201);
      const session = await user.request("/api/v1/agent/sessions", "POST", { canvasId: canvas.id });
      const run = await user.request(`/api/v1/agent/sessions/${session.id}/runs`, "POST", { providerId: providers.chat.id, modelKey: "maf-chat", prompt: `${marker} restart` });
      if (marker === "[approval]") await connection.invoke("AttachAgentRun", run.id, canvas.id);
      assertRuntime("agent", run.id);
      agents.push({ run, session, canvas, status });
    }
    for (const item of agents) {
      await until(() => user.request(`/api/v1/agent/runs/${item.run.id}`), value => value.status === item.status);
      item.events = await user.request(`/api/v1/agent/runs/${item.run.id}/events?afterSeq=0`);
    }
    const definition = await workflow(user, { nodes: [node("text", "text", { text: "[hold-video] restart" }), node("video", "videoGenerate", { duration: 15 }), node("out", "videoOutput")],
      edges: [edge("text", "text", "video", "prompt"), edge("video", "video", "out", "video")] });
    const run = await user.request(`/api/v1/workflows/${definition.id}/runs`, "POST", {}, 202);
    assertRuntime("workflow", run.id);
    const originalVideoId = await until(async () => videoFor(run.id), value => Boolean(value));
    const imageJobs = [];
    for (const prompt of ["[hold] image crash", "[hold-download] partial save crash"]) {
      const image = await user.request("/api/v1/ai/image-jobs", "POST", { key: crypto.randomUUID(), input: { providerId: providers.image.id, modelKey: "maf-image", prompt,
        canvasId: agents[0].canvas.id, nodeId: prompt, count: 2 } }, 202);
      imageJobs.push({ ...image, prompt });
    }
    await until(fixtureCalls, calls => imageJobs.every(image => calls.some(call => call.body.prompt === image.prompt)));
    const partial = await until(() => user.request(`/api/v1/ai/image-jobs/${imageJobs[1].jobId}`), value => value.status === "saving" && value.assets.length === 1);
    const postsBefore = (await fixtureCalls()).filter(call => call.method === "POST").length;
    await kill("SIGKILL");
    await connection.stop(); connection = null;
    api = startBaselineApi();
    await ready();
    for (const item of agents) {
      assertRuntime("agent", item.run.id);
      if (item.status === "queued") {
        await until(() => user.request(`/api/v1/agent/runs/${item.run.id}`), value => value.status === "succeeded");
        continue;
      }
      const interrupted = await user.request(`/api/v1/agent/runs/${item.run.id}`);
      assert.equal(interrupted.status, "interrupted");
      assert.equal(interrupted.error, "The server restarted while this run was active.");
      assert.deepEqual(await user.request(`/api/v1/agent/runs/${item.run.id}/events?afterSeq=0`), item.events);
    }
    const interrupted = await user.request(`/api/v1/workflows/${definition.id}/runs/${run.id}`);
    assert.equal(interrupted.status, "failed");
    assert.equal(videoFor(run.id), originalVideoId);
    assert.equal(interrupted.nodes.find(item => item.nodeId === "text").status, "succeeded");
    assert.equal(interrupted.nodes.find(item => item.nodeId === "out").status, "skipped");
    const resumed = await user.request(`/api/v1/workflows/${definition.id}/runs/${run.id}/resume`, "POST", {}, 202);
    assertRuntime("workflow", resumed.id);
    const completed = await until(() => user.request(`/api/v1/workflows/${definition.id}/runs/${resumed.id}`), value => ["succeeded", "failed"].includes(value.status), 40000);
    assert.equal(completed.status, "succeeded", completed.error);
    assert.equal(videoFor(run.id), originalVideoId);
    assert.equal(videoFor(resumed.id), "", "Resuming observes the old video job, it does not create a replacement");
    // 使用真实 90 秒租约，不改系统时钟、不直接修改 SQL 的过期时间。
    const unknown = await until(() => user.request(`/api/v1/ai/image-jobs/${imageJobs[0].jobId}`), value => value.status === "failed", 105000);
    assert.equal(unknown.canRetryDownload, false);
    const recovered = await until(() => user.request(`/api/v1/ai/image-jobs/${imageJobs[1].jobId}`), value => value.status === "succeeded", 15000);
    assert.equal(recovered.assets.length, 2);
    assert.equal(recovered.assets[0].assetId, partial.assets[0].assetId);
    await delay(200);
    assert.equal((await fixtureCalls()).filter(call => call.method === "POST").length, postsBefore + (queuedRecovery ? 1 : 0), "Only the previously queued Agent may start its first model call; recovery never regenerates");
    if (queuedRecovery) {
      const freshCanvas = await user.request("/api/v1/canvas", "POST", { name: "MAF after restart", graph: { nodes: [], edges: [] } }, 201);
      const fresh = await user.request("/api/v1/agent/sessions", "POST", { canvasId: freshCanvas.id });
      for (const session of [fresh, agents.at(-1).session]) {
        const submitted = await user.request(`/api/v1/agent/sessions/${session.id}/runs`, "POST", { providerId: providers.chat.id, modelKey: "maf-chat", prompt: "safe follow-up" });
        assertRuntime("agent", submitted.id);
        await until(() => user.request(`/api/v1/agent/runs/${submitted.id}`), value => value.status === "succeeded");
      }
      const freshWorkflow = await workflow(user, { nodes: [node("input", "image", { assetUrl: recovered.assets[0].url }), node("out", "imageOutput")],
        edges: [edge("input", "image", "out", "image")] });
      assert.equal((await runWorkflow(user, freshWorkflow)).status, "succeeded");
    }
    mkdirSync("artifacts/maf", { recursive: true });
    writeFileSync(`artifacts/maf/${queuedRecovery ? "maf-only-queued-restart" : "maf-only-restart"}.json`, JSON.stringify({ generatedAt: new Date().toISOString(), passed: true,
      engine: "maf-only", activeAgentStates: 3, queuedAgent: queuedRecovery, reusedVideo: true, reusedPartialImage: true }, null, 2));
  } finally {
    if (connection) await connection.stop();
    if (api.exitCode === null && api.signalCode === null) await kill("SIGTERM");
  }
});
