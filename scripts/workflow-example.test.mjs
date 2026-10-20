import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const script = fileURLToPath(new URL("./prepare-workflow-example.mjs", import.meta.url));
const catalog = fileURLToPath(new URL("../docs/examples/credential-catalog.example.json", import.meta.url));
const ids = [1, 2, 3, 4, 5].map(value => `10000000-0000-0000-0000-${String(value).padStart(12, "0")}`);
const run = args => spawnSync(process.execPath, [script, ...args], { encoding: "utf8" });

test("prepares a draft without performing API calls and warns when a saved contract is absent", () => {
  const result = run(ids); assert.equal(result.status, 0);
  const draft = JSON.parse(result.stdout); assert.equal(draft.schemaVersion, 1);
  assert.equal(draft.graph.nodes.find(node => node.id === "answer").data.modelId, ids[2]);
  assert.match(result.stderr, /reselect/);
});

test("binds exact credential scope and keeps HTTP configuration in the node", () => {
  const result = run([...ids, catalog]); assert.equal(result.status, 0, result.stderr);
  const data = JSON.parse(result.stdout).graph.nodes.find(node => node.id === "fallback").data;
  assert.equal(data.fields[0].label, "查询问题"); assert.equal(data.fields[0].maxLength, 2000);
  assert.equal(data.outputFields[0].key, "text"); assert.equal(data.outputFields[0].label, "查询结果");
  assert.equal(data.credentialRevisionId, ids[4]); assert.equal(data.http.url, "https://example.com/query");
  assert.equal(data.http.responseLimitBytes, 1048576); assert.equal(data.operationId, undefined);
});

test("rejects invalid resource identifiers or a mismatched credential revision", () => {
  assert.notEqual(run(["invalid", ...ids.slice(1)]).status, 0);
  assert.notEqual(run([...ids.slice(0, 4), ids[0], catalog]).status, 0);
});
