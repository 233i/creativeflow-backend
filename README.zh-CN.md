# CreativeFlow 后端

基于 .NET 10 的 AI 画布、媒体创作、通用编排、知识库、资产和 Agent 后端。

[English](README.md) · [文档导航](docs/README.md) ·
[前端仓库](https://github.com/233i/creativeflow-frontend)

## 包含的能力

- 媒体创作和通用编排工作流，持久化运行与节点状态。
- 渠道/模型管理、图片/视频生成、素材库。
- 知识导入、语义切片、向量/关键词/混合检索、可选重排序。
- HTTP 节点、结构化输出、参数提取和 OpenSandbox 脚本。
- 发布工作流供外部系统调用：REST 查询、自定义 SSE、AG-UI。

**本仓库不包含网页。** 后端启动后提供 Swagger；画布和管理后台需要另外运行前端。
基础服务启动不需要模型 Key。生成、知识库和沙箱功能需单独配置服务，调用模型可能产生费用。

## 快速启动：本机独立环境

准备 Git 和带 Compose v2 的 Docker。这条路径由 Docker 编译后端，无需在宿主机安装 .NET。
在新的克隆目录中执行：

```bash
git clone https://github.com/233i/creativeflow-backend.git
cd creativeflow-backend
cp .env.example .env
openssl rand -hex 32
openssl rand -base64 32
```

编辑被 Git 忽略的 `.env`，不同字段使用不同随机值：

| 变量 | 要填写的内容 |
| --- | --- |
| `POSTGRES_PASSWORD` | 随机数据库密码 |
| `RABBITMQ_PASSWORD` | 独立随机消息队列密码 |
| `ADMIN_PASSWORD` | 足够强的初始管理员密码 |
| `DEVICE_PEPPER` | 独立随机值，例如 `openssl rand -hex 32` |
| `AI_ENCRYPTION_KEY` | `openssl rand -base64 32`，解码恰好 32 字节，不能用 hex |
| `PUBLIC_ORIGIN` | 本机前端使用 `http://localhost:3000` |

加密密钥需要长期保管，重启时不能重新生成，否则已有渠道凭据无法解密。

```bash
docker compose up -d --build
docker compose ps
curl --retry 20 --retry-delay 3 --retry-all-errors --max-time 10 \
  -fsS http://localhost:5298/health/ready
curl -fsS http://localhost:5298/health/live
```

浏览器打开 [Swagger](http://localhost:5298/swagger/index.html)。
ready 检查 PostgreSQL、Redis、RabbitMQ 和文件存储，不包含模型等可选服务。
首次构建可能需要数分钟。

默认启动 PostgreSQL 18、Redis 8、RabbitMQ 4、文件卷初始化服务及 API，
端口只绑定 **127.0.0.1**，数据库和文件使用持久卷。这是本地栈，不是正式环境部署清单。
端口占用或使用 Podman 时见[本地开发说明](docs/local-development.md#isolated-compose-stack)。

### 启动完整网页

另开终端：

```bash
git clone https://github.com/233i/creativeflow-frontend.git
cd creativeflow-frontend
# 先安装 .node-version 指定的 Node 版本。
npm ci
cp .env.example .env.development.local
npm run dev
```

打开[管理后台](http://localhost:3000/admin)，使用 `ADMIN_USERNAME`（默认 `admin`）
和前面设置的密码登录，按提示修改初始密码。
在工作流中心导入 [startup-echo.workflow.json](docs/examples/startup-echo.workflow.json)，
输入 `message = Hello CreativeFlow`，应得到 `result = Hello CreativeFlow`，不调用模型。

用户画布 `/` 需另外注册普通账号；管理员与普通用户不是同一个登录体系。
开始生成前先在后台配置渠道、模型及用途。上传、素材复用、重启持久化的完整验收步骤见
[前后端联调](docs/local-development.md#frontend-and-end-to-end-startup)。

### 停止但保留数据

```bash
docker compose down
docker compose up -d
```

除非明确清空本地实例，不要使用 `down -v`。
修改 `.env` 中的引导密码不会自动重置已有数据库、消息队列或管理员账号的密码。

## 接下来按需阅读

| 目标 | 文档 |
| --- | --- |
| 源码开发后端 | [本地开发](docs/local-development.md)，SDK 由 `global.json` 固定 |
| 部署自己的服务器 | [配置](docs/configuration.md) → [部署](docs/deployment.md) → [运维](docs/operations.md) |
| 启用知识库 | [知识库](docs/knowledge.md)：Qdrant 回环策略、向量模型和配额 |
| 启用脚本 | [OpenSandbox](docs/local-development.md#optional-opensandbox) |
| 外部系统接入 | [REST/SSE](docs/workflow-api-streaming.md) 或 [AG-UI](docs/ag-ui-workflow-api.md) |
| 维护代码 | [架构](docs/architecture.md)、[数据与迁移](docs/data-model.md)、[开发规范](docs/coding-standards.md) |

其他功能说明见[文档导航](docs/README.md)。
**迁移 0001–0045 和校验文件必须原样保留**，重建 Git 历史不等于重建数据库。
升级仍在数据库保存媒体的旧实例前，先阅读数据迁移边界，不能直接丢弃文件。

## 验证与安全

项目持续开发中。基础服务和网页可正常运行，不代表全部质量门禁通过；
发布前阅读[测试及已知限制](docs/local-development.md#tests)。
模型、知识库和沙箱需要另外验收，不能用无模型回显代替。

不要提交密钥、上传文件、私有配置或构建产物。
正式环境使用 HTTPS，核对出站白名单与资源授权；需要时在反向代理限制 Swagger。

## 许可证

CreativeFlow 基于 [AIDotNet/Sol](https://github.com/AIDotNet/Sol) 二次开发。
上游 MIT 许可及 `Copyright (c) 2026 AIDotNet` 声明保留于 [LICENSE](LICENSE)。
该说明不代表 AIDotNet 维护或认可 CreativeFlow。
