# 工作流对外 API：SSE 流式返回

需要接入标准 AG-UI 客户端时，使用 [AG-UI 工作流接口](./ag-ui-workflow-api.md)；本页描述的
`external/v1` 自定义事件流。它只对公开文本提供持久化续读，进度是实时快照；
需要逐个节点事件与精确子帧续读时使用 AG-UI。

## 接口选择与准备

- `GET /api/external/v1/workflows`：已开放的发布目录。
- `GET /api/external/v1/workflows/{id}`：发布版本的输入输出契约，用于动态渲染控件。
- `POST /api/external/v1/assets/images`：使用当前工作流 Key 上传图片，返回 `assetId`。
- `POST /api/external/v1/workflows/{id}/runs`：提交运行。
- `GET /api/external/v1/runs/{runId}`：查询状态及最终 `outputs`。
- `GET /api/external/v1/runs/{runId}/events`：订阅自定义 SSE。
- `DELETE /api/external/v1/runs/{runId}`：请求取消，不等于删除审计记录。

目录接口描述公开契约，不返回密钥或内部提示词。上传、运行、查询与取消使用 Bearer Key。
Key 绑定工作流；知识库和 HTTP 凭据还需明确授权。`workflow_resource_denied`
应检查这些授权和固定版本，不能靠重复提交解决。
图片参数使用 `{"assetId":"..."}`，不能假设知道素材 UUID 就有权使用。
服务端要求 POST 的 `Idempotency-Key` 为 1–200 字符，同一业务重试保持原值。

## 提交与订阅

现有 POST 提交、GET 查询和 DELETE 取消继续使用。提交的 `stream` 默认为 false；设为 true 后，后台模型生成时保存公开文本增量。POST 始终立即返回 202，工作流由原 PostgreSQL 队列和 MAF 执行。

```bash
curl -X POST 'https://your-domain/api/external/v1/workflows/WORKFLOW_ID/runs' \
  -H 'Authorization: Bearer YOUR_API_KEY' \
  -H 'Idempotency-Key: business-request-1001' \
  -H 'Content-Type: application/json' \
  -d '{"inputs":{"question":"客户销售情况如何？"},"stream":true}'
```

```json
{
  "requestId":"req_...",
  "runId":"RUN_ID",
  "workflowId":"WORKFLOW_ID",
  "version":1,
  "status":"queued",
  "statusUrl":"/api/external/v1/runs/RUN_ID",
  "eventsUrl":"/api/external/v1/runs/RUN_ID/events"
}
```

使用创建该运行的同一个 Key 订阅 `eventsUrl`。该接口只观察已有运行，连接和断线不会触发新执行。

```bash
curl -N 'https://your-domain/api/external/v1/runs/RUN_ID/events' \
  -H 'Authorization: Bearer YOUR_API_KEY' \
  -H 'Accept: text/event-stream'
```

`stream: false` 的任务也可订阅进度与最终结果，但无法补回当时没有保存的逐步文本。媒体工作流提供进度与完成后的媒体结果。

## 事件与结果

响应为 UTF-8 `text/event-stream`，帧用空行分隔，data 为 JSON。服务端返回 `private, no-store, no-transform` 和 `X-Accel-Buffering: no`，连接空闲期间约每 15 秒发送注释心跳。

| event | 内容 | 调用方处理 |
| --- | --- | --- |
| `run.progress` | 与状态查询相同的状态、progress（0–1）、currentNode 和时间；outputs 为空 | 更新执行进度 |
| `output.delta` | output、path、delta、provisional | 按字段追加暂存文本，并保存 id |
| `run.completed` | 最终成功状态和完整 outputs | 用 outputs **替换**所有暂存预览；停止重连 |
| `run.failed` | 失败状态、error，outputs 为空 | 清除预览，展示错误，停止重连 |
| `run.cancelled` | 取消状态，outputs 为空 | 清除预览，停止重连 |
| `stream.error` | code、message、retryable | false 时停止订阅并清除预览；true 时可带游标重连 |

```text
event: run.progress
data: {"runId":"RUN_ID","status":"running","progress":0.3333333333333333,"currentNode":{"id":"llm","name":"回答问题","type":"llm"},"outputs":null}

id: RUN_ID:1
event: output.delta
data: {"output":"answer","path":"","delta":"根据查询结果，","provisional":true}

id: RUN_ID:2
event: output.delta
data: {"output":"answer","path":"","delta":"本月销售额为……","provisional":true}

event: run.completed
data: {"runId":"RUN_ID","status":"succeeded","progress":1,"outputs":{"answer":"根据查询结果，本月销售额为……"}}

```

以上状态事件省略了 requestId、workflowId、version 和时间等常规字段。只有文本增量携带持久化 id；进度是当前数据库状态快照，不保证捕获所有瞬时节点状态。连接建立后以及状态改变时发送快照。最终事件每次重连都会重新提供，调用方按 runId 幂等处理。

`path` 是公开字段内部的 JSON Pointer：普通字符串为 `""`，知识答案为 `"/text"`。答案的 grounded 和 citations 只在最终校验通过后交付。暂存文本可能因引用、结构化输出或后续节点失败而被丢弃，**不能用暂存片段执行 SQL 或触发其他业务操作**；这些操作仍等待节点完整输出通过校验。

只有直接连接到结束字段、或经过原样汇合节点连接到结束字段的模型正文会流式输出。支持普通 LLM 文本、结构化模型公开的顶层字符串字段、知识答案 text。不会发送内部提示词、隐藏思考、检索查询、引用草稿或未公开的模型输出；经过变换、HTTP 等后续处理的结果在完成后交付。

## 断线与恢复

记录最后一个已经处理的 `output.delta` id，向**同一个** eventsUrl 重连：

```bash
curl -N 'https://your-domain/api/external/v1/runs/RUN_ID/events' \
  -H 'Authorization: Bearer YOUR_API_KEY' \
  -H 'Last-Event-ID: RUN_ID:2'
```

- 服务端只重放该序号之后的文本；并重新发送当前进度或终态。处理并保存正文和游标应保持一致；若有重复，按 id 去重。
- 不传游标可从头重放。错误格式、另一个运行的 id 或超前序号返回 400，避免静默丢失内容。
- 文本日志与运行在同一个数据库，API 重启或换实例后仍可读取。删除运行时同时删除日志。已发起但因服务重启中断的模型任务沿用原失败恢复策略，不自动重新生成。
- Key 无效返回 401；不是自己的运行返回 404；资源撤权返回 403。已建立的连接在每批数据发送前复验权限，失效时发送 `stream.error` 并关闭。
- 订阅限流返回 429。当前默认每个 Key、每个 API 进程最多 8 个连接，单进程总计 128 个；可通过 `ExternalWorkflowApi:MaxStreamsPerApiKey` 和 `ExternalWorkflowApi:MaxConcurrentStreams` 调整。每次建立连接还计入原状态读取速率。
- 客户端掉线、关闭订阅或读取过慢不会取消工作流。需要取消时显式 DELETE statusUrl。
- 重新 POST 时必须保留原幂等键、inputs 和 stream 值。相同 Key/幂等键下改变参数返回 409；新业务任务使用新幂等键。

## JavaScript 与代理

发布面板“调用示例”可切换 SSE 和查询模式，并从前端站点下载 `/examples/workflow-stream.mjs`（[源码](https://github.com/233i/creativeflow-frontend/blob/main/public/examples/workflow-stream.mjs)）。示例使用 fetch 携带 Bearer Key，处理 UTF-8、跨网络块分帧、失败、取消与有限次数重连；浏览器原生 EventSource 构造参数无法设置 Authorization 请求头。正式业务通过自己的服务端保护 Key，不应把长期密钥嵌入前端包或 localStorage。

```javascript
import { workflowEvents } from './workflow-stream.mjs';

const controller = new AbortController();
try {
  for await (const event of workflowEvents(eventsUrl, apiKey, { signal: controller.signal })) {
    if (event.event === 'output.delta') appendPreview(event.data.output, event.data.path, event.data.delta);
    if (event.event === 'run.progress') showProgress(event.data);
    if (event.event === 'run.completed') replaceWithFinalOutputs(event.data.outputs);
    if (['run.failed', 'run.cancelled'].includes(event.event)) clearPreview();
  }
} catch (error) {
  clearPreview();
  showError(error.message);
}
```

反向代理应尊重 `X-Accel-Buffering: no`，不缓存、不聚合 SSE 内容，读超时应长于心跳间隔。流式模型适配支持 openai-chat、openai-responses、anthropic 和 gemini，沿用模型选择、输出预算、期限与独立网络守卫。上游不支持 SSE、格式错误或提前断流会失败，不改为非流式重发请求。

Gemini 使用 `:streamGenerateContent?alt=sse`。使用精确端点许可时，要同时配置原 `:generateContent` 和这个完整流式地址；工作流已有公网 HTTPS 通配配置时无需增加地址。固定 alt=sse 是工作流模型通道的唯一查询参数例外，向量服务和其他参数仍保持原限制。

## 验证

`WorkflowStreamHttpTests` 经过真实 MVC、隔离 PostgreSQL、API Key 授权、MAF 和模型协议适配，使用可暂停的合成上游；验证完成前收到文本、断线续读、幂等冲突、取消、上游中断、Key 与知识授权撤销、重启遗留任务和日志生命周期。`WorkflowV2ModelTests` 验证实际模型服务与网络守卫，`TextStreamProtocolTests` 验证四种协议的正文、结束帧和真实 usage。前端测试覆盖发布面板切换和可下载客户端的解析、重连、撤权及取消。

这些是测试覆盖入口，不代表当前数据库测试全部通过；执行前查看[测试环境与已知限制](local-development.md#tests)。
