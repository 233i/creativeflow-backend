import assert from "node:assert/strict";
import { createRequire } from "node:module";
import { execFileSync } from "node:child_process";

export const origin = "http://127.0.0.1:55443";
export const fixtureOrigin = "http://127.0.0.1:55445";
const requireTestPackage = createRequire(new URL("./package.json", import.meta.url));
export const delay = ms => new Promise(resolve => setTimeout(resolve, ms));

// SQL 只证明受理存在且不再写旧绑定；唯一 MAF 实现另由生产程序集和 DI 门禁验证。
export function assertRuntime(kind, id) {
  assert.ok(["agent", "workflow"].includes(kind));
  assert.match(id, /^[a-f0-9-]{36}$/);
  const accepted = execFileSync("psql", ["-h", "127.0.0.1", "-p", "55439", "-U", "creativeflow_maf_test", "-d", "creativeflow_maf_e2e", "-At", "-c",
    `SELECT count(*) FROM ${kind}_run run WHERE run_id='${id}' AND NOT EXISTS (SELECT 1 FROM ${kind}_run_runtime binding WHERE binding.run_id=run.run_id)`], { encoding: "utf8" }).trim();
  assert.equal(accepted, "1", `${kind} acceptance must not depend on retired engine bindings`);
}

// 独立 Cookie 容器，不读取用户浏览器 Cookie 或本地存储。
export class Client {
  cookies = new Map();
  cookie() { return [...this.cookies].map(([key, value]) => `${key}=${value}`).join("; "); }
  async request(path, method = "GET", body, expected = 200, headers = {}) {
    const response = await fetch(`${origin}${path}`, {
      method, headers: { Cookie: this.cookie(), ...(body === undefined || body instanceof FormData ? {} : { "content-type": "application/json" }), ...headers },
      body: body === undefined ? undefined : body instanceof FormData ? body : JSON.stringify(body),
      signal: AbortSignal.timeout(30000),
    });
    for (const line of response.headers.getSetCookie()) {
      const [pair] = line.split(";"); const position = pair.indexOf("=");
      this.cookies.set(pair.slice(0, position), pair.slice(position + 1));
    }
    const text = await response.text();
    assert.equal(response.status, expected, `${method} ${path}: ${response.status} ${text.slice(0, 1500)}`);
    return text ? JSON.parse(text) : null;
  }
}

export async function account() {
  const client = new Client();
  await client.request("/api/v1/auth/password/register", "POST", {
    email: `maf-${crypto.randomUUID()}@example.invalid`, password: "Maf-p01-fixture-only!", displayName: "Migration fixture",
  });
  const handshake = await client.request("/api/v1/device/handshake", "POST", {
    v: 1, stable: { timeZone: "UTC", platform: `maf-${crypto.randomUUID()}` }, volatile: {}, clientStoredId: null,
  });
  client.deviceId = handshake.deviceId;
  return client;
}

export async function admin() {
  const client = new Client();
  await client.request("/api/v1/admin/auth/login", "POST", { username: "maf-admin", password: "Maf-p01-fixture-only!" });
  return client;
}

export async function configureProviders(client) {
  const providers = {};
  const existing = (await client.request("/api/v1/admin/ai/providers")).providers;
  for (const [kind, type, modelKey] of [["chat", "openai-chat", "maf-chat"], ["image", "openai-images", "maf-image"], ["video", "openai-video", "doubao-seedance-2.0-fast"], ["extension", "xai-video", "grok-imagine-video"]]) {
    const category = kind === "extension" ? "video" : kind;
    const created = existing.find(provider => provider.name === `MAF fixture ${kind}`) ?? await client.request("/api/v1/admin/ai/providers", "POST", {
      name: `MAF fixture ${kind}`, type, baseUrl: `${fixtureOrigin}/v1`, apiKey: "synthetic-fixture-key", enabled: true,
      models: [{ modelKey, name: `MAF ${kind}`, category, enabled: true, supportsVision: true, supportsFunctionCall: true, supportsThinking: true }],
    }, 201);
    assert.equal(created.baseUrl, `${fixtureOrigin}/v1`, "Never reuse a non-fixture provider");
    providers[kind] = created;
  }
  for (const kind of ["chat", "image", "video"]) {
    await client.request(`/api/v1/admin/ai/model-slots/${kind}`, "PUT", { providerId: providers[kind].id, modelId: providers[kind].models[0].id });
  }
  if (!providers.video.models.some(model => model.modelKey === "grok-imagine-video")) {
    await client.request(`/api/v1/admin/ai/providers/${providers.video.id}/models`, "POST", {
      modelKey: "grok-imagine-video", name: "MAF extension override", category: "video", type: "xai-video", enabled: true,
    }, 201);
  }
  return providers;
}

export async function until(action, predicate, timeout = 30000) {
  const deadline = performance.now() + timeout;
  let result;
  while (performance.now() < deadline) {
    result = await action();
    if (predicate(result)) return result;
    await delay(50);
  }
  throw new Error(`Timed out waiting for baseline result: ${JSON.stringify(result).slice(0, 1200)}`);
}

export async function hub(client) {
  const { HubConnectionBuilder, LogLevel } = requireTestPackage("@microsoft/signalr");
  const connection = new HubConnectionBuilder().withUrl(`${origin}/hubs/creativeflow`, { headers: { Cookie: client.cookie() } })
    .configureLogging(LogLevel.Error).build();
  await connection.start();
  assert.equal(await connection.invoke("Ping"), "pong");
  return connection;
}

export const fixtureCalls = async () => (await fetch(`${fixtureOrigin}/__calls`)).json();
export const fixtureCallCount = async () => (await (await fetch(`${fixtureOrigin}/__call-count`)).json()).count;
export const fixtureCallsSince = async after => {
  assert.ok(Number.isInteger(after) && after >= 0);
  const response = await fetch(`${fixtureOrigin}/__calls?after=${after}`);
  assert.equal(response.status, 200);
  return response.json();
};
export const node = (id, type, data = {}) => ({ id, type, position: { x: 0, y: 0 }, data });
export const edge = (source, sourceHandle, target, targetHandle, id = `${source}-${target}`) => ({ id, source, sourceHandle, target, targetHandle });

export async function workflow(client, graph) {
  return client.request("/api/v1/workflows", "POST", { name: "Migration fixture", schemaVersion: 1, graph }, 201);
}

export async function runWorkflow(client, definition) {
  const run = await client.request(`/api/v1/workflows/${definition.id}/runs`, "POST", {}, 202);
  assertRuntime("workflow", run.id);
  return until(() => client.request(`/api/v1/workflows/${definition.id}/runs/${run.id}`),
    value => ["succeeded", "failed", "cancelled"].includes(value.status), 40000);
}
