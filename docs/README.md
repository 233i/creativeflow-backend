# 后端文档导航

这些文档说明当前代码如何安装、配置、接入和维护，不包含内部部署日志、历史开发计划或过期验收报告。
先按根目录 [中文 README](../README.zh-CN.md) / [English README](../README.md) 启动基础服务。
完整网页还需要[前端仓库](https://github.com/233i/creativeflow-frontend)。

## 按任务阅读

| 你要做什么 | 阅读顺序 |
| --- | --- |
| 第一次在本机运行 | README → [本地开发与前后端联调](local-development.md) → [无模型回显示例](examples/README.md) |
| 部署自己的服务器 | [配置](configuration.md) → [部署](deployment.md) → [运维与备份](operations.md) |
| 接入外部业务系统 | [工作流概念](workflows.md) → [REST/SSE](workflow-api-streaming.md) 或 [AG-UI](ag-ui-workflow-api.md) |
| 配置模型与知识库 | [模型/画布 API](ai-canvas-api.md) → [端点许可](configuration.md#endpoint-allowlists) → [知识库](knowledge.md) |
| 修改或维护代码 | [架构](architecture.md) → [数据模型与迁移](data-model.md) → [开发规范](coding-standards.md) |

## 功能参考

- [认证与权限](authentication.md)：用户、管理员、外部 API Key 与设备范围。
- [资产中心](asset-center.md)：素材授权、复用、回收和物理存储。
- [异步图片任务](image-generation-jobs.md)：查询、重新下载与重新生成的区别。
- [HTTP 节点](workflow-http-request-node.md)：节点配置、凭据固定版本、1 MiB 响应上限。
- [SignalR](realtime-signalr.md)：应用内实时通知与 Agent 浏览器工具回执。
- [工作流示例](examples/README.md)：无模型启动验收、知识分流和外部调用。

## 配置与契约的依据

- 默认配置：[appsettings.json](../src/CreativeFlow.Api/appsettings.json)。
- 本机覆盖模板：[Development JSON](../deploy/development/appsettings.Development.local.example.json)。
- 正式环境模板：[backend.env.example](../deploy/production/backend.env.example)。
- HTTP 路由与字段：[Controllers](../src/CreativeFlow.Api/Controllers/) 及运行中实例 `/swagger/index.html`。
- 数据库：[迁移目录](../db/migrations/)和[不可改写的校验文件](../tests/CreativeFlow.UnitTests/Contracts/migration-checksums.json)。
- 隔离集成验收：[测试环境说明](../tests/maf-e2e/README.md)。

`docs/examples/` 中的 JSON 是脚本/测试使用的契约样本，不是可随意删除的历史产物。
变更行为时同步更新所属文档，其他页面只链接该说明，避免复制多份配置或接口表。
启动验收不等于全部测试通过；已知质量限制保留在[本地测试](local-development.md#tests)中。
