# 数据模型

## 独立发行版迁移说明

独立发行版使用 `creativeflow_` 数据库对象前缀、`creativeflow` Redis/RabbitMQ 命名空间和
`creativeflow_*` Cookie。它从全新数据库开始初始化；从旧版数据库升级时，不能只替换二进制，
必须先备份并按业务停机窗口完成表、函数、触发器、连接配置和持久化文件的迁移，再执行新版本迁移。
仓库中的迁移校验基线已经按独立发行版重新生成，避免把旧对象名称带入新安装。

## ER

```
visitor 1 ──< device_link >── 1 device
account 1 ──< account_device >── 1 device
account 1 ──< external_identity
account 1 ─── 0..1 password_credential
account 1 ──< auth_session
              (可逆的边，带置信度)
```

一个 `visitor` 代表"推测的同一台物理设备"，聚合一个或多个 `device`（浏览器profile）。第一台之外的成员关系都是**概率性**的。

`account` 是用户主动通过邮箱密码或第三方登录建立的身份。`account_device` 是明确授权边，不使用
`visitor` 的概率关联；登录 session 有效且当前 device 在这张表中时，账号可以访问其全部
关联设备数据。数据层仍保留单设备作用域作为纵深防御，但未登录的 HTTP 和 SignalR 请求不能进入
业务端点。

### `creativeflow_external_identity` — 第三方身份映射

| 列 | 类型 | 说明 |
|---|---|---|
| `provider_key` | `text` | `github`、未来的 `google` / `oidc-*` 等 |
| `subject` | `text` | provider 返回的稳定用户 ID，不是 email |
| `account_id` | `uuid` | 所属账号 |

唯一约束是 `(provider_key, subject)`。不同 provider 的资料通过统一的 provider adapter
归一化，禁止按 email 自动合并。

### `creativeflow_password_credential` — 邮箱密码凭据

| 列 | 类型 | 说明 |
|---|---|---|
| `account_id` | `uuid` PK/FK | 每个账号至多一条邮箱密码凭据 |
| `normalized_email` | `text` UNIQUE | NFKC、IDNA 和大小写归一化后的唯一登录键 |
| `password_hash` | `text` | ASP.NET Core Identity V3 的版本化密码哈希，不保存明文密码 |
| `failed_attempts` | `int` | 连续失败次数，成功登录后清零 |
| `locked_until` | `timestamptz` | 达到阈值后的临时锁定截止时间 |
| `created_at` / `updated_at` | `timestamptz` | |

原始邮箱不另存一份；展示邮箱使用注册时写入 `creativeflow_account.email` 的规范形式。凭据和第三方身份都
指向同一个 provider-neutral 账号，因此同一 session 和设备授权模型可以复用。

## 表

### `device` — 浏览器 profile

| 列 | 类型 | 说明 |
|---|---|---|
| `device_id` | `uuid` PK | UUIDv7，使主键索引按创建时间聚簇 |
| `fp_exact` | `bytea` | 全信号哈希（含浏览器相关信号）。相等即同一浏览器 |
| `fp_coarse` | `bytea` | 浏览器**无关**信号 + ip_prefix 的哈希。唯一能跨浏览器匹配的指纹，且很弱 |
| `ip_prefix` | `inet` | IPv4 `/24`、IPv6 `/48` |
| `signals` | `jsonb` | 归一化后的信号**分量**（不只是哈希） |
| `sig_version` | `int` | 产出哈希的归一化方案版本 |
| `user_agent` | `text` | 便于排查，不参与匹配决策 |
| `first_seen` / `last_seen` | `timestamptz` | |

**为什么 `bytea` 而非 `text`**：32 字节原始哈希优于 64 字符 hex，索引更小。

**为什么存 `signals` 分量而不只存哈希**：只存哈希会让未来的信号集升级失去全部历史设备。存分量才能重新推导，也才能做漂移分析。

### `visitor` — 推测的物理设备

| 列 | 类型 |
|---|---|
| `visitor_id` | `uuid` PK |
| `created_at` | `timestamptz` |

### `device_link` — 设备到访客的边

| 列 | 类型 | 说明 |
|---|---|---|
| `device_id` | `uuid` FK → device | `ON DELETE CASCADE` |
| `visitor_id` | `uuid` FK → visitor | `ON DELETE CASCADE` |
| `confidence` | `real` | `CHECK (> 0 AND <= 1)` |
| `method` | `smallint` | `CHECK IN (0,1,2)`：0=确定性 1=概率性 2=人工 |
| `created_at` | `timestamptz` | |
| PK | `(device_id, visitor_id)` | |

**为什么是边而不是行合并**：误判只需一条 `DELETE`，而不是一次数据抢救。写入用 `ON CONFLICT DO UPDATE` 且置信度**只升不降**——后来的确定性命中会覆盖早先的猜测，反之不会。

**为什么 `method` 用 `smallint` 而非 PG enum**：避免 `NpgsqlSlimDataSourceBuilder` 需要额外的类型映射配置（而 enum 映射的非泛型重载在 AOT 下不可用）。

⚠️ **类型映射注意**：写入时 SQL 里必须写 `@Method::smallint`——参数以 `int` 传入，Npgsql 拒绝把 `Int32` 直接写进 `smallint` 列。读取 `timestamptz` 时行类型要声明 `DateTime`（Npgsql 返回 UTC `DateTime`），声明 `DateTimeOffset` 会触发不存在的 `Convert.ChangeType` 转换而抛异常。

### `schema_version` — 迁移记录

| 列 | 类型 | 说明 |
|---|---|---|
| `version` | `int` PK | |
| `name` | `text` | |
| `checksum` | `bytea` | 脚本的 SHA-256，**每次启动重新校验** |
| `applied_at` | `timestamptz` | |

### `canvas_asset_group` — 素材分组

| 列 | 类型 | 说明 |
|---|---|---|
| `group_id` | `uuid` PK | UUIDv7 分组 ID |
| `device_id` | `uuid` FK → device | 分组只属于当前设备 |
| `name` | `text` | 同一设备内不区分大小写唯一 |
| `created_at` / `updated_at` | `timestamptz` | |

`canvas_asset.group_id` 是可空外键，删除分组时 `ON DELETE SET NULL`，因此分组删除不会
删除或破坏素材。

### 文件存储元数据（迁移 0040 起）

图片、视频、缩略图只存储在 `Ai:AssetRoot` 持久卷中，Skill 包只存储在 `Skills:Root`。
`creativeflow_asset_blob`、`creativeflow_skill_file` 已删除，`image_job_output.payload` 已删除；没有数据库或旧文件键回退。

- `storage_object`：不可变的 `objects/<摘要前两位>/<SHA-256>` 存储键、摘要、类型、字节数、
  `ready/deleting/deleted` 状态和保护期限。同一内容只保留一个物理文件。
- `storage_write`：稳定操作键、租约、摘要、大小和 `writing/ready/abandoned` 状态，
  支持完整临时文件及 rename 后数据库提交失败的恢复。不包含正文。
- `image_job_output`：预留的素材 ID、输出序号、上游下载信息、`storage_operation_key`、
  已确认 `storage_path`、完成标记。重试先找回已有文件，不重新调用模型。
- `canvas_asset`、`asset_preview` 保留业务元数据；新引用必须指向 `ready` 文件。
  清理继续使用资产引用域的事务锁，不能删除仍有其他资产或未保存任务引用的文件。
- Skill 仍保存 `skill` 元数据，包文件按设备和 Skill ID 隔离，以临时目录写完后原子发布。
- 读取仍先检查账号/API Key/工作流权限；文件卷不通过静态站点公开。API 返回可定位文件流，
  图片下载、视频播放和 Range 请求不再从 PostgreSQL 读取整份文件。

数据库和文件卷必须配套备份、恢复。素材文件只走持久化文件存储；哈希、加密凭据等非文件
`bytea` 不受影响。部署和恢复边界见[运维说明](operations.md)。

## 索引

```sql
ix_device_coarse_ip_seen  ON device (fp_coarse, ip_prefix, last_seen DESC)
ix_device_fp_exact        ON device (fp_exact)
ix_device_link_visitor    ON device_link (visitor_id)
```

第一个直接服务模糊候选查询：`(fp_coarse, ip_prefix)` 等值 + `last_seen` 时间窗 + 小 LIMIT。

## 迁移机制

AOT 下 EF Core Migrations 不可用（依赖运行时模型构建），改为版本化 SQL + `schema_version` 表。

- 脚本位于 `db/migrations/NNNN__name.sql`，以 `<EmbeddedResource>` 嵌入 `CreativeFlow.Infrastructure`——**单个原生二进制自带它期望的 schema**
- 命名：零填充序号 + 双下划线 + snake_case

`MigrationRunner` 流程：

1. `pg_advisory_lock(固定常量)` —— 串行化并发实例。**没有这一步，多副本同时启动会在 DDL 上竞争死锁**
2. 对 `version > max(applied)` 的脚本按序执行，**脚本 + 版本插入在同一事务内**，全有或全无
3. 对已应用版本重算 SHA-256 与库中 `checksum` 比对，**不一致则启动失败并大声报错**——修改已发布的迁移是 bug，会让各环境静默分叉，比启动失败昂贵得多
4. `pg_advisory_unlock`

由 `Database:RunMigrationsOnStartup` 控制（开发为 true）。**生产建议关闭**，改用同一二进制加 `--migrate-only` 作为独立部署步骤，使 schema 变更成为显式动作而非发布的副作用。

所有脚本都写成幂等（`IF NOT EXISTS`），作为纵深防御。

## 查询

```bash
docker compose exec -T postgres psql -U creativeflow -d creativeflow -c "SELECT * FROM schema_version ORDER BY version;"
docker compose exec -T postgres psql -U creativeflow -d creativeflow -c \
  "SELECT visitor_id, count(*) devices, min(confidence), array_agg(DISTINCT method) FROM device_link GROUP BY 1;"
```
