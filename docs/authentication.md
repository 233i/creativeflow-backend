# 账号与登录

## 用户状态

CreativeFlow 强制登录。未登录时前端不会挂载画布，后端的设备握手、画布、渠道、素材、Agent、MCP、
Skill 和生成接口也不会接受仅凭设备 Cookie 的请求。匿名访问只保留注册、登录、OAuth 回调、
登录状态查询和健康检查所需的入口。

邮箱密码或 GitHub 登录成功后，浏览器得到不透明的账号 session Cookie。随后第一次设备握手会
创建或恢复 `device_id`，并把它写入 `creativeflow_account_device`。账号访问数据库时，服务端连接才会激活
该账号对应的设备集合，因此：

```text
未登录请求             -> 仅认证入口，业务 API 返回 401
有效 account session   -> 设备握手并建立显式关联
session + 关联 device   -> 该账号已明确关联的全部 device_id
```

清除浏览器数据不会删除云端数据；登录同一账号后，新设备会在第一次握手时被关联，并重新
看到账号作用域内的云端数据。退出登录会删除 session 和设备 Cookie，并从前端删除设备镜像；
页面随即回到登录门禁，不再继续加载画布。

## 邮箱密码注册与登录

邮箱密码入口默认启用，不依赖第三方服务：

- `POST /api/v1/auth/password/register` 创建账号；若浏览器已有未归属的设备，会在同一事务中关联。
- `POST /api/v1/auth/password/login` 校验凭据，将当前设备关联到已有账号，并创建不透明 session。
- 邮箱先进行 Unicode NFKC 和国际化域名归一化，再以不区分大小写的规范值建立唯一约束。
- 密码长度为 10–128 个字符，不强制大小写、数字或符号组合。服务端使用 ASP.NET Core
  Identity V3 的版本化 PBKDF2 格式、HMAC-SHA512、220,000 次迭代和随机 salt；旧工作因子可在
  登录成功后自动升级。
- 同一账号连续失败 5 次会锁定 15 分钟；HTTP 入口还按已登记设备（无有效设备时按 IP）限制为
  每分钟 10 次。不存在的账号也执行一次 dummy hash 校验，降低通过响应时间枚举邮箱的风险。
- 登录失败和锁定都返回统一的 `invalid_credentials`。注册时会明确返回邮箱已占用，方便正常注册
  流程；如果产品需要隐藏账号存在性，可改为邮件确认式注册。

当前版本尚未接入邮件投递服务，因此不包含邮箱所有权验证和“忘记密码”邮件。公网部署前应接入
邮件服务，增加短时、单次使用的验证/重置 token，并在修改密码后撤销该账号的其他 session。

## OAuth 安全边界

- OAuth state、PKCE verifier、provider、回跳路径和发起请求的设备都写入
  `creativeflow_oauth_transaction`，state 只以 SHA-256 哈希入库，且回调时原子消费并检查过期时间。
- GitHub client secret 只存在 API 进程；浏览器只会被重定向到 GitHub，不会收到 secret 或上游
  access token。
- session Cookie 是随机不透明值，数据库只保存 SHA-256；Cookie 使用 HttpOnly、SameSite=Lax、
  生产环境 Secure 属性。
- 第三方身份的唯一键是 `(provider_key, subject)`，绝不按 email 自动合并账号。email 只是展示
  信息，因为不同 provider 对 email 的验证语义不同。
- `DeviceContextMiddleware.GetDeviceId` 只有在请求同时存在有效账号 session 时才会向业务端点
  暴露设备身份；设备握手本身也要求账号 session，不能仅凭旧的 `creativeflow_did` Cookie 绕过登录。

## 多 provider 设计

`IExternalLoginProvider` 是唯一的 provider 适配边界。每个 provider 只负责：

1. 生成授权 URL；
2. 用 code 换取上游 profile；
3. 将上游 profile 归一化为 `provider_key + subject + displayName/email/avatarUrl`。

账号、session、OAuth transaction 和设备关联全部是 provider-neutral 的。新增 Google、Microsoft
或企业 OIDC 时，只需新增一个 provider adapter 并显式注册到
`IExternalLoginProviderRegistry`，不应在 `Account` 表添加 `github_id`、`google_id` 等列。

登录和绑定是两个不同的动作：

- `/api/v1/auth/login/{provider}`：登录已有 provider 身份，或为新身份创建账号；当前未关联的
  设备会被关联到该账号。
- `/api/v1/auth/link/{provider}`：必须已有有效 session，显式把另一个 provider 身份绑定到当前
  账号。如果该身份已经属于别的账号，返回冲突，不做隐式账号合并。

## 配置 GitHub

在 GitHub OAuth App 中把 Authorization callback URL 配置为：

```text
http://localhost:5298/api/v1/auth/callback/github
```

生产环境替换为浏览器实际访问的同源地址。通过环境变量提供 secret（不要提交到配置文件）：

```text
Authentication__PublicOrigin=https://creativeflow.example.com
Authentication__GitHub__Enabled=true
Authentication__GitHub__ClientId=...
Authentication__GitHub__ClientSecret=...
Authentication__GitHub__Scope=read:user user:email
```

`Authentication:PublicOrigin` 必须是客户端看到的 origin。开发环境和生产环境都应让客户端
通过同一个反向代理 origin 访问 API，这样 OAuth 回调和认证 Cookie 才能保持一致。

使用 Docker Compose 时，通过部署环境注入 `PUBLIC_ORIGIN`、`GITHUB_OAUTH_CLIENT_ID` 与
`GITHUB_OAUTH_CLIENT_SECRET`，并将 `GITHUB_OAUTH_ENABLED` 设为 `true`。重启 API 后，
OAuth 登录端点即可使用。
