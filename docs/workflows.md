# 轻量工作流

CreativeFlow 的工作流与自由画板是两个独立产品边界。现有 `/api/v1/canvas`、Canvas 数据和前端
画板执行逻辑保持不变；工作流使用 `/api/v1/workflows`、独立数据表与后台执行器。

## 第一版节点

| 节点类型 | 输入 | 输出 | 说明 |
| --- | --- | --- | --- |
| `text` | - | `text` | 固定提示词文本 |
| `image` | - | `image` | 已上传到素材库的图片 |
| `imageGenerate` | `prompt`、`image` | `image` | 使用管理后台的 `image` 模型槽位 |
| `videoGenerate` | `prompt`、`image` | `video` | 使用管理后台的 `video` 模型槽位 |
| `imageOutput` | `image` | `image` | 预览、下载并透传图片结果，可继续连接图片或视频生成节点 |
| `videoOutput` | `video` | - | 播放和下载视频结果 |
| `output` | `image`、`video` | - | 兼容已有工作流的旧版混合结果节点，不再提供新建入口 |

服务端只接受以上白名单节点。通用编排图另外支持 `start`、`llm`、`parameterExtract`、
`intentClassify`、`transform`、`script`、`condition`、`merge`、`knowledgeRetrieve`、
`knowledgeAnswer`、`httpRequest`、`loop`、`batch` 和 `end`；端口按声明类型校验，未知节点、
不兼容连线、缺失输入都会在排队前被拒绝。普通用户不能在工作流内覆盖渠道、模型或
图片返回格式。

## API

```text
GET    /api/v1/workflows
POST   /api/v1/workflows
GET    /api/v1/workflows/{id}
PUT    /api/v1/workflows/{id}
DELETE /api/v1/workflows/{id}

POST   /api/v1/workflows/{id}/runs
POST   /api/v1/workflows/{id}/runs/{runId}/resume
GET    /api/v1/workflows/{id}/runs/{runId}/partial-rerun/preview?fromNodeId={nodeId}
POST   /api/v1/workflows/{id}/runs/{runId}/partial-rerun
POST   /api/v1/workflows/{id}/runs/{runId}/nodes/{nodeId}/retry
POST   /api/v1/workflows/{id}/runs/{runId}/nodes/{nodeId}/skip
POST   /api/v1/workflows/{id}/runs/{runId}/rollback
GET    /api/v1/workflows/{id}/runs/latest
GET    /api/v1/workflows/{id}/runs/{runId}
DELETE /api/v1/workflows/{id}/runs/{runId}
```

`POST runs` 保存一份不可变图快照并返回 `202 Accepted`。PostgreSQL 队列由
`WorkflowRunWorker` 领取，按拓扑顺序执行节点；每个节点的状态、进度、输出素材 URL
和错误分别写入 `workflow_node_run`。同一工作流同一时间只允许一个活动执行，避免重复
点击造成重复调用。

运行状态通过 SignalR 的 `WorkflowRunChanged` 消息推送。HTTP 运行查询是刷新页面、
断线重连以及低频兜底使用的持久化真相来源。

## 运行和恢复

- 浏览器关闭不会停止已经排队或正在执行的工作流。
- 视频节点复用耐久的 `video_job` 与后台供应商轮询器。
- 用户可以取消排队或执行中的运行；未执行节点会变为 `cancelled`。
- API 进程重启时，无法安全续跑的旧 `running` 任务会明确标记为失败并释放活动运行约束，
  防止项目永久卡死或重复生成。
- 所有媒体结果仍进入统一素材存储，工作流图和运行记录只保存素材 URL/ID，不保存 Base64。
- 成功运行可以选择一个节点进行部分重跑。新运行使用该成功记录的不可变图快照，复用不受
  影响节点的持久化输出，只重新执行所选节点及所有依赖它的下游节点；无关分支不会重跑。
- 失败运行也可以从指定失败节点重试，或在确认风险后跳过失败/排队节点再续跑。重试是新运行，
  上游成功输出会复用；节点可通过 `retry.maxAttempts`、退避和错误码白名单配置最多五次自动重试。
  远端可能已受理的模型或 HTTP 请求不会自动重发。
- `loop` 按项执行子流程，`batch` 按批执行子流程；两者限制项数、并发度、批大小和失败策略，
  结果按输入顺序交付并保存失败项。子流程不能包含知识库或 HTTP 节点，避免隐藏外部副作用。
- `rollback` 创建从检查点衍生的新运行，不撤销已发出的外部副作用；需要撤销时必须在工作流中配置
  明确的补偿 HTTP 操作。

`script` 节点仅在 OpenSandbox 开启后出现，支持受限 Python/JavaScript JSON 转换：脚本从标准输入读取
绑定后的 JSON 对象，并且只能向标准输出写入一个符合 `outputFields` 的 JSON 对象。源码最多 32 KiB、
超时最多 30 秒、输入最多 256 KiB；沙箱默认拒绝网络，脚本不会获得模型密钥、凭据、知识正文
或工作流服务。沙箱未启用时能力接口不会公布该节点，发布也会拒绝包含脚本节点的版本。并行执行只在
loop/batch 的有界子流程内进行，运行版本发布和积分预扣继续复用当前持久化运行模型。
