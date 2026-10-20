# 数据模型与迁移

PostgreSQL 保存业务状态和元数据；媒体文件、Skill 包与向量索引使用独立存储。
不要把重新建立 Git 历史、首次部署和升级已有数据库混为一谈。

## 存储分工

| 存储 | 保存内容 | 备份要求 |
| --- | --- | --- |
| PostgreSQL | 账号、权限、模型配置、工作流图/快照、任务状态、知识正文/切片、文件元数据及审计 | 与文件卷在一致的业务时间点备份 |
| `Ai:AssetRoot` | 图片、视频、预览图等文件内容 | 持久卷，不能放在镜像或临时发布目录 |
| `Skills:Root` | 已安装 Skill 的正文与包文件 | 持久卷，保留文件权限 |
| Qdrant | 知识版本对应的向量索引 | 与库/版本记录对应；单独备份或按受控流程重建 |
| Redis / RabbitMQ | 缓存、锁、在线状态、可选实时背板 / 集成消息 | 不替代数据库中的工作流队列与结果 |

## 主要关系

- `creativeflow_account` → `creativeflow_account_device` → `device`：明确登录绑定的账号范围。
- `creativeflow_external_identity` / `creativeflow_password_credential` → 账号：
  第三方身份与邮箱密码均归到账号，不能按相同邮箱自动合并第三方身份。
- `creativeflow_auth_session` 与 `creativeflow_admin_session`：普通用户和管理员会话分离。
- `device_link` → `visitor`：设备的概率性关联，不能用于授予资源权限。
- `canvas` / `canvas_asset`：画布图与逻辑素材；分组删除不等于文件删除。
- `workflow` / `workflow_run` / `workflow_node_run`：定义、不可变运行快照、节点执行结果。
- `workflow_run_event`：AG-UI 事务日志；不是全部外部运行都会产生这种日志。
- `image_job_output`：生成结果的下载/保存检查点，不再保存媒体正文。

上表是领域索引，不是完整建表脚本。完整约束、索引和列定义以
[迁移目录](../db/migrations/)为准。

## 名称与授权范围

当前并非所有表都带 `creativeflow_` 前缀：账号/会话表有前缀，
`device`、`canvas_asset`、`workflow_run`、`schema_version` 等仍使用其现有名称。
迁移 0045 只重命名指定的旧 `sol_*` 对象并更新相关函数，不重建业务数据。

业务数据仍有 `device_id` 归属。服务端验证账号与设备后，将账号范围写入数据库连接的
`creativeflow.account_id` setting，由 `creativeflow_accessible_device_ids` 解析明确绑定的设备集合。
`visitor` 不参与该授权。管理员和外部 API 的资源授权由各自入口另外校验。

## 文件元数据

迁移 0040 切换为独立文件存储，删除旧数据库文件正文；迁移 0045 负责命名空间。
当前代码没有数据库 blob 读取兜底：

- `storage_object`：内容摘要、类型、大小、`objects/<前缀>/<SHA-256>` 存储键与清理状态。
- `storage_write`：稳定操作键、写入租约、摘要和完成状态，支撑临时文件/原子发布后的恢复。
- `canvas_asset` / `asset_preview`：逻辑素材与预览元数据；多个逻辑素材可以复用同一物理内容。
- `image_job_output`：下载地址和存储操作键等恢复信息，恢复保存不能隐式再次调用模型。

读取先授权再打开文件流，视频 Range 不需要从数据库加载整个文件。
资产回收和物理清理需核对引用；不能直接删除磁盘目录或数据库行来清理素材。
详见[资产中心](asset-center.md)。

## 迁移规则

1. **完整保留 `0001–0045` 及**
   [`migration-checksums.json`](../tests/CreativeFlow.UnitTests/Contracts/migration-checksums.json)。
   历史 SQL 中的 `sol_` 是原迁移内容，不能为改名或公开仓库而批量替换。
2. 新安装也按完整迁移链初始化，不能只执行最后一个脚本。
3. 已应用脚本不可改写；修复用新迁移，不能重新生成校验值来掩盖差异。
4. 使用当前程序的 `--migrate-only`；运行器包含锁、校验、事务和部分应用级迁移逻辑，
   不能用逐个执行 `psql -f` 替代。
5. `Postgres:RunMigrationsOnStartup=false` 适用于受控发布；迁移成功后才启动新 API。

运行器取得 PostgreSQL advisory lock，校验已应用脚本的 SHA-256，
按序在事务内执行待应用脚本并写 `schema_version`，最后释放锁。
校验失败应恢复正确脚本并核对部署版本，不能清空迁移记录后重跑。

```bash
# 只针对已经核对过的目标数据库执行。
dotnet run --project src/CreativeFlow.Api -- --migrate-only
docker compose exec -T postgres psql -U creativeflow -d creativeflow \
  -c 'SELECT version, name, applied_at FROM schema_version ORDER BY version;'
```

## 升级已有实例

先停止新任务、处理活动任务，备份数据库、素材、Skills、可选 Qdrant 数据和密钥，
在备份副本上演练，再执行[部署流程](deployment.md)。
不承诺任意旧二进制可以直接读取新 schema。

特别是 **0040 之前仍在数据库保存文件的安装**：本仓库没有通用的保留旧 blob
自动迁移方案，必须先另行核对导出和引用迁移。`--discard-database-files`
是显式丢弃旧文件的选项，不是修复命令，也不能用于要求保留媒体的升级。
0045 的保留数据改名不能代替 0040 的文件迁移。

数据库测试使用独立资源；当前 0045 的 `public` schema 与部分测试夹具的临时 schema
存在隔离差异，详见[测试限制](local-development.md#tests)。
