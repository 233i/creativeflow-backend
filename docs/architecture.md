# 架构

## 分层

```
CreativeFlow.Domain          零外部依赖（无 NuGet、无 ProjectReference）
   ▲
CreativeFlow.Application     接口（端口）+ 用例 + 契约
   ▲
CreativeFlow.Infrastructure  Dapper/Npgsql/Redis/RabbitMQ 实现
   ▲
CreativeFlow.Api             组合根 · MVC Controllers · HTTP Handlers · SignalR · 中间件
```

依赖箭头**只能向内**。`CreativeFlow.Api` 是唯一允许引用全部层的项目。

## 各层职责

### CreativeFlow.Domain
身份类型与不变量。`DeviceId`/`VisitorId`（UUIDv7 包装）、`Device`/`DeviceLink` 实体、`LinkMethod` 枚举、`LinkConfidence` 值对象（强制概率性关联的 0.6 上限）。

不含任何 I/O、任何框架类型。想在这里加包引用时，说明该类型其实属于 Application。

### CreativeFlow.Application
定义**端口**（`IDeviceRepository`、`ICacheStore`、`IEventPublisher`、`IRealtimeNotifier`…）与**用例**（`ResolveDeviceIdentity`、`GenerateImagesUseCase`、`GenerateTextUseCase` 等）。账号端口还定义 provider-neutral 的 OAuth identity、session 和 account-device bridge。用例及验证器由该层的 `AddApplication` 显式注册。

关键设计：缓存与消息端口签名里带 `JsonTypeInfo<T>`，使序列化契约成为调用方必须显式提供的依赖，避免不同交付通道产生反射回退或命名差异。

### CreativeFlow.Infrastructure
实现端口。**所有 Dapper 调用点都必须在这里**——原因见下。

### CreativeFlow.Api
组合根与交付层。`Controllers/` 声明路由和模型绑定，`Http/Handlers/` 编排现有 HTTP 用例，另含
SignalR Hub、中间件与 `ApiJsonContext`。新业务规则应优先下沉到 Application 用例。

## 关键位置决策

### 1. 为什么所有 Dapper 调用必须集中在 Infrastructure

Dapper.AOT 通过 **C# interceptor** 工作。interceptor 是编译期产物，**只在包含调用点的编译单元内生效，不跨 `ProjectReference` 传递**。

后果：调用点若离开 Infrastructure，就不会获得该项目生成的静态映射，并会绕过 DAP 构建诊断。

防护：`CreativeFlow.Api.csproj` **完全不引用 Dapper**，因此数据访问泄漏会直接编译失败。这机械性地强制了
Clean Architecture 的“数据访问只在一层”。

### 2. 为什么 `CreativeFlowHub` 在 CreativeFlow.Api 而不是 Infrastructure

Hub 是**交付机制**，等同 Controller，属最外层。另外 JSON hub protocol 的 resolver 需要覆盖所有过线类型，Hub 与其 `JsonSerializerContext` 同处一个程序集，源生成器才能获得完整视图。

`IRealtimeNotifier` 接口定义在 Application，但实现 `SignalRNotifier` **也在 CreativeFlow.Api**——因为它需要 `CreativeFlowHub` 类型，放 Infrastructure 会把交付层类型拖进内层。组合根实现 Application 接口是正统 Clean Architecture 做法。

### 3. 为什么需要两个 JsonSerializerContext

| Context | 位置 | 覆盖 |
|---|---|---|
| `ApiJsonContext` | `CreativeFlow.Api/Serialization/` | MVC、SignalR 过线 DTO |
| `IntegrationJsonContext` | `CreativeFlow.Infrastructure/Serialization/` | RabbitMQ 事件、Redis 缓存信封 |

不能合一：Infrastructure 不允许引用 Api。共享的过线 DTO 放 `CreativeFlow.Application/Contracts/`，两个生成器都能看到。`TypeInfoResolverChain` 支持多个 resolver，组合无冲突。

`ApiJsonContext` 同时注册到 MVC `JsonOptions` 和 SignalR resolver chain，保证 HTTP 与实时通道的
命名和枚举契约一致。

### 4. 为什么 Controller 与 Handler 分开

Controller 只表达 HTTP 语义：方法、路径、绑定来源、限流与构造函数依赖。迁移前 Endpoint 中的
编排逻辑保留为返回 `IActionResult` 的内部 Handler，使公开契约迁移与业务重写解耦。
`ControllerRouteTests` 锁定 116 项路由签名，其中一项同时支持 GET/HEAD，共 117 个方法/路径组合。
后续修改某个功能时，再把与 `HttpContext`/`IActionResult` 无关的规则逐步下沉到
Application，而不让 Controller 重新膨胀。

图片生成已完成首个迁移：`AiGenerationController → AiGenerationHandlers.GenerateImagesAsync → GenerateImagesUseCase`。
Handler 验证交付层身份并映射原有 HTTP 结果，用例负责模型、参考图、蒙版、生成策略及素材保存顺序。
文本生成沿同一边界迁移为 `AiGenerationController → AiGenerationHandlers.GenerateTextAsync → GenerateTextUseCase`。
用例拥有渠道/聊天模型校验、密钥解密、参考素材读取、参数缺省及文本分派；`GenerateTextResult`
表达业务结果，HTTP Handler 保留身份检查、MVC 401/404 与原有错误体映射。两个用例均为 scoped，
Controller 只注入两个用例。素材管理仍沿原有 Handler 路径执行，后续按切片迁移。

### 5. 账号作用域为何不把所有业务表改成 account_id

业务表继续保留 `device_id`，用于浏览器级隔离并兼容现有仓储接口。登录后，API 在验证 session 与当前
设备关联关系后，将 account id 写入本次数据库连接的 `creativeflow.account_id` session setting。
`creativeflow_accessible_device_ids(device_id)` 再把当前设备扩展为账号明确关联的设备集合；没有有效账号
session 时该函数仍只返回当前设备，作为数据库层纵深防御；HTTP 与 SignalR 业务入口会更早返回
401。这使账号访问具备跨设备能力，同时避免把账号权限误授给只持有旧设备 Cookie 的请求。

## 请求生命周期（设备握手）

```
POST /api/v1/device/handshake
  │
  ├─ Serilog 请求日志
  ├─ DeviceContextMiddleware   读 Cookie → HttpContext.Items + 日志 scope（只读，不创建身份）
  ├─ AccountContextMiddleware  校验不透明 session 与设备归属
  ├─ DeviceController.Handshake         属性路由 + FromBody + 构造函数注入
  │    └─ DeviceHandlers.HandshakeAsync
  │         ├─ 无有效账号 session → 401
  │         ├─ IValidator<DeviceSignalsPayload>   显式注册的 FluentValidation
  │         └─ ResolveDeviceIdentity.HandleAsync  ← 核心用例（Application 层）
  │              ├─ IDeviceRepository        Dapper.AOT（Infrastructure）
  │              ├─ IFingerprintHasher       量化 + SHA256 + pepper
  │              └─ IVisitorRepository       device_link 边写入
  └─ DeviceCookie.Write        每次握手都补发，让确定性层尽快接管
```

`DeviceContextMiddleware` **只读不写**：它从不创建身份。身份只由握手端点铸造，因此健康探针、爬虫等请求不会静默产生设备行。

## 构建期防护

### MAF 运行时边界

Agent 与工作流通过框架无关的运行端口固定使用 MAF，组合根各注册唯一实现。MAF 的 ChatClientAgent 和
Executor 链仅在 Infrastructure 内部推进原业务轮次/节点；协议构造、审批、工具回执、
图校验、素材保存、后台队列和取消/重启策略仍是原系统的唯一实现。
Legacy 循环、选择器、灰度配置和绑定读写已删除；0024 迁移及旧表仅保留原结构，不参与运行。
工作流继续/局部重跑仍使用原图快照、节点输出和视频任务检查点，不增加自动重生。
缺失运行端口注册直接失败，没有另一套引擎兜底。生产 API/Infrastructure 锁定 MAF 及完整传递依赖。
运行观察和发布边界见[运维说明](operations.md)。MAF 是 Agent 与工作流的唯一
执行引擎；原业务持久化、权限、队列和媒体组件继续由应用层拥有。

`scripts/verify-governance.sh` 汇总当前可离线于业务服务执行的构建、注释、回归测试和发布检查。
项目引用与 Application 程序集依赖、Agent 宿主实例一致性、原有迁移字节均有测试约束。
`tools/CreativeFlow.CodeQuality` 检查全部手写方法与字段说明，规则见[注释规范](coding-standards.md)。

`Directory.Build.props` 中两组设置承担主要防护职责：

```xml
<IsAotCompatible>true</IsAotCompatible>
<WarningsAsErrors>$(WarningsAsErrors);DAP001;DAP013;DAP015;DAP016;DAP017</WarningsAsErrors>
```

- `IsAotCompatible` 在内层**类库**上开启 trim/AOT 分析器；MVC 宿主 `CreativeFlow.Api` 显式覆盖为 `false`
- `DAP001` 等设为 error，把 Dapper.AOT 的静默回退变成编译失败

包版本集中在 `Directory.Packages.props`（含一处安全提升：`Microsoft.OpenApi` → 2.7.5）。SDK 由 `global.json` 锁定 `rollForward: disable`。

## 目录

```
src/CreativeFlow.Domain/Identity/          DeviceId, VisitorId, Device, DeviceLink, LinkConfidence, LinkMethod
src/CreativeFlow.Application/
  DependencyInjection.cs         AddApplication：用例及验证器显式注册
  Abstractions/{Persistence,Caching,Messaging,Realtime,Security,Common}/   端口
  Contracts/{Device,Realtime}/    过线 DTO（两个 JSON context 共享）
  Features/Identity/              ResolveDeviceIdentity + AccountAuthService + Options + Validator
  Features/Generation/            GenerateImagesUseCase + 业务结果
  Events/                         集成事件
src/CreativeFlow.Infrastructure/
  DependencyInjection/           持久化、缓存、消息、身份与 AI 注册组；公共入口保持不变
  Persistence/                    DapperModule, NpgsqlDataSourceFactory, 仓储, Migrations/
  Caching/                        RedisCacheStore, RedisPresenceTracker, RedisDistributedLock
  Messaging/                      连接提供者, 拓扑, 发布者, 消费者, 处理器注册表
  Security/                       Sha256FingerprintHasher, IpPrefixExtractor
  Health/ Options/ Common/
src/CreativeFlow.Api/
  Program.cs                      MVC/SignalR 组合根
  Controllers/                   属性路由、模型绑定、限流、构造函数注入
  Http/Handlers/                 HTTP 编排与响应映射（内部类型）
  Http/Contracts/                API 层共享响应
  Hubs/ Realtime/ Middleware/ Extensions/ Serialization/
db/migrations/                    版本化 SQL（嵌入资源）
```
