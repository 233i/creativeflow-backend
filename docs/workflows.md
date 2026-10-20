# 工作流：媒体创作与通用编排

两种工作流在工作流中心统一管理，但图模型、参数编辑和节点能力保持分离。
自由画布 `/api/v1/canvas` 是另一个编辑入口，不等同于工作流定义。

## 节点能力

| 类型 | 节点 | 用途 |
| --- | --- | --- |
| 媒体创作 | `text`、`image` | 提示词和素材输入 |
| 媒体创作 | `imageGenerate`、`videoGenerate`、`videoExtend`、`videoFrame` | 图片生成、视频生成/延长/抽帧 |
| 媒体创作 | `imageOutput`、`videoOutput` | 展示结果；旧图中的 `output` 由验证器继续识别 |
| 通用编排 | `start`、`end` | 声明输入、公开输出契约 |
| 通用编排 | `llm`、`parameterExtract`、`intentClassify` | 模型调用、结构化提取、意图路由 |
| 通用编排 | `transform`、`script` | 有界变量/表达式转换、沙箱脚本 |
| 通用编排 | `condition`、`merge`、`loop`、`batch` | 条件、结果汇合、循环、批处理 |
| 通用编排 | `knowledgeRetrieve`、`knowledgeAnswer` | 检索、带引用约束的知识回答 |
| 通用编排 | `httpRequest` | 调用外部接口，使用独立凭据授权 |

实际可用节点还受环境能力开关、权限、协议和模型能力限制。
读取 `/api/v1/workflows/capabilities` 或管理端对应能力接口，不要硬编码“所有节点可用”。
通用编排持久化 `schemaVersion` 为 **1**；源码中的 `V2` 命名不是第二套外部 API 版本。

模型选择规则见[模型配置](ai-canvas-api.md)。开始节点的 `image` 字段在运行时通过上传或素材库选择，
外部调用以 `{"assetId":"..."}` 传参；后端读取授权素材并构造模型图片内容段，
同时校验 `supportsVision`。不能把任意文件路径或公网 URL 当作已授权素材。

## 定义、运行与发布

| 入口 | 职责 |
| --- | --- |
| `/api/v1/workflows` | 用户工作流定义、运行与恢复 |
| `/api/v1/admin/workflow-apis` | 管理工作流定义、调试与运行 |
| `/api/v1/admin/workflow-center` | 管理后台聚合列表/详情与生命周期操作 |
| `/api/external/v1` | API Key 调用已发布版本 |
| `/api/agui/v1` | 同一外部工作流的 AG-UI 传输 |

精确方法与子路径见 [Controllers](../src/CreativeFlow.Api/Controllers/) 或 Swagger。
管理后台发布的是固定版本；随后修改草稿不会改变正在执行的快照或已发布输入输出。
Key 的工作流访问权不自动包含知识库、凭据和素材授权，需分别配置。

运行请求通过校验后保存快照并入队，返回 `202`，后台 Worker 通过唯一 MAF 引擎推进。
排队、节点状态、输出与错误保存到数据库；关闭页面或断开 SSE 不等于取消运行。
网页通知用 SignalR，刷新或断线后以 HTTP 查询恢复。
外部运行另有队列/并发/幂等约束，不要把交互运行的重复提交限制套用为所有外部客户只能串行。

## 调试与恢复

- 单节点调试使用该节点及已绑定输入，仍需校验资源权限；可能产生模型或外部调用费用。
- `resume` 用于既有恢复语义，不代表“暂停等待用户输入”的多轮会话。
- `partial-rerun/preview` 先显示影响范围；局部重跑根据原运行快照复用允许复用的输出。
- 节点重试、跳过和检查点 rollback 均有状态/输入/引用约束，不保证每个失败节点都可跳过。
- 自动重试可配置错误码、次数与退避；上游结果不明确时不应再次发出可能已计费的调用。
- rollback 是创建检查点派生运行，**不会撤销外部系统已发生的副作用**。
- 重启后对无法安全恢复的运行明确收尾，不能以重启为理由自动重新生成。

`loop`/`batch` 的子流程有项数、并发和失败策略约束；不支持把知识库或 HTTP 节点隐藏在子流程中。
`script` 使用 OpenSandbox，支持受限 Python/JavaScript：标准输入为 JSON，标准输出为
符合声明字段的 JSON；没有应用数据库、模型密钥或任意网络访问。
仅配置一个节点不等于 OpenSandbox 已部署，见[沙箱启用](local-development.md#optional-opensandbox)。

## 外部客户端如何选择

| 需要 | 接口方式 |
| --- | --- |
| 提交任务后轮询最终结果 | REST：POST 返回运行 ID，再 GET 状态 |
| 现有自定义事件客户端 | REST 提交 + `eventsUrl` SSE 订阅 |
| 展示每个节点的执行事件、使用 AG-UI 客户端 | AG-UI POST 流，断线后 GET 精确续读 |

两类流的游标不通用。自定义 SSE 的进度是状态快照，可能错过快速节点；
AG-UI 的 step/state 来自持久化事务日志，包含节点 ID、名称和类型。
`threadId` 只用于关联，不会自动加载之前对话。

详见 [REST/SSE](workflow-api-streaming.md)、[AG-UI](ag-ui-workflow-api.md)、
[HTTP 节点与凭据](workflow-http-request-node.md)和[可导入示例](examples/README.md)。

## 删除与保留

删除前检查已发布 API、模板引用及排队/运行任务；已发布模板先下架。
默认保留生成素材与历史审计，不要把删除工作流理解为清空其所有结果文件。
素材的回收和永久清理由[资产中心](asset-center.md)单独处理。
