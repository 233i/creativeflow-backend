# 知识分流与外部 API 示例

只验证安装是否可用时，先导入 [startup-echo.workflow.json](startup-echo.workflow.json)。
它只有“开始 → 结束”，输入 `message`，输出同样的 `result`，不需要模型、知识库或外部凭据，
也不会产生模型费用。完整步骤见[本地启动验收](../local-development.md#accept-the-complete-local-installation)。

[knowledge-routing.workflow.json](knowledge-routing.workflow.json) 使用稳定 `schemaVersion: 1`：开始 → 检索 → 条件分流 → 知识问答 / HTTP 查询 → 汇合 → 结束。另一分支跳过；检索失败不等于未命中。示例不含真实密钥，不自动调用服务。

## 准备

1. 准备知识库及固定就绪版本、已启用聊天模型，记录三个后台 UUID。使用真实远程模型前核对费用和授权。
2. 在“凭据管理”新建仅许可 `GET https://你的接口/query` 的凭据。没有认证要求也可使用零个认证头保留接口授权。该示例要求接口接收查询参数 `q`，返回 `{"data":{"result":"查询结果"}}`。
3. 记录凭据和版本 UUID。GET `/api/v1/admin/http-credentials/catalog` 可取得不含密钥的目录；[credential-catalog.example.json](credential-catalog.example.json) 仅示范形状。切勿导出 Cookie 或密钥。
4. 执行以下命令，脚本仅输出创建草稿请求，不登录、不发布、不调用模型：

```bash
node scripts/prepare-workflow-example.mjs \
  LIBRARY_UUID VERSION_UUID CHAT_MODEL_UUID CREDENTIAL_UUID REVISION_UUID \
  /absolute/path/to/safe-credential-catalog.json
```

最后一个参数可省略，但输出图仍包含示例 URL，必须到 HTTP 节点选择凭据并核对地址与提取规则。脚本只接受一个精确 GET 许可；已迁移并锁定完整契约的旧查询凭据不能用于替换成这个新示例。URL、q 参数、`/data/result` 和 1 MiB 上限都保存在节点中。

可将脚本输出作为 POST `/api/v1/admin/workflow-apis` 的管理请求体，或按编辑器支持的图导入格式使用 graph。保存、调试后发布 API；分别给 Key 授予知识库和凭据权限。发布或创建 Key 不等于资源授权。

本地合成查询可运行 `node scripts/workflow-http-dev-fixture.mjs`，并在 Development 精确放行 `http://127.0.0.1:18599/query`；本地合成接口不查询业务数据库。

## 一次提交，后续只查询

先从私有环境注入 `CREATIVEFLOW_API_KEY`，不把密钥写进命令行或 Git：

```bash
CREATIVEFLOW_ORIGIN=http://localhost:5298 \
CREATIVEFLOW_IDEMPOTENCY_KEY=synthetic-question-0001 \
node scripts/call-workflow-example.mjs WORKFLOW_UUID '星舟随行杯的保修期限是多久？'
```

客户端只 POST 一次并保存 runId，随后 GET 轮询。约五分钟轮询窗口结束不代表任务取消；单次 HTTP 上限 30 秒。已知运行可只查询：

```bash
CREATIVEFLOW_ORIGIN=http://localhost:5298 \
CREATIVEFLOW_RUN_ID=RUN_UUID \
node scripts/call-workflow-example.mjs
```

提交响应丢失、GET 失败或未知上游结果都不自动重新生成。显式重放必须使用原 Key、原幂等键和原输入。
Key 和资源授权在每次读取时检查；非回环服务必须 HTTPS，不跟随跨源重定向。

退出码：0 成功；1 任务/HTTP/回执失败；2 参数错误；3 轮询结束但仍未完成。业务模型效果应另行评估，合成链路通过不能证明回答质量。

## 回归测试

```bash
node --test scripts/workflow-example.test.mjs scripts/workflow-client.test.mjs
```

准备脚本覆盖单一图版本、凭据精确版本与 GET 许可、配置留在节点、无效 UUID；客户端通过合成 HTTP 服务检查提交、查询、恢复和错误行为，不连接共享队列或模型。C# `WorkflowExampleTests` 验证完整图可发布。
