# CreativeFlow 后端

CreativeFlow 是基于 .NET 10 的 AI 画布、媒体生成、工作流编排、知识库和 Agent 后端，
提供对外工作流 API 和 AG-UI 兼容的 SSE 流式事件。

[English](README.md) · [完整文档目录](docs/README.md)

## 包含什么

- 媒体创作与通用编排两种工作流，支持节点状态、运行记录、发布和权限管理。
- 渠道与模型配置、图片/视频生成和素材库。
- 知识库导入、语义切片、混合检索、重排序和引用。
- HTTP 工具、结构化输出、参数提取和沙箱脚本。
- PostgreSQL 元数据、独立文件存储、Redis、RabbitMQ 和 SignalR。

**本仓库只有后端，不包含管理后台和画布网页。** 启动成功后可以访问 Swagger 与 API；
完整界面需要另外部署前端。模型、向量服务、Qdrant 和 OpenSandbox 需要自行配置。

要运行完整网页应用，请先完成下面的后端步骤，再按
[前端联调与验收](docs/local-development.md#frontend-and-end-to-end-startup)启动前端。
仅启动后端时，访问根路径 `/` 没有管理页面是正常的。

## 快速启动：本地独立环境

准备 Git，以及带 Compose v2 插件的 Docker。macOS/Windows 可使用 Docker Desktop，
Linux 使用 Docker Engine。以下命令在克隆后的仓库根目录执行。

Linux 主机已有 Podman 时，可使用[明确指定架构的 Podman 启动步骤](docs/local-development.md#podman-alternative-on-linux)。

### 1. 配置密钥

~~~bash
cp .env.example .env
openssl rand -hex 32
openssl rand -base64 32
~~~

先编辑被 Git 忽略的 `.env`：

| 变量 | 要填写的内容 |
| --- | --- |
| `POSTGRES_PASSWORD` | 新生成的数据库密码 |
| `RABBITMQ_PASSWORD` | 另一个随机密码 |
| `ADMIN_PASSWORD` | 足够强的初始管理员密码 |
| `DEVICE_PEPPER` | 独立随机值，例如 `openssl rand -hex 32` 的输出 |
| `AI_ENCRYPTION_KEY` | `openssl rand -base64 32` 的输出，解码后必须恰好 32 字节，**不能用 hex** |

不同配置使用不同随机值，不复用正式环境密钥。加密密钥需要长期保管，重启时不能重新生成。
暂时保持 OAuth、沙箱等可选功能关闭。

### 2. 启动并验证

~~~bash
docker compose up -d --build
docker compose ps
curl --retry 20 --retry-delay 3 --retry-all-errors --max-time 10 \
  -fsS http://localhost:5298/health/ready
curl -fsS http://localhost:5298/health/live
~~~

首次构建可能需要数分钟。浏览器打开
[Swagger](http://localhost:5298/swagger/index.html)。
ready 检查应显示 PostgreSQL、Redis、RabbitMQ 和文件存储均正常。

默认只启动 API、PostgreSQL 18、Redis 8、RabbitMQ 4 和一次性的文件卷初始化服务，
自动为这个独立数据库执行迁移。端口只绑定 **127.0.0.1**，这不是正式环境部署清单。
默认端口被占用时，通过 `.env` 中的可选端口变量调整；并行部署还需使用独立项目名，
详见[独立栈配置](docs/local-development.md#isolated-compose-stack)。

### 3. 登录并配置模型

另外部署前端后，使用 `ADMIN_USERNAME`（默认 `admin`）和设置的 `ADMIN_PASSWORD`
登录管理后台；也可先在 Swagger 调用 `POST /api/v1/admin/auth/login`。
按提示修改初始密码。已有管理员账号不会因修改引导配置而被重置。

在管理后台配置服务商地址、API Key 和模型，仓库不附带可用模型密钥。
工作流、向量化和 HTTP 节点还需要配置各自的出站白名单，详见
[配置说明](docs/configuration.md)和[认证说明](docs/authentication.md)。

配置收费模型之前，可在前端工作流中心导入
[启动验收回显工作流](docs/examples/startup-echo.workflow.json)。它直接返回输入文本，
不调用模型、知识库或外部 HTTP 接口；预期结果和重启持久化检查见前端联调说明。

### 停止、重启与数据保留

~~~bash
docker compose down
docker compose up -d
~~~

数据库和素材保存在命名卷。**除非确认要清空本地实例，不要在 `down` 后加 `-v`。**
已有数据卷初始化后，修改 `.env` 中的数据库密码不会自动修改数据库内部的账号密码。

## 从源码运行

安装 [global.json](global.json) 指定的 SDK，并参考[本地开发说明](docs/local-development.md)：

~~~bash
docker compose up -d postgres redis rabbitmq
cp deploy/development/appsettings.Development.local.example.json \
  src/CreativeFlow.Api/appsettings.Development.local.json
# 先填写复制出的配置；dotnet 不会自动读取根目录的 .env。
dotnet restore CreativeFlow.slnx --locked-mode
dotnet build CreativeFlow.slnx
dotnet run --project src/CreativeFlow.Api -- --migrate-only
dotnet run --project src/CreativeFlow.Api
~~~

本地配置中的数据库和 RabbitMQ 密码应与 Compose 的 `.env` 一致。
不要将这些迁移命令指向共享库或正式库。

## 可选能力

- **知识库：** 基础 Compose 默认关闭。当前 Qdrant 适配器只接受 API 所在网络空间的
  `http://127.0.0.1:<端口>`，正式环境固定 6333；普通 Docker 服务名、
  远程 Qdrant 地址不能通过校验。见[知识库配置](docs/knowledge.md)。
- **OpenSandbox：** 默认关闭，通过 `sandbox` profile 单独启动；需要私有本地配置，
  并拥有 Docker daemon 访问权限。见[沙箱说明](docs/local-development.md#optional-opensandbox)。
- **前端：** 单独部署，将网页与 `/api`、`/hubs`、SSE 接口放在同源反向代理后面。
  Swagger 不等于管理后台。

## 正式部署

按[部署文档](docs/deployment.md)构建固定版本镜像，使用
[正式环境模板](deploy/production/backend.env.example)注入配置，
准备持久化文件卷，单独执行一次 `--migrate-only`，再通过 HTTPS 反向代理启动 API。
文档提供 Linux Docker 命令和健康检查步骤。

数据库和沙箱不要暴露公网。数据库、素材卷、Skill 卷必须一起备份。
数据库迁移文件及校验值必须完整保留，不能用空库初始化脚本覆盖已有业务库。

## 开发与接口参考

- [架构](docs/architecture.md)与[数据模型](docs/data-model.md)
- [画布 API](docs/ai-canvas-api.md)与[资产中心](docs/asset-center.md)
- [工作流](docs/workflows.md)、[AG-UI API](docs/ag-ui-workflow-api.md)与[流式协议](docs/workflow-api-streaming.md)
- [HTTP 节点和凭据](docs/workflow-http-request-node.md)
- [运维与恢复](docs/operations.md)

## 验证状态

这是持续开发中的源码版本，不表示所有质量门禁已经通过。已实测独立环境启动、
空库迁移、登录、无模型工作流、素材持久化及前端联调。
目前仍有迁移 0045 对应的数据库测试隔离问题，以及 MessagePack 依赖安全告警待处理。
不要关闭测试或审计来隐藏这些问题，详见[本地验证说明](docs/local-development.md)。
模型生成、知识库和沙箱服务需要另外配置并在自己的环境验收。

## 安全与许可证

不要提交真实密钥、密码、私有配置、上传文件或构建产物；测试夹具中的凭据仅为合成数据。
当前 API 在正式环境也开放 Swagger，需要时在反向代理限制访问。
公开服务前检查出站白名单和资源授权边界。

CreativeFlow 基于 [AIDotNet/Sol](https://github.com/AIDotNet/Sol) 二次开发。
上游代码采用 MIT 许可证，原版权声明 `Copyright (c) 2026 AIDotNet`
及完整许可文本保留于 [LICENSE](LICENSE)。此处说明代码来源，不代表
AIDotNet 负责维护或认可 CreativeFlow。
