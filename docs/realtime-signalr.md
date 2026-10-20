# 浏览器实时通知（SignalR）

SignalR 用于应用内实时通知和 Agent 浏览器工具交互。
外部工作流集成应使用 [REST/SSE](workflow-api-streaming.md) 或
[AG-UI](ag-ui-workflow-api.md)，不要求业务系统连接浏览器 Hub。

## 连接与授权

Hub 地址：`/hubs/creativeflow`。
普通用户先登录，再调用 `/api/v1/device/handshake` 建立已授权设备范围。
单独持有设备 ID、设备 Cookie 或管理员 Cookie，不等同于普通用户会话。

连接成功后服务端加入 `device:{guid:N}` 分组并登记在线状态；
解析到 visitor 时也会加入 `visitor:{guid:N}`。visitor 是概率关联，
不能向这个组广播账号私有结果。断开时会清理在线状态和当前 Agent 执行器绑定。

## 消息与方法

服务端通过 `ReceiveMessage` 发送 `RealtimeEnvelope`；
另有 `DeviceLinked` 设备关联通知。具体业务事件放在信封中，不是每种事件一个 Hub 方法。
工作流实时通知是更新提示，断线后仍应查询 HTTP 运行状态，不能假设通知可重放。

| 客户端调用 | 用途 |
| --- | --- |
| `Ping` | 返回 `"pong"`，验证传输往返 |
| `AttachAgentRun(runId, canvasId)` | 校验运行/画布归属并绑定浏览器执行器 |
| `DetachAgentRun(runId)` | 解除本连接绑定 |
| `SubmitToolResult(runId, toolUseId, resultJson, isError)` | 提交当前绑定运行的工具结果 |
| `SubmitApproval(runId, approvalId, approved)` | 提交当前绑定运行的工具审批 |

方法签名及所有权校验见
[`CreativeFlowHub`](../src/CreativeFlow.Api/Hubs/CreativeFlowHub.cs)。
客户端不能指定任意组或替别的连接提交工具回执。

## 客户端连接示例

```typescript
import * as signalR from "@microsoft/signalr";

// 登录及设备握手已经完成；地址由同源代理转发。
const connection = new signalR.HubConnectionBuilder()
  .withUrl("/hubs/creativeflow")
  .withAutomaticReconnect()
  .build();

connection.on("ReceiveMessage", envelope => console.log(envelope));
await connection.start();
console.log(await connection.invoke("Ping")); // pong
```

生产反向代理需要支持 WebSocket Upgrade，也要允许 SSE/长轮询回退。
开发 Next.js rewrite 的普通 HTTP 请求成功，不能证明 WebSocket 升级可用。

## 多实例

`Realtime:Backplane` 默认 `None`。多实例时设为 `Redis`，所有实例使用一致的 Redis
和共享数据库/文件配置，并实测跨实例交付。背板只解决消息转发，不自动解决进程内
执行器状态、所有后台 Worker 的多副本语义或负载均衡会话亲和；不能仅打开背板就认定可水平扩容。

Redis 在线状态使用带 TTL 的集合；断线清理失败不应被解释为用户仍在线。
实时连接恢复后重新查询状态，再恢复需要重新绑定的 Agent 执行器。

## 维护与验证

API 使用 MVC/JIT，Hub 使用普通 `Hub` 和显式方法名；共享 DTO 登记在
`ApiJsonContext`，resolver 插入链首而非替换整个链。见[架构说明](architecture.md)。

[隔离集成测试](../tests/maf-e2e/README.md)包含 HTTP/SignalR 检查。
人工验收至少包含登录、握手、连接、Ping、断线重连、HTTP 状态恢复，
使用 Agent 时再检查执行器绑定与审批。不要将未认证的 negotiate 成功当作业务授权成功。
