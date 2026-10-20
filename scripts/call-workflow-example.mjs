import { randomUUID } from "node:crypto";
import { setTimeout as delay } from "node:timers/promises";

// One POST followed by GET polling. Unknown POST outcomes are never automatically resubmitted.
const origin = new URL(process.env.CREATIVEFLOW_ORIGIN ?? "http://localhost:5298");
if (origin.username || origin.password || origin.pathname !== "/" || origin.search || origin.hash
  || !(origin.protocol === "https:" || origin.protocol === "http:" && ["localhost", "127.0.0.1", "[::1]"].includes(origin.hostname)))
  throw new Error("Use an HTTPS origin, or loopback HTTP for development.");
const apiKey = process.env.CREATIVEFLOW_API_KEY;
const workflow = process.argv[2];
const question = process.argv[3];
const resumeRun = process.env.CREATIVEFLOW_RUN_ID;
const validId = value => typeof value === "string"
  && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value)
  && value !== "00000000-0000-0000-0000-000000000000";
if (!apiKey || (resumeRun ? !validId(resumeRun) : !validId(workflow) || !question)) {
  console.error("Set CREATIVEFLOW_API_KEY. Usage: node scripts/call-workflow-example.mjs WORKFLOW_UUID 'question'; set a valid CREATIVEFLOW_RUN_ID UUID for GET-only recovery.");
  process.exitCode = 2;
} else {
  const idempotency = process.env.CREATIVEFLOW_IDEMPOTENCY_KEY ?? randomUUID();
  const headers = { Authorization: `Bearer ${apiKey}`, "Content-Type": "application/json" };
  const endpoint = path => new URL(`/api/external/v1/${path}`, origin);
  let runId = resumeRun?.toLowerCase();
  try {
    if (!runId) {
      console.log(`Idempotency-Key: ${idempotency} (reuse this key if the submission result is unclear)`);
      const response = await fetch(endpoint(`workflows/${encodeURIComponent(workflow)}/runs`), {
        method: "POST", headers: { ...headers, "Idempotency-Key": idempotency }, redirect: "error",
        body: JSON.stringify({ inputs: { question } }), signal: AbortSignal.timeout(30000),
      });
      if (!response.ok) throw new Error(`Submission returned HTTP ${response.status}; not resubmitted.`);
      const accepted = await response.json();
      if (!validId(accepted?.runId)) throw new Error("Invalid run receipt. Reuse the printed idempotency key to check the submission.");
      // Preserve idempotent recovery if the receipt is malformed; never suggest querying an unverified ID.
      runId = accepted.runId.toLowerCase();
    }
    console.log(`Run ID: ${runId}; querying only, without generating again.`);
    const deadline = Date.now() + 5 * 60 * 1000;
    let finished = false;
    while (Date.now() < deadline) {
      const response = await fetch(endpoint(`runs/${encodeURIComponent(runId)}`), { headers, redirect: "error", signal: AbortSignal.timeout(30000) });
      if (response.status === 429) { await delay(10000); continue; }
      if (!response.ok) throw new Error(`Result query returned HTTP ${response.status}; check access or availability. No generation retry.`);
      const result = await response.json();
      if (result?.runId?.toLowerCase?.() !== runId || !["queued", "running", "succeeded", "failed", "cancelled"].includes(result.status))
        throw new Error("Invalid result receipt: run ID or status does not match the requested task. No generation retry.");
      console.log(`Status: ${result.status}`);
      if (["succeeded", "failed", "cancelled"].includes(result.status)) {
        console.log(JSON.stringify(result, null, 2));
        finished = true;
        if (result.status !== "succeeded") process.exitCode = 1;
        break;
      }
      await delay(2500);
    }
    if (!finished) {
      console.error("The polling window ended before a terminal result. The server task was not cancelled; query this run ID later.");
      process.exitCode = 3;
    }
    console.log(`To query later without resubmitting, set CREATIVEFLOW_RUN_ID=${runId}.`);
  } catch (error) {
    console.error(error instanceof Error ? error.message : "Request failed");
    if (runId) console.error(`GET-only recovery: set CREATIVEFLOW_RUN_ID=${runId}.`);
    else console.error(`Submission may have been accepted. Reuse Idempotency-Key ${idempotency}; do not generate a new key for the same request.`);
    process.exitCode = 1;
  }
}
