import { readFile } from "node:fs/promises";

// Only prepares a draft request on stdout. It never logs in, publishes or calls a model.
const names = ["library", "version", "model", "credential", "revision"];
const args = process.argv.slice(2);
const values = args.slice(0, names.length);
if (args.length < names.length || args.length > names.length + 1 || values.some(value => !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value) || /^0{8}-0{4}-0{4}-0{4}-0{12}$/.test(value))) {
  console.error("Usage: node scripts/prepare-workflow-example.mjs LIBRARY_ID VERSION_ID CHAT_MODEL_ID CREDENTIAL_ID REVISION_ID [SAFE_CREDENTIAL_CATALOG_JSON]");
  process.exitCode = 2;
} else {
  const graph = JSON.parse(await readFile(new URL("../docs/examples/knowledge-routing.workflow.json", import.meta.url), "utf8"));
  const byId = Object.fromEntries(graph.nodes.map(node => [node.id, node.data]));
  [byId.retrieve.libraryId, byId.retrieve.versionId, byId.answer.modelId, byId.fallback.credentialId, byId.fallback.credentialRevisionId] = values;
  if (args[5]) {
    const catalog = JSON.parse(await readFile(args[5], "utf8"));
    const credential = Array.isArray(catalog) ? catalog.find(item => item.id?.toLowerCase() === values[3].toLowerCase()) : null;
    const revision = credential?.revisions?.find(item => item.id?.toLowerCase() === values[4].toLowerCase());
    const scopes = revision?.definition?.requests;
    if (revision?.definition?.requestDigest || scopes?.length !== 1 || scopes[0].method !== "GET")
      throw new Error("Select a new credential revision with one exact GET scope. Migrated locked query contracts cannot be replaced by this example.");
    const url = new URL(scopes[0].url);
    if (url.origin !== revision.definition.origin || !["http:", "https:"].includes(url.protocol) || url.username || url.password || url.search || url.hash)
      throw new Error("Invalid credential request scope.");
    byId.fallback.http.url = url.toString();
  } else {
    console.error("Before publishing, reselect a permitted credential and verify the exact HTTP URL and response mapping in the editor.");
  }
  console.log(JSON.stringify({ name: "合成知识问答与外部查询", schemaVersion: 1, graph }, null, 2));
}
