# AG-UI 工作流接口

CreativeFlow 的工作流仍由现有队列、MAF 编排器和节点执行器运行；AG-UI 只是标准传输层。旧的
`/api/external/v1` 提交、状态查询和自定义 SSE 接口保持不变。

实现范围：媒体创作和通用编排共用标准运行事件、客户端身份关联、持久化日志、断线精确续读、动态资源权限和原运行取消。使用官方 `AGUI.Abstractions` / `AGUI.Formatting` 1.0.0，不引入第二个 Agent 或工作流执行器。

## 运行接口

```http
POST /api/agui/v1/workflows/{workflowId}/runs
Authorization: Bearer <workflow-api-key>
Content-Type: application/json
Accept: text/event-stream
Idempotency-Key: optional-business-key
```

请求体使用 AG-UI `RunAgentInput`。工作流输入放在 `forwardedProps.inputs`，例如：

```json
{
  "threadId": "customer-10001",
  "runId": "turn-10001",
  "messages": [],
  "tools": [],
  "context": [],
  "state": {},
  "forwardedProps": {
    "inputs": {
      "question": "近半年销售金额100万的客户"
    }
  }
}
```

如果没有 `forwardedProps`，适配器会把最后一条纯文本用户消息（多个文本段会拼接）映射为 `inputs.question`。多个参数、图片或结构化字段必须使用 `forwardedProps.inputs`；`image` 参数仍是 `{"assetId":"通过当前 Key 上传接口获得的素材 ID"}`，保留原素材权限校验。

`threadId`、`runId` 必填，1–128 个非空白可见字符。它们是外部协议关联标识，不是内部数据库 UUID，也不会自动启用会话记忆。`parentRunId` 可作为关联标识保存。非空 `state`、客户端 `tools`、`context`、`resume` 不支持，明确返回 400，不会悄悄忽略或启动额外工具。重连使用 SSE 游标，不是 HITL resume。

服务端在同一个 HTTP 响应中使用官方 `AGUI.Formatting` SSE formatter 返回 `data: {...}` 帧。事件顺序为：

1. `RUN_STARTED`（保留调用方 `threadId` / `runId`），随后 `CUSTOM`（`name=workflow.accepted`）；
2. 按实际事务提交顺序发出 `STATE_SNAPSHOT` 和 `STEP_STARTED`/`STEP_FINISHED`，包括快速节点、失败节点和复用节点；跳过分支或未开始就取消的节点仅更新状态，不伪装成执行过；
3. 公开输出可流式投影时发出 `TEXT_MESSAGE_START`、`TEXT_MESSAGE_CONTENT`、`TEXT_MESSAGE_END`；
4. 长时间排队或执行时每约 15 秒发送 SSE 注释保活，不改变业务事件游标；
5. 成功返回 `RUN_FINISHED`（`result` 为发布输出），失败返回 `RUN_ERROR`，取消返回带 cancelled outcome 的 `RUN_FINISHED`。

`STATE_SNAPSHOT` 只包含运行/节点状态、进度、当前节点和错误，不包含未通过原有资源权限复验的节点正文。`nodes[]` 的每个节点包含 `id`、`type`、`name`、`status` 等字段；`currentNode` 为 `{id,name,type}` 对象。`STEP_STARTED`/`STEP_FINISHED` 的 `stepName` 直接使用节点名称，并在 `metadata` 中提供 `nodeId`、`nodeName`、`nodeType`，前端使用 `metadata.nodeId` 定位画布节点；若多个节点同名，也以 `nodeId` 区分。
最终结果仍由现有发布输出投影和素材签名逻辑提供。

`workflow.accepted.value` 返回 `internalRunId`、`statusUrl`、`eventsUrl`；响应头也包含 `X-Workflow-Run-ID` 和 `X-Workflow-Events-URL`。状态快照包含 `internalRunId`、工作流、当前状态、节点和进度，不包含内部模型提示词、凭据、非公开节点结果。内部模型日志与公开事件保持分离。

节点名称取本次运行固定图快照中 `data.title`，其次为 `data.label`、节点类型；不会读取当前草稿，也不会把提示词、凭据或节点输出放进 AG-UI 事件。外部运行状态的 `currentNode` 同样增加 `name` 字段。

公开文本是暂存预览：成功时以 `RUN_FINISHED.result` 替换；失败、取消、撤权时清除预览。知识答案仅逐步交付正文，引用和完整结构化输出在完成复验后交付。媒体通过终态 `result` 交付签名 URL，不假装视频或图片本身是文字流。

鉴权失败、工作流未发布、输入校验失败和队列已满等**受理前**错误仍使用普通 HTTP 状态码和现有
`external/v1` 错误体；只有已经受理并开始流式响应后的运行错误才使用 `RUN_ERROR`。

## 幂等、队列与断线

- `Idempotency-Key` 与原 external API 共用同一套唯一约束；没有传入时使用 `agui:{runId}`。
- 同一 Key 下客户端 `runId` 唯一并永久绑定其内部运行。改变输入、threadId 或幂等键不能用原 runId 生成第二个任务；冲突返回 409。新业务使用新 runId；网络重试保留原身份、原参数和原幂等键。
- API Key、工作流发布版本、知识库/HTTP 凭据授权、队列上限和外部并发上限均复用原实现。
- 浏览器或 AG-UI 客户端断开只会结束 SSE 订阅，不会取消后台任务，也不会重新调用模型。
- 若请求在收到受理信息前断开，可用同一请求安全重试 POST；若已取得 `eventsUrl`，直接 GET 续读，不再次提交。
- 运行状态及最终结果使用 `/api/external/v1/runs/{internalRunId}`；主动取消使用该地址的 DELETE。AG-UI 运行最终成功和取消为 `RUN_FINISHED`，失败为 `RUN_ERROR`。

```http
GET /api/agui/v1/runs/{internalRunId}/events
Authorization: Bearer <创建运行的同一个 Key>
Last-Event-ID: <最后完整消费的 SSE id>
Accept: text/event-stream
```

每个事件有 `id: {internalRunId}:{事务日志序号}:{子帧号}`，调用方必须原样保存。一次日志可能产生多个标准事件，子帧游标保证在 `TEXT_MESSAGE_START` 与 `CONTENT` 之间断开也不会遗漏或重复。无游标则重放全部；游标精确指向最后已消费的帧，仅返回之后的帧；终态之后的游标返回空 SSE 并结束。跨运行、超前或不存在的子帧返回 400。AG-UI 游标和原自定义 SSE 的文本游标不可互换。

## 安全、持久化和部署

- POST 在入队前获得 SSE 订阅额度，额度耗尽返回 429，不会创建后台付费任务。GET 也遵守 Key 查询速率及全局／Key 订阅限额，额度在断线、超时和异常时释放。
- 每批日志读出后重新校验 Key、固定发布版本、知识库与 HTTP 凭据授权，再写出正文。撤权发送 `RUN_ERROR(code=stream_access_lost, metadata={retryable:false,clearPreview:true})` 并关闭；其他 Key 不能订阅该运行。
- 可恢复订阅错误使用 `RUN_ERROR(code=stream_unavailable,metadata.retryable=true)`；仅重连原运行，不重新生成。整批写入／flush 共用 15 秒期限，慢客户端不会长期占据额度。
- 新迁移 `0041__agui_workflow_journal.sql`：协议身份在运行快照中保存，唯一索引防止重受理；公开事务日志在 `workflow_run_event` 保存，和运行／节点／公开文本一起提交。删运行时级联删日志。普通 external 请求仍使用原快照与原幂等摘要，不写 AG-UI 日志。增量迁移 `0043__agui_node_names.sql` 为节点事件写入固定图快照中的显示名称。
- 运行锁先于节点锁，取消、普通节点更新和恢复使用统一锁序。完成标记在事务提交前、全部节点事件写入后产生，终态之后的晚到回调不再追加公开日志；服务器重启还会补齐“主运行已终止，但节点收尾被崩溃打断”的 AG-UI 审计，不重试模型。
- 正式发布需部署后端及前端，启动后端应用迁移；反向代理支持长响应且关闭 SSE 缓冲。后端返回 `X-Accel-Buffering:no`、`Cache-Control:private,no-store,no-transform`。

## 管理页面和 JavaScript 客户端

工作流详情 → 发布与版本 → 调用示例，提供 **AG-UI 标准协议 / SSE 流式返回 / 查询最终结果** 三种方式，均根据已发布契约而非未发布草稿生成参数。AG-UI 页给出 POST、续读、身份与取消说明，下载 `/examples/workflow-agui.mjs`。

```javascript
import { runWorkflowAgUi, cancelWorkflowAgUi } from './workflow-agui.mjs';
await runWorkflowAgUi({
  baseUrl: 'https://your-domain', workflowId, apiKey,
  threadId: 'customer-10001', runId: 'turn-10001',
  inputs: { question: '近半年销售金额100万的客户' },
  onEvent: event => renderAgUiEvent(event),
  onCheckpoint: checkpoint => saveCheckpoint(checkpoint),
  signal: subscriptionAbort.signal,
});
// 显式取消后台任务；subscriptionAbort.abort() 只断开订阅。
await cancelWorkflowAgUi({ baseUrl: 'https://your-domain', apiKey, internalRunId });
```

示例解析分块 UTF-8、CRLF、多行 data 和保活注释，去重后才交付 UI，并在消费成功后保存游标。重连优先 GET；受理信息丢失才幂等重试原 POST。可传 `checkpoint` 在关页后恢复；已完成的 checkpoint 重读原运行以复验资源和刷新签名 URL。401/403/404/400/409、业务失败或撤权不自动重试；429/网关临时故障和断流有限指数退避。UI 或 checkpoint 回调失败不会自动重复副作用。正式系统应让业务后端保护 API Key，不把 Key 存入浏览器持久化 checkpoint。

## 适配边界

当前接口把工作流节点映射为 AG-UI 的 step/state 事件，不把内部模型调用、凭据或知识库正文直接广播给客户端。
这保持了工作流的资源撤权语义，同时让通用 AG-UI 客户端可以显示排队、节点执行、流式文本和终态。

部署前应使用隔离数据库和受控上游验证首段早于完成、精确子帧续读、幂等冲突、权限撤销、取消、
服务器恢复和慢客户端等路径；不要把模拟验收当作线上模型质量证明。
