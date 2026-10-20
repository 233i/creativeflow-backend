import { createServer } from "node:http";

// 仅供人工启用的本机合成查询接口。不连接模型、数据库或其他业务系统，不记录请求参数。
const port = Number(process.env.CREATIVEFLOW_WORKFLOW_FIXTURE_PORT ?? 18599);
if (!Number.isInteger(port) || port < 1024 || port > 65535) throw new Error("Invalid fixture port");
let requests = 0;
const server = createServer(async (request, response) => {
  const url = new URL(request.url ?? "/", `http://127.0.0.1:${port}`);
  response.setHeader("Content-Type", "application/json; charset=utf-8");
  response.setHeader("Cache-Control", "no-store");
  if (url.pathname !== "/query" || !["GET", "POST"].includes(request.method ?? "")) {
    response.writeHead(404); response.end(JSON.stringify({ error: "fixture_query_only" })); return;
  }
  let bytes = 0; const chunks = [];
  for await (const chunk of request) {
    bytes += chunk.length;
    if (bytes > 32768) { response.writeHead(413); response.end(JSON.stringify({ error: "fixture_input_too_large" })); return; }
    chunks.push(chunk);
  }
  let body = {};
  try { if (bytes > 0) body = JSON.parse(Buffer.concat(chunks).toString("utf8")); }
  catch { response.writeHead(400); response.end(JSON.stringify({ error: "invalid_json" })); return; }
  const question = url.searchParams.get("q") ?? body.question ?? body.query;
  if (typeof question !== "string" || question.length > 16000) {
    response.writeHead(400); response.end(JSON.stringify({ error: "synthetic_question_required" })); return;
  }
  requests++;
  response.end(JSON.stringify({ data: { result: `合成外部查询结果：${question}`, synthetic: true }, requestNumber: requests }));
});
server.listen(port, "127.0.0.1", () => console.log(`Synthetic workflow query fixture: http://127.0.0.1:${port}/query (loopback only)`));
for (const signal of ["SIGINT", "SIGTERM"]) process.on(signal, () => server.close());
