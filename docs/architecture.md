# 后端架构与开发边界

本文描述当前实现，不记录历史迁移过程。部署方式见[部署说明](deployment.md)，
数据存储见[数据模型](data-model.md)。

## 项目分层

| 项目 | 职责 | 不应承担 |
| --- | --- | --- |
| `CreativeFlow.Domain` | 业务实体、标识、枚举、不变量 | I/O、框架服务和 NuGet 依赖 |
| `CreativeFlow.Application` | 用例、端口、DTO、工作流规则与节点执行器 | HTTP 响应、SQL、具体模型 SDK |
| `CreativeFlow.Infrastructure` | PostgreSQL、缓存、消息、文件、模型协议和 MAF 适配 | 依赖 API 的 Controller/Hub |
| `CreativeFlow.Api` | 组合根、Controller、HTTP Handler、中间件、SignalR | 直接执行 Dapper 查询 |

内层不引用外层。API 装配实现；业务服务使用构造函数注入。
后台 Worker 创建作用域，执行上下文只传运行标识、输入和快照，不充当服务容器。

## 三条运行路径

- **自由画布与媒体任务**：用户保存画布、上传或选择素材，图片任务和视频任务各自持久化状态；
  不等同于提交一个工作流。
- **工作流**：媒体创作与通用编排保持独立图模型；共享发布、队列、运行记录、权限和素材边界。
  入队前校验图与输入，运行固定快照，节点输出持久化后才推进后继节点。
- **Agent**：推进模型轮次、执行工具与审批、记录消息及事件。浏览器画布工具通过 SignalR
  回传结果；与后台工作流执行不是同一个交互协议。

Agent 与工作流只注册 MAF 执行引擎。MAF 适配位于 Infrastructure，原业务组件仍负责
授权、图校验、工具审批、队列、模型协议、事件顺序和文件保存。
AG-UI 是已有工作流运行的传输适配，不是第二套执行器，也不自动提供多轮会话记忆。

## HTTP 与认证

`Controllers/` 声明方法、路径、绑定和依赖，`Http/Handlers/` 负责交付层编排与响应映射。
与 HTTP 无关的规则放入 Application。精确路由和 DTO 以 Controller、Swagger 为准，
不在架构文档中维护易过期的路由数量。

普通用户使用账号 session 与已绑定设备，管理员使用独立 admin session，
外部工作流使用 API Key。设备概率关联不授予账号权限，详见[认证与权限](authentication.md)。

## 当前发布模式

API 项目显式设置 `PublishAot=false`、`IsAotCompatible=false`，采用 **MVC/JIT**。
不要按旧的 Native AOT 验收说明发布 API。

类库仍保留静态约束：

- Dapper 调用集中在 Infrastructure，同一编译单元的 Dapper.AOT interceptor 才能处理这些调用。
- `Directory.Build.props` 将 `DAP001;DAP013;DAP015;DAP016;DAP017` 设为构建错误。
- API 不直接引用 Dapper；不要在 Controller/Handler 中增加数据库查询。
- 关键服务、验证器显式注册；启动启用作用域和构造依赖验证。
- 依赖版本由 `Directory.Packages.props` 和锁文件控制，SDK 由 `global.json` 固定。

标准发布：

```bash
dotnet restore CreativeFlow.slnx --locked-mode
dotnet publish src/CreativeFlow.Api -c Release
```

容器与自包含发布使用仓库 [Dockerfile](../src/CreativeFlow.Api/Dockerfile)。
无需另行启用 `PublishAot`。

## 序列化与实时事件

- MVC 与 SignalR 的 resolver chain 都插入 `ApiJsonContext`；新增过线 DTO 同步登记。
- Redis/RabbitMQ 使用 `IntegrationJsonContext`，不依赖 API 程序集。
- 缓存、消息端口显式传递 `JsonTypeInfo<T>`，不在调用方隐藏序列化策略。
- SignalR 使用普通 Hub 和显式方法名，通过 `IRealtimeNotifier` 发送。
  `SignalRNotifier` 位于 API，因为它依赖具体 Hub；这是交付层适配，不是基础设施反向依赖。
- SignalR 通知不是持久化事件日志；HTTP 查询、工作流 SSE 和 AG-UI 的重连语义见各自文档。

## 代码入口

| 目录 | 内容 |
| --- | --- |
| `src/CreativeFlow.Application/Features/Workflow/Execution/` | 通用编排节点执行器与注册表 |
| `src/CreativeFlow.Application/Features/Agent/` | Agent 用例和轮次规则 |
| `src/CreativeFlow.Infrastructure/DependencyInjection/` | 持久化、AI、知识库和工作流注册 |
| `src/CreativeFlow.Infrastructure/Persistence/` | 数据库仓储及迁移运行器 |
| `src/CreativeFlow.Api/Controllers/` | 可调用 HTTP 接口 |
| `src/CreativeFlow.Api/Hubs/` | SignalR 协议和组定义 |
| `db/migrations/` | 不可原地改写的发布 SQL |

开发检查见[代码规范](coding-standards.md)与[本地测试](local-development.md#tests)。
