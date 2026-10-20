# 模型、画布与媒体 API

本文是接口分组和使用顺序说明；精确请求字段、校验规则和完整路由请查看运行中实例的
`/swagger/index.html` 或 [Controllers](../src/CreativeFlow.Api/Controllers/)。
账号、管理员、API Key 的区别见[认证与权限](authentication.md)。

## 模型配置：管理员维护，用户读取目录

渠道和模型是**全局管理资源**，不是每个设备单独保存的配置。
普通用户可以读取允许使用的目录，但不能写渠道或获取密钥明文。

| 路由 | 用途 |
| --- | --- |
| `GET /api/v1/ai/providers` | 已登录用户读取渠道/模型目录 |
| `GET /api/v1/ai/model-slots` | 读取模型用途配置 |
| `GET/POST /api/v1/admin/ai/providers` | 管理员查询/新增渠道 |
| `PATCH/DELETE /api/v1/admin/ai/providers/{id}` | 修改/删除渠道 |
| `POST /api/v1/admin/ai/providers/{id}/check` | 上游连通性检查 |
| `GET /api/v1/admin/ai/providers/{id}/upstream-models` | 尝试读取上游模型目录 |
| `POST /api/v1/admin/ai/providers/{id}/models` | 手动添加模型 |
| `POST /api/v1/admin/ai/providers/{id}/models/import` | 批量添加模型 |
| `PATCH/DELETE /api/v1/admin/ai/models/{modelId}` | 修改/删除模型 |
| `GET /api/v1/admin/ai/model-slots` | 读取各用途的可选模型 |
| `PUT /api/v1/admin/ai/model-slots/{kind}` | 保存用途模型配置 |
| `POST /api/v1/admin/ai/config/import` | 管理员批量导入配置，DTO 见 Swagger |

上游没有模型列表接口不代表不能调用；可手动填写上游模型 ID。
模型的本地 UUID、上游 `modelKey`、展示名称是三个不同概念。
媒体工作流从管理员为相应用途启用的模型中选择，只有一个选项时默认选中。
通用编排 LLM 使用其支持协议和能力的模型目录，不等同于媒体用途槽位。

### 协议与端点

协议决定请求格式，不等同于厂商品牌。当前协议定义见
[`ProviderType`](../src/CreativeFlow.Domain/Ai/ProviderType.cs)，解析见
[`AiProvider.ResolveProtocol`](../src/CreativeFlow.Domain/Ai/AiProvider.cs)：

1. 模型显式协议覆盖优先。
2. 图片/视频模型可继承与类别兼容的渠道协议。
3. 渠道协议不兼容时，图片默认 `openai-images`，视频默认 `seedance-video`。
4. 重排序类别默认 `rerank`；其他类别按渠道协议解析。

不要假设只改模型名称就能把 OpenAI 视频接口变成 MiniMax 原生接口。
Base URL 不能带用户信息、查询参数或 fragment；服务端按协议去掉会重复追加的后缀。
模型能保存也不等于网络已放行，另需配置[对应端点许可](configuration.md#endpoint-allowlists)。
`supportsVision` 等能力必须与真实模型一致。

### 密钥

服务端使用 `Ai:EncryptionKey` 加密渠道密钥，普通目录只返回是否已设置和掩码。
更新接口中，密钥字段省略表示保留，空串表示清除，非空值表示替换；
批量导入接口不支持用空串清除。密钥不能写入工作流图、前端环境变量或分享链接。
当前前端没有接通 URL 自动导入页面流程；残留的解析工具不代表可用的产品入口。

## 画布与资产

用户先登录并完成设备握手，再调用 `/api/v1/canvas` 列表/创建接口及
`/api/v1/canvas/{id}` 读取、保存、删除接口。
画布图以 JSON 保存，服务端持久化与前端本地缓存各有职责，不能把临时 `blob:` URL
当成可跨会话恢复的素材地址。

上传图片使用 `POST /api/v1/canvas/assets`（multipart），素材库与分组在
`/api/v1/canvas/assets` 下。素材选择复用已有 ID，不重复上传。
读取/下载受账号和素材状态校验，图片需通过 MIME 与文件头校验，不能上传可执行 SVG。
删除走回收站、引用检查和保留期，不是“先删数据库行再删文件”。
完整生命周期见[资产中心](asset-center.md)。

## 生成与恢复

| 路由 | 运行方式 |
| --- | --- |
| `POST /api/v1/ai/image-jobs` | 持久化异步图片任务；画布应使用这条路径 |
| `GET /api/v1/ai/image-jobs/{jobId}` | 查询既有任务，不重复生成 |
| `POST /api/v1/ai/images` | 另有同步生成接口；不要与异步任务混淆 |
| `POST /api/v1/ai/text` | 文本生成 |
| `POST /api/v1/ai/videos` | 提交视频任务，返回 `jobId` |
| `GET /api/v1/ai/videos/{id}` | 查询视频状态 |
| `DELETE /api/v1/ai/videos/{id}` | 请求取消视频任务 |

工作流、Agent 与画布图片任务有不同提交入口，共享模型和素材组件。
“重新查询”“重新下载/保存”“重新生成”成本不同，详见[图片任务](image-generation-jobs.md)。
任意外部 URL 不应作为素材权限的替代；参考图使用当前范围内的素材。

## Agent、MCP 与 Skills

Agent 的会话、消息和运行位于 `/api/v1/agent/`；Skill 上传扫描、安装与管理位于
`/api/v1/skills/`，具体绑定与审批字段见对应 Controller/Swagger。
Skill 包扫描不等于执行沙箱。

脚本执行使用 **OpenSandbox**，不是 API 内执行脚本，也不是旧的 Unix socket runner。
通过 `Skills:SandboxEnabled` 开启，控制平面地址、密钥和执行镜像由部署配置提供。
Agent Skill 脚本经过审批；通用编排 `script` 节点按已发布的节点契约执行，
不要把二者的审批交互混用。未配置沙箱时不应宣称脚本功能可用。

MCP 远程工具也有自己的审批和网络校验，不会因为保存了工具配置而自动执行。
画布工具的浏览器执行器绑定、工具回执和审批使用[SignalR](realtime-signalr.md)。
