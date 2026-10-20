import { test } from "node:test";
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { once } from "node:events";
import { createServer } from "node:http";
import { fileURLToPath } from "node:url";

const script = fileURLToPath(new URL("./call-workflow-example.mjs", import.meta.url));
const workflowId = "10000000-0000-0000-0000-000000000001";
const runId = "10000000-0000-0000-0000-000000000002";
const syntheticKey = "synthetic-client-test-not-a-real-key";
const idempotencyKey = "synthetic-client-request-01";
const question = "合成测试问题";

async function fixture(t, handler) {
  const requests = [];
  const errors = [];
  const server = createServer(async (request, response) => {
    try {
      let text = "";
      for await (const chunk of request) text += chunk;
      const entry = { method: request.method, path: request.url, headers: request.headers, body: text ? JSON.parse(text) : undefined };
      requests.push(entry);
      handler(request, response, entry);
    } catch (error) {
      errors.push(error);
      response.writeHead(500).end();
    }
  });
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  t.after(async () => {
    server.closeAllConnections();
    await new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
    assert.deepEqual(errors, []);
  });
  return { origin: `http://127.0.0.1:${server.address().port}`, requests };
}

function json(response, status, body) {
  response.writeHead(status, { "Content-Type": "application/json" }).end(JSON.stringify(body));
}

async function client(origin, options = {}) {
  const environment = { ...process.env };
  for (const name of Object.keys(environment)) if (name.startsWith("CREATIVEFLOW_")) delete environment[name];
  Object.assign(environment, { CREATIVEFLOW_ORIGIN: origin, CREATIVEFLOW_API_KEY: syntheticKey,
    CREATIVEFLOW_IDEMPOTENCY_KEY: idempotencyKey, NODE_USE_ENV_PROXY: "0" }, options.env);
  const child = spawn(process.execPath, [script, ...(options.args ?? [workflowId, question])], {
    env: environment, stdio: ["ignore", "pipe", "pipe"],
  });
  let stdout = "";
  let stderr = "";
  child.stdout.setEncoding("utf8").on("data", value => { stdout += value; });
  child.stderr.setEncoding("utf8").on("data", value => { stderr += value; });
  const timer = setTimeout(() => child.kill("SIGKILL"), 40000);
  try {
    const [code, signal] = await once(child, "close");
    assert.equal(signal, null, "The test client must finish without forced termination.");
    assert.ok(!stdout.includes(syntheticKey) && !stderr.includes(syntheticKey), "Client output must not contain its API Key.");
    return { code, stdout, stderr };
  } finally {
    clearTimeout(timer);
    if (child.exitCode === null && child.signalCode === null) child.kill();
  }
}

test("submits once, polls queued/running/succeeded and never follows a receipt URL", async t => {
  let queries = 0;
  const service = await fixture(t, (_request, response, entry) => {
    if (entry.method === "POST") json(response, 202, { runId, statusUrl: "/must-not-use-receipt-url" });
    else json(response, 200, { runId, status: ["queued", "running", "succeeded"][queries++], outputs: { answer: "合成结果" } });
  });
  const result = await client(service.origin);
  assert.equal(result.code, 0, result.stderr);
  assert.deepEqual(service.requests.map(request => request.method), ["POST", "GET", "GET", "GET"]);
  assert.equal(service.requests[0].path, `/api/external/v1/workflows/${workflowId}/runs`);
  assert.deepEqual(service.requests[0].body, { inputs: { question } });
  assert.equal(service.requests[0].headers["idempotency-key"], idempotencyKey);
  assert.ok(service.requests.every(request => request.headers.authorization === `Bearer ${syntheticKey}` && !request.headers.cookie));
  assert.ok(service.requests.slice(1).every(request => request.path === `/api/external/v1/runs/${runId}`));
  assert.match(result.stdout, /Status: queued[\s\S]*Status: running[\s\S]*Status: succeeded/);
});

for (const status of ["succeeded", "failed", "cancelled"]) {
  test(`GET-only recovery of ${status} never submits and reports the correct exit status`, async t => {
    const service = await fixture(t, (_request, response) => json(response, 200, { runId, status }));
    const result = await client(service.origin, { args: [], env: { CREATIVEFLOW_RUN_ID: runId.toUpperCase() } });
    assert.equal(result.code, status === "succeeded" ? 0 : 1, result.stderr);
    assert.deepEqual(service.requests.map(request => [request.method, request.path]), [["GET", `/api/external/v1/runs/${runId}`]]);
  });
}

test("a lost POST response is not resubmitted and preserves the original idempotency key", async t => {
  const service = await fixture(t, request => request.socket.destroy());
  const result = await client(service.origin);
  assert.equal(result.code, 1);
  assert.deepEqual(service.requests.map(request => request.method), ["POST"]);
  assert.match(result.stderr, /Submission may have been accepted/);
  assert.ok(result.stderr.includes(idempotencyKey));
  assert.doesNotMatch(result.stderr, /GET-only recovery/);
});

test("a timed-out POST is not resubmitted or treated as a known run", { timeout: 38000 }, async t => {
  // The real client deadline is 30 seconds; the fixture receives the request but never sends a receipt.
  const service = await fixture(t, () => {});
  const result = await client(service.origin);
  assert.equal(result.code, 1);
  assert.deepEqual(service.requests.map(request => request.method), ["POST"]);
  assert.match(result.stderr, /Submission may have been accepted/);
  assert.ok(result.stderr.includes(idempotencyKey));
  assert.doesNotMatch(result.stderr, /GET-only recovery/);
});

test("a malformed accepted run ID does not produce a GET or an invalid recovery instruction", async t => {
  const service = await fixture(t, (_request, response) => json(response, 202, { runId: "-".repeat(36) }));
  const result = await client(service.origin);
  assert.equal(result.code, 1);
  assert.deepEqual(service.requests.map(request => request.method), ["POST"]);
  assert.match(result.stderr, /Invalid run receipt/);
  assert.ok(result.stderr.includes(idempotencyKey));
  assert.doesNotMatch(result.stderr, /GET-only recovery/);
});

for (const responseBody of [{ runId: workflowId, status: "succeeded" }, { runId, status: "invented" }]) {
  test(`rejects a mismatched result identity or status: ${responseBody.runId}/${responseBody.status}`, async t => {
    const service = await fixture(t, (_request, response) => json(response, 200, responseBody));
    const result = await client(service.origin, { args: [], env: { CREATIVEFLOW_RUN_ID: runId } });
    assert.equal(result.code, 1);
    assert.deepEqual(service.requests.map(request => request.method), ["GET"]);
    assert.match(result.stderr, /Invalid result receipt/);
  });
}

test("access revocation stops result queries without creating another task", async t => {
  const service = await fixture(t, (_request, response) => json(response, 403, { error: "access_denied" }));
  const result = await client(service.origin, { args: [], env: { CREATIVEFLOW_RUN_ID: runId } });
  assert.equal(result.code, 1);
  assert.deepEqual(service.requests.map(request => request.method), ["GET"]);
  assert.match(result.stderr, /HTTP 403/);
  assert.match(result.stderr, /GET-only recovery/);
});

test("HTTP 429 only delays GET polling, without another POST", async t => {
  let queries = 0;
  const service = await fixture(t, (_request, response, entry) => {
    if (entry.method === "POST") json(response, 202, { runId });
    else if (queries++ === 0) json(response, 429, { error: "rate_limited" });
    else json(response, 200, { runId, status: "succeeded" });
  });
  const result = await client(service.origin);
  assert.equal(result.code, 0, result.stderr);
  assert.deepEqual(service.requests.map(request => request.method), ["POST", "GET", "GET"]);
});

test("a redirect cannot forward the API Key to a second origin", async t => {
  const destination = await fixture(t, (_request, response) => json(response, 200, { runId, status: "succeeded" }));
  const service = await fixture(t, (_request, response) => response.writeHead(307, { Location: destination.origin }).end());
  const result = await client(service.origin);
  assert.equal(result.code, 1);
  assert.equal(service.requests.length, 1);
  assert.deepEqual(destination.requests, []);
});

test("invalid local arguments or an insecure non-loopback origin fail before sending the Key", async t => {
  const service = await fixture(t, (_request, response) => json(response, 500, {}));
  const invalidWorkflow = await client(service.origin, { args: ["-".repeat(36), question] });
  const invalidRun = await client(service.origin, { args: [], env: { CREATIVEFLOW_RUN_ID: "00000000-0000-0000-0000-000000000000" } });
  const insecureOrigin = await client("http://example.invalid");
  assert.equal(invalidWorkflow.code, 2);
  assert.equal(invalidRun.code, 2);
  assert.notEqual(insecureOrigin.code, 0);
  assert.deepEqual(service.requests, []);
});
