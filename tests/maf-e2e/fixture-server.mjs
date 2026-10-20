import http from "node:http";
import { readFileSync } from "node:fs";

// 纯合成上游：只有回环监听，不接受/转发任何真实服务商请求或密钥。
const root = process.env.CREATIVEFLOW_MAF_TEST_ROOT;
if (!root || !/^\/(private\/)?tmp\/creativeflow-maf-[\w.]+$/.test(root)) throw new Error("Dedicated test root required.");
const png = readFileSync(`${root}/data/fixture.png`);
const video = readFileSync(`${root}/data/fixture.mp4`);
const calls = [];
const downloadAttempts = new Map();
const videoReadyAt = new Map();
let sequence = 0;
const json = (response, body, status = 200) => {
  response.writeHead(status, { "content-type": "application/json" });
  response.end(JSON.stringify(body));
};
const server = http.createServer(async (request, response) => {
  try {
    const url = new URL(request.url, "http://127.0.0.1:55445");
    const path = url.pathname;
    if (path === "/__call-count") return json(response, { count: calls.length });
    if (path === "/__calls") {
      const after = Number(url.searchParams.get("after") ?? 0);
      if (!Number.isInteger(after) || after < 0 || after > calls.length) return json(response, { error: "invalid capture cursor" }, 400);
      return json(response, calls.slice(after));
    }
    if (path.startsWith("/recoverable/") || path.startsWith("/hold-download/")) {
      const attempt = (downloadAttempts.get(path) ?? 0) + 1;
      downloadAttempts.set(path, attempt);
      if (path.startsWith("/hold-download/") && attempt === 1) await new Promise(resolve => setTimeout(resolve, 20000));
      if (attempt === 1) return json(response, { error: "synthetic download failure" }, 503);
      response.writeHead(200, { "content-type": "image/png" });
      return response.end(png);
    }
    if (path === "/fixture.mp4" || path.endsWith("/content")) {
      response.writeHead(200, { "content-type": "video/mp4" });
      return response.end(video);
    }
    if (path === "/fixture.png") {
      response.writeHead(200, { "content-type": "image/png" });
      return response.end(png);
    }
    const chunks = [];
    for await (const chunk of request) chunks.push(chunk);
    const raw = Buffer.concat(chunks).toString();
    let body;
    try { body = JSON.parse(raw || "{}"); } catch {
      body = { multipart: raw, n: Number(raw.match(/name="?n"?\r\n\r\n(\d+)/)?.[1] ?? 1),
        prompt: raw.match(/name="?prompt"?\r\n\r\n([\s\S]*?)\r\n--/)?.[1] ?? "" };
    }
    calls.push({ index: ++sequence, method: request.method, path, body, at: performance.now(), atUtc: Date.now() });
    if (path === "/v1/models") return json(response, { data: [] });
    if (path === "/v1/images/generations" || path === "/v1/images/edits") {
      const prompt = body.prompt ?? body.multipart ?? "";
      if (prompt.includes("[unknown]")) return request.socket.destroy();
      if (prompt.includes("[delay]")) await new Promise(resolve => setTimeout(resolve, 800));
      if (prompt.includes("[hold]")) await new Promise(resolve => setTimeout(resolve, 20000));
      if (prompt.includes("[downloadfail]")) return json(response, { data: [{ url: `http://127.0.0.1:55445/recoverable/${sequence}.png` }] });
      if (prompt.includes("[hold-download]")) return json(response, { data: [{ b64_json: png.toString("base64") }, { url: `http://127.0.0.1:55445/hold-download/${sequence}.png` }] });
      return json(response, { data: Array.from({ length: Math.min(body.n ?? 1, 4) }, () => ({ b64_json: png.toString("base64") })) });
    }
    if (path === "/v1/videos" && request.method === "POST") {
      const id = `fixture-video-${sequence}`;
      videoReadyAt.set(id, Date.now() + (body.prompt?.includes("[hold-video]") ? 20000 : 0));
      return json(response, { id, status: "in_progress", progress: 20 });
    }
    if ((path === "/v1/videos/generations" || path === "/v1/videos/extensions") && request.method === "POST") {
      return json(response, { request_id: `fixture-xai-${sequence}` });
    }
    if (path.startsWith("/v1/videos/") && request.method === "GET") {
      if (Date.now() < (videoReadyAt.get(path.split("/").at(-1)) ?? 0)) return json(response, { status: "in_progress", progress: 50 });
      return json(response, { id: path.split("/").at(-1), status: path.includes("fixture-xai-") ? "done" : "completed", progress: 100,
        url: "http://127.0.0.1:55445/fixture.mp4", video: { url: "http://127.0.0.1:55445/fixture.mp4" } });
    }
    if (path === "/v1/chat/completions") {
      const messages = body.messages ?? [];
      const turn = messages.findLastIndex(message => message.role === "user");
      const prompt = JSON.stringify(messages[turn]?.content ?? "");
      if (prompt.includes("[hold]")) {
        await new Promise(resolve => setTimeout(resolve, 20000));
      }
      const toolResults = messages.slice(turn + 1).filter(message => message.role === "tool");
      const hasResult = toolResults.length > 0;
      const tool = prompt.includes("[approval]") ? "delete_nodes" : prompt.includes("[tool]") ? "read_canvas" : null;
      response.writeHead(200, { "content-type": "text/event-stream" });
      if (prompt.includes("[benchmark-multi]") && toolResults.length < 4) {
        const round = toolResults.length / 2;
        const tool_calls = [0, 1].map(index => ({ index, id: `fixture-${round}-${index}`, function: { name: "read_canvas", arguments: "{}" } }));
        response.write(`data: ${JSON.stringify({ choices: [{ delta: { tool_calls }, finish_reason: "tool_calls" }] })}\n\n`);
      } else if (tool && !hasResult) {
        const argumentsJson = tool === "delete_nodes" ? '{"nodeIds":[]}' : "{}";
        response.write(`data: ${JSON.stringify({ choices: [{ delta: { tool_calls: [{ index: 0, id: "fixture-call", function: { name: tool, arguments: argumentsJson } }] }, finish_reason: "tool_calls" }] })}\n\n`);
      } else {
        response.write('data: {"choices":[{"delta":{"reasoning_content":"fixture-thinking"},"finish_reason":null}]}\n\n');
        response.write('data: {"choices":[{"delta":{"content":"fixture-done"},"finish_reason":"stop"}]}\n\n');
      }
      response.end('data: {"choices":[],"usage":{"prompt_tokens":3,"completion_tokens":5}}\n\ndata: [DONE]\n\n');
      return;
    }
    return json(response, { error: `No fixture for ${request.method} ${path}` }, 400);
  } catch (error) {
    if (!response.headersSent) json(response, { error: error.message }, 500);
    else response.destroy(error);
  }
});
server.listen(55445, "127.0.0.1", () => console.log("Synthetic fixture provider on 127.0.0.1:55445"));
for (const signal of ["SIGINT", "SIGTERM"]) process.on(signal, () => server.close(() => process.exit(0)));
