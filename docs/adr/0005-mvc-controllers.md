# ADR 0005：HTTP 层迁移到 MVC Controllers

**状态**：已采纳

**日期**：2026-08-27

## 背景

HTTP API 原先使用 Minimal API：`Program.cs` 逐组调用 `Map*Endpoints`，每个 Endpoint 文件同时
承担路由声明、模型绑定和业务编排。随着接口增长到 15 组、约 5,800 行，路由发现、依赖边界和
资源归属越来越难以维护。

目标是采用团队更熟悉的标准 ASP.NET Core Web API 结构，并在不改变公开契约的前提下拆开路由
与编排逻辑。

## 决策

采用基于属性路由的 ASP.NET Core MVC Controllers：

- `Controllers/` 按公开资源拆分，负责 HTTP 方法、路径、模型绑定、限流和依赖注入；
- `Http/Handlers/` 保存内部编排逻辑，并通过 MVC `IActionResult` 返回响应；
- `Http/Contracts/` 保存 API 层共享响应；
- `Program.cs` 只注册 `AddControllers` 并调用 `MapControllers`；
- 所有既有 HTTP 方法、URL、端点名称、标签、限流策略和 GET/HEAD 行为保持不变；
- `ControllerRouteTests` 对完整公开路由表做快照回归。

Controller 使用构造函数注入，并将请求显式标记为 `FromBody`、`FromForm`、`FromRoute` 或
`FromQuery`。这使绑定来源不再依赖 Minimal API 的参数推断规则。

## 运行时选择

.NET 10 的 MVC Controllers 不支持 Native AOT。因此 `CreativeFlow.Api` 明确设置：

```xml
<PublishAot>false</PublishAot>
<IsAotCompatible>false</IsAotCompatible>
```

API 使用 JIT 发布。`CreativeFlow.Infrastructure` 的 Dapper.AOT、源生成 JSON、显式服务注册与其他静态
约束继续保留；它们仍能提供稳定协议和编译期数据映射，并不要求最终宿主使用 Native AOT。

## JSON 配置

Controller 与 Handler 完全使用 MVC result 管线。`ApiJsonContext` 注册到 MVC `JsonOptions`，
SignalR 的 resolver chain 继续使用同一上下文，从而保持 HTTP 与实时协议一致。

## 代价

- 放弃 Native AOT 的启动速度、常驻内存和小型部署产物优势；
- Controller 与 Handler 之间增加一层轻量转发；
- MVC 自动模型验证会为无效绑定返回标准 400 响应；
- JIT 自包含单文件产物会明显大于此前的 Native AOT 二进制。

## 收益

- 路由按资源组织，入口可直接从 `Controllers/` 发现；
- 使用标准 MVC filter、模型绑定、验证和依赖注入管线；
- `Program.cs` 回归组合根职责，不再维护 15 组手工映射；
- 大型业务域可以继续拆分 Controller，而不复制底层编排逻辑；
- 完整路由快照使后续结构调整可以机械验证兼容性。

## 后续演进

`Http/Handlers` 是迁移期间保留行为的边界。新业务规则优先进入 `CreativeFlow.Application` 用例；当现有
Handler 被修改时，也应逐步把不依赖 `HttpContext`/`IActionResult` 的逻辑下沉到 Application，保持
Controller 和 Handler 只负责交付层编排。
