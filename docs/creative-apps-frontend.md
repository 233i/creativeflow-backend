# 公共创作前端对接

Web 和桌面客户端使用这组接口打开管理员发布的创作应用，根据表单收集输入，提交图片或视频任务，再查询进度和成品。服务端受理后立即返回任务 ID，页面关闭后任务继续执行。发布流程和应用额度见[公共创作发布](public-creative-apps.md)。

## 登录与设备握手

1. 调用 `POST /api/v1/auth/password/login`，提交 `{"email":"user@example.com","password":"<用户输入的密码>"}`。服务端通过 Cookie 建立普通账户会话。
2. 保留登录响应的 Cookie，调用 `POST /api/v1/device/handshake`，提交 `{"v":1}`，建立当前设备与账户的关联。
3. 列表、任务、素材和事件请求都携带会话及设备 Cookie。浏览器通过同源 `/api/v1` 代理调用，桌面端在原生 HTTP 会话中管理 Cookie。认证入口和权限范围见[认证与权限](authentication.md)。

使用 API Key 的业务系统走独立发布接口。详见 [REST/SSE](workflow-api-streaming.md) 或 [AG-UI](ag-ui-workflow-api.md)。

## 接口一览

公共创作的路径前缀为 `/api/v1/creative-apps`。下表中的 `appId` 对应应用详情里的 `id`。

| 方法与相对路径 | 请求和返回 |
| --- | --- |
| `GET /` | 可选 `search`、`kind=image\|video`、`offset`。返回 `{ items }`，每页最多 50 个 |
| `GET /{appId}` | 当前上架版本的表单、输出声明和限额 |
| `GET /{appId}/cover` | 当前版本的封面内容。也可直接使用详情里的 `coverUrl` |
| `POST /{appId}/runs` | 受理新任务或返回同键任务，返回 `202`、`Location` 和完整任务视图 |
| `GET /{appId}/requests/{requestKey}` | 按原提交键找回任务，返回同一任务视图 |
| `GET /runs/{runId}` | 当前账户任务的状态、节点和已保存结果 |
| `GET /runs/{runId}/events` | SSE `progress` 事件，每帧是完整任务视图 |
| `DELETE /runs/{runId}` | 取消当前账户的活动任务，成功返回 `204` |
| `GET /runs?offset=0` | 当前账户任务摘要，可按 `appId` 筛选。返回 `{ items }`，每页最多 50 条 |
| `GET /outputs?offset=0` | 当前账户历史成品，返回 `{ items, hasMore, offset, limit }` |

目录和任务历史的下一页使用 `offset + 50`，结果少于 50 条时结束。历史成品使用响应里的 `hasMore` 和 `limit`。

## 渲染表单与选择素材

应用详情中的 `releaseId` 是本次提交绑定的发布版本，`version` 用于展示。用 `inputs[].key` 作为表单字段名，显示 `label` 和 `description`。页面可以用 `default` 初始化字段，并按 `required` 和 `constraints` 做校验。提交时带上所有必填字段的值，选填字段留空时可从请求中省略。

目录可展示 `name`、`description`、`category` 和 `coverUrl`。详情中的 `dailyLimit` 是每用户每日提交额度，`userConcurrency` 是每用户在这个应用内的并发上限。

| 字段 `type` | `inputs` 中的值 | 可用约束 |
| --- | --- | --- |
| `string` | JSON 字符串 | `constraints.maxLength` |
| `integer` | JSON 整数 | `constraints.minimum`、`constraints.maximum` |
| `boolean` | `true` 或 `false` | 两个值都可作为已填写的值 |
| `enum` | 枚举值字符串 | `constraints.values` |
| `image` | `{"assetId":"<素材 UUID>"}` | 素材须是当前账户可访问的有效图片 |

- 本地上传使用 `POST /api/v1/canvas/assets`。把文件放在 multipart 的 `file` 字段，由浏览器设置 Content-Type 和 boundary。图片支持 PNG、JPEG、WebP、GIF，单文件上限为 20 MiB。成功后使用返回的 `assetId`。
- 从素材库选取使用 `GET /api/v1/canvas/assets?kind=image`，把 `assets[].id` 作为 `assetId`。已选素材可直接复用。
- 提交 JSON 的图片字段只放素材 ID。图片文件通过上传接口单独发送，站内预览使用素材接口返回的 `url`。

应用的 `outputs` 数组声明结果的 `key`、`label` 和 `type`。成品 URL 从运行结果中读取。

## 提交和找回任务

用户确认生成时，先保存这次操作的 `appId` 和 `requestKey`，再发送最多 128 KiB 的 JSON。请求键可用 `crypto.randomUUID()`，接受 1–128 个 ASCII 字母、数字、连字符、下划线或点。新生成使用新键。同一账户和应用下，同一个键与相同版本及输入对应同一个任务。

以下示例假设应用表单公开了 `prompt` 和 `reference` 两个字段。

```json
{
  "releaseId": "00000000-0000-0000-0000-000000000001",
  "requestKey": "f0454368-2d51-4e22-bfc9-3ba7fa4536c2",
  "inputs": {
    "prompt": "自然光商品展示",
    "reference": { "assetId": "00000000-0000-0000-0000-000000000002" }
  }
}
```

收到 `202` 后，用响应体的 `id` 作为 `runId`，查询地址也可取响应头的 `Location`。请求中断、响应读不到或收到 `5xx` 时，先按原 `requestKey` 查询受理情况。查到任务后继续跟踪原任务。查询暂时返回 `404` 时显示「提交结果待确认」，提供重新查询入口，避免自动发起新的生成。

## 进度和失败处理

任务视图由下列字段组成。首次提交、按键找回、状态查询和 SSE 共用该结构。

| 字段 | 用途 |
| --- | --- |
| `id`、`appId`、`releaseId`、`version`、`name` | 任务、应用和固定版本的标识与名称 |
| `status` | `queued`、`running` 为活动状态。以 `succeeded`、`failed`、`cancelled` 判断终态 |
| `stage` | 活动任务可展示「排队」`queued`、「生成」`generating`、「保存」`saving`。终态时与 `status` 一致 |
| `progress` | 0–1 的工作流节点完成比例，包含跳过的节点 |
| `createdAt`、`updatedAt`、`startedAt`、`finishedAt` | ISO 8601 时间。开始和结束时间可能为空。等待时间从 `createdAt` 起算 |
| `nodes` | 每个节点有 `id`、`name`、`type`、`status`、`progress`、`startedAt`、`finishedAt`。进度可能为空，可显示活动状态 |
| `inputs` | 调用者本次提交的字段 |
| `outputs` | 已保存的声明产物。每项有 `key`、`label`、`kind`、`url`、`mediaType`。同一输出键可包含多个产物 |
| `error` | 客户可见的简短说明，可能为空 |

活动任务可每约 2 秒查一次。上一条查询结束后再安排下一次，终态或离开页面时停止，返回页面时用 `runId` 或任务历史恢复。使用 SSE 时，监听 `progress` 事件并整体替换视图。连接约 30 秒会结束，断线后先查最新状态，任务仍活动时再订阅。事件没有回放游标。取消要调用 DELETE，再查最终状态。已发给上游的工作可能继续处理。

| 情况 | 前端行为 |
| --- | --- |
| `401` | 检查普通账户会话和设备握手 |
| `400`，如 `required_input` 或 `invalid_inputs` | 显示响应的 `message`，回到表单修正 |
| `403 asset_denied` | 提示重新选择当前账户的可用图片 |
| `404 not_published` | 刷新应用目录 |
| `409 version_changed` | 重新读取应用详情并请用户确认新版表单 |
| `409 idempotency_conflict` | 用原请求键查询原任务，新的生成单独提交 |
| `429 queue_full` 或 `429 daily_limit` | 显示服务端提示和等待时间。队列拥堵可参考 `Retry-After`，每日额度以 UTC 零点重置 |
| 任务 `failed` | 展示 `error` 与可复制的任务 ID，保留已保存的 `outputs`。错误文本为空时显示「生成未完成」。具体模型或传输错误由管理员按任务 ID 查询执行详情 |

「重新查询」读取原任务。「重新下载」读取已保存成品的 URL。「重新生成」由用户确认后带新请求键提交。遇到上游连接中断且结果不明确时，保留任务编号供核对，前端以 `status` 判断终态，错误文本只用于展示。

## 预览、下载与历史成品

在同源页面将成品的 `url` 用于 `<img>` 或 `<video controls>`。下载时对同一个 URL 发 GET 并携带会话 Cookie。视频接口支持 Range，代理应转发 `Range` 请求头并保留 `206`、`Content-Range` 和媒体类型。

`GET /api/v1/creative-apps/outputs` 查询当前账户仍可读取的已保存声明产物。应用下架后仍可查到历史成品，失败任务中已经保存并声明的成品也可展示。

- 可选筛选为 `appId`、`kind`（`image` 或 `video`）、`search`（最多 100 字，按字面匹配应用名称、产物标签或任务 ID）、`days`（`0`、`7`、`30`、`90`）和 `offset`（0–100000）。`days=0` 查询全部时间。
- 每页 24 条，按任务创建时间倒序。同一任务内按声明输出及产物顺序排列。使用 `hasMore` 判断是否继续加载，下一页的 `offset` 为本页的 `offset + limit`。
- 每项包含 `id`、`assetId`、`url`、`kind`、`mediaType`、`byteSize`、`createdAt`、`key`、`label`、`runId`、`runStatus`、`appId`、`name` 和 `version`。用 `id` 作为列表键，用 `runId` 打开任务详情。

## 浏览器调用示例

登录和握手完成后，先从应用详情取得 `appId` 和 `releaseId`。下列函数接受调用方传入的请求键，调用前可先把键保存到当前账户的待确认记录中。

```javascript
const creativeApi = "/api/v1/creative-apps";

async function requestJson(path, options = {}) {
  const response = await fetch(path, { ...options, credentials: "same-origin" });
  if (response.status === 204) return null;

  if (!response.ok) {
    const detail = await response.json().catch(() => null);
    throw Object.assign(new Error(detail?.message ?? `请求失败（${response.status}）`), {
      status: response.status,
      code: detail?.error,
      retryAfter: response.headers.get("Retry-After"),
    });
  }

  return response.json();
}

export async function uploadReference(file, signal) {
  const form = new FormData();
  form.set("file", file);
  const { assetId } = await requestJson("/api/v1/canvas/assets", {
    method: "POST",
    body: form,
    signal,
  });
  return { assetId };
}

export function submitRun(appId, { releaseId, requestKey, inputs }, signal) {
  return requestJson(`${creativeApi}/${appId}/runs`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ releaseId, requestKey, inputs }),
    signal,
  });
}

export function getRun(runId, signal) {
  return requestJson(`${creativeApi}/runs/${runId}`, { signal });
}

export async function findSubmittedRun(appId, requestKey, signal) {
  try {
    return await requestJson(`${creativeApi}/${appId}/requests/${encodeURIComponent(requestKey)}`, {
      signal,
    });
  } catch (error) {
    if (error.status === 404) return null;
    throw error;
  }
}
```

接口契约与用例见[公共创作控制器](../src/CreativeFlow.Api/Controllers/CreativeAppsController.cs)、[任务状态投影](../src/CreativeFlow.Application/Features/CreativeApps/CreativeAppService.cs)和[HTTP 集成测试](../tests/CreativeFlow.UnitTests/Api/CreativeAppsHttpTests.cs)。
