# 实时通信（SignalR）

## 运行时约束

当前 API 使用 MVC/JIT；SignalR 继续保留显式协议与静态 JSON 契约，见
[运行时约束](aot-constraints.md)。项目不使用动态生成的强类型客户端代理：

> **禁止 `Hub<TClient>` 与 `IHubContext<THub,TClient>`。** 其 `TypedClientBuilder<T>` 用 `Reflection.Emit` 动态生成客户端代理，标注 `[RequiresDynamicCode]`，编译告警 + 运行时抛异常。

## Hub 契约

`CreativeFlowHub : Hub`（普通 Hub），挂载在 `/hubs/creativeflow`。

**服务端 → 客户端**（方法名集中在 `RealtimeMethods`，因为失去了强类型 Hub 的编译期检查）：

| 方法 | 载荷 | 说明 |
|---|---|---|
| `ReceiveMessage` | `RealtimeEnvelope` | 通用消息信封 |
| `DeviceLinked` | `RealtimeEnvelope` | 设备被关联到访客 |

**客户端 → 服务端**：

| 方法 | 返回 | 说明 |
|---|---|---|
| `Ping` | `Task<string>` → `"pong"` | 传输连通性检查 |

新增 Hub 方法时的约束：返回值只能是 `Task`/`Task<T>`/`ValueTask`/`ValueTask<T>`；**禁止 `IAsyncEnumerable<T>`/`ChannelReader<T>` 且 `T` 为结构体**（这会在**启动时**抛异常，直接拖垮整个应用，而不是等到调用时）。

## 发送模式

```csharp
// AOT 安全：字符串式方法名
await hub.Clients.Group(HubGroups.ForDevice(deviceId))
         .SendAsync(RealtimeMethods.ReceiveMessage, envelope, ct);
```

应用层通过 `IRealtimeNotifier` 发送，不直接接触 `IHubContext`，magic string 因此被限制在 `CreativeFlow.Api/Hubs/HubGroups.cs` 一处。

**所有过线类型必须在 `ApiJsonContext` 中有 `[JsonSerializable]` 条目**，且注册方式必须是：

```csharp
options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default);
```

注意是 `Insert(0, …)` 而**不是**给 `TypeInfoResolver` 赋值——赋值会丢弃 SignalR 为自身协议类型安装的 resolver。

## 连接与设备绑定

不使用 `IUserIdProvider`——实时通信按设备/访客分组，账号资源权限仍由 HTTP 请求中的账号 session
和数据库作用域控制。改用分组：

```
OnConnectedAsync:
    从 Cookie 读 creativeflow_did
    缺失或非法 → Context.Abort()      ← 强制客户端先完成握手
    加入 device:{deviceId}
    加入 visitor:{visitorId}（若已解析）
    IPresenceTracker.MarkOnline(deviceId, connectionId)

OnDisconnectedAsync:
    IPresenceTracker.MarkOffline(...)  ← 用 CancellationToken.None，
                                          此时连接令牌已取消，但清理必须完成
```

分组命名：`device:{guid:N}` / `visitor:{guid:N}`。分组天然支持 backplane，单机与集群无需改代码。

⚠️ **发送到 `visitor:` 分组时要谨慎**：该分组内除第一台设备外都是概率性关联的，**不要发送敏感内容**。

## 在线状态

Redis Set 而非计数器：一台设备可能有多个连接（多标签页），而计数器在进程崩溃、未执行断开处理时会永久漂移。Set 成员操作幂等，且带 12 小时 TTL 兜底清理失败的场景。

## Backplane

由配置开关控制：

```json
{ "Realtime": { "Backplane": "None" } }   // 或 "Redis"
```

**默认 `None`**，适用于单实例部署；多实例需显式开启共享 Redis backplane，
并验证不同 API 实例之间的消息交付。Redis 健康检查通过不等于跨实例广播已经启用。

需要多节点时改为 `Redis` 即可，代码无需改动。

## 客户端接入

```ts
import * as signalR from '@microsoft/signalr';

const connection = new signalR.HubConnectionBuilder()
  .withUrl('/hubs/creativeflow')          // 经同源反向代理
  .withAutomaticReconnect()
  .build();

connection.on('ReceiveMessage', (envelope) => { /* … */ });
await connection.start();
```

**必须先调用 `/api/v1/device/handshake` 拿到 Cookie**，否则 Hub 会拒绝连接。

⚠️ **开发环境的 WebSocket 升级**：反向代理必须支持 WebSocket 升级。若 `/hubs` 出问题，可核对客户端 API 地址，或允许回退到 SSE / 长轮询（negotiate 会同时返回这三种传输）。生产环境将前后端置于同一反向代理 origin 下即可。

## 验证

可使用[隔离集成测试](../tests/maf-e2e/README.md)运行真实 HTTP/SignalR 合同测试，
无需调用收费模型或连接生产资源。

```bash
# negotiate 应返回 connectionId 与三种传输
curl -s -X POST "http://localhost:5298/hubs/creativeflow/negotiate?negotiateVersion=1" -b cookies.jar

# 真实 WebSocket 连接可使用任意 SignalR 客户端，确认 negotiate、握手和 Ping 往返。

# backplane 是否真的在工作（不只是编译通过）
docker compose exec -T redis redis-cli PUBSUB CHANNELS 'creativeflow*'
```
