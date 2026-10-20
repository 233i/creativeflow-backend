# 代码分层与注释规范

本规范用于保持现有功能，并让职责、边界和约束可以从代码中直接读出。

## 分层约束

- Domain 保存业务类型与不变量，不显式引用其他项目或 NuGet 包。
- Application 定义用例、端口和契约，通过 `AddApplication` 显式注册用例；不依赖 HTTP、数据库实现或后台宿主。
- Infrastructure 实现端口，所有 Dapper 调用保留在此程序集，以继续获得 Dapper.AOT 生成与诊断。
- API 承担路由、绑定、身份上下文、响应和 SignalR；业务规则优先下沉到 Application 用例。
- 同一个后台运行所使用的作用域、账号范围、取消源和事件顺序不可因拆文件而改变。`IAgentRunControl` 与 HostedService 必须共享同一个 Agent 宿主实例。

`Infrastructure/DependencyInjection/` 的 partial 文件仅组织已有注册组，公共入口仍为 `AddInfrastructure`。这不表示其他大型业务类可以只拆为 partial 文件就完成职责治理。

## 方法说明

每个手写方法、构造函数、运算符及局部函数必须有说明，覆盖公开、内部、私有和测试代码。

- 普通方法使用非空的 XML `<summary>`，说明实际职责、关键输入或结果。不能只复述方法名，也不能描述该方法没有执行的校验、副作用或重试。
- 接口方法写清端口契约；实现和覆盖方法也必须在声明处编写有实际正文的 `<summary>`，说明当前实现的职责及事务、并发或兼容规则。不能用 `<inheritdoc />`、空摘要或只有符号引用的摘要代替说明。
- 测试替身说明实际的记录、固定返回、空操作或拒绝调用行为，不能把生产接口的数据库写入、权限校验等职责误写成测试替身已经实现的能力。
- 主构造函数的类型摘要说明装配职责；局部函数使用普通 `//` 说明。
- 有重要缺省值、单位、返回状态或异常时补充 `<param>`、`<returns>`、`<remarks>`。不要求每个方法机械重复所有参数名。

```csharp
/// <summary>在已验证设备范围内生成图片，按既有顺序保存字节与素材元数据。</summary>
/// <param name="deviceId">交付层验证过的设备标识，仓储按当前账号范围解析可访问素材。</param>
/// <param name="request">原有请求契约，不改变可接受字段及缺省值。</param>
/// <param name="ct">传递到上游与存储操作的取消令牌。</param>
public Task<GenerateImagesResult> HandleAsync(
    DeviceId deviceId, GenerateImageRequest request, CancellationToken ct);
```

## 实体、字段与契约

实体、DTO、数据库 Row/Args、配置、常量和枚举值都要有逐项说明。说明优先表达业务含义、单位、可空含义、归属、敏感性和兼容格式，避免把不同类型的同名字段写成错误的统一解释。

- 普通属性、字段、枚举值使用 XML `<summary>`。
- 位置记录的每个参数使用记录声明上的 `<param name="...">`；这也是对应自动生成属性的说明。无需为了添加注释改成普通类或更改 JSON 字段。
- 现有有效说明保留；不得为补注释改名、修改类型/默认值、调整序列化属性或增加业务校验。
- 编译器合成的成员、匿名函数及自动属性访问器不单独要求方法摘要。

```csharp
/// <summary>素材存储完成后的定位信息。</summary>
/// <param name="StoragePath">存储后端返回的不透明键，不接受用户指定文件路径。</param>
/// <param name="MediaType">内容的 MIME 类型。</param>
/// <param name="ByteSize">原始内容的字节数。</param>
public sealed record StoredAsset(string StoragePath, string MediaType, long ByteSize);
```

## 方法内部的合理注释

内部注释解释关键步骤为何存在，以及改变顺序会产生什么影响。例如：普通参考图可跳过、蒙版不可跳过；取消状态不能被稍后的成功写入覆盖；事件序号递增和事件插入必须在同一事务内；云端内容缺失时哪些路径允许回退。

不要逐行翻译赋值或循环。简单转发、直接字段映射和显然的取值方法有方法摘要即可。即使不足 30 行，只要有隐含权限、并发或协议规则，也应写明。

自动检查以“方法体跨度至少 30 行，包含分支/循环/异常处理，且没有内部说明”为最低缺失判定。它不能证明注释准确，也不能代替对复杂方法的人工审阅。更新逻辑时必须同步检查注释是否仍成立。

## 可执行检查

在安装了 `global.json` 锁定 SDK 的环境中运行：

```bash
bash scripts/verify-governance.sh
```

如使用隔离 SDK 安装，显式选择其可执行文件：

```bash
CREATIVEFLOW_DOTNET_BIN=/absolute/path/to/dotnet bash scripts/verify-governance.sh
```

脚本依次执行 Release 构建、注释检查、单元/HTTP 兼容/架构测试、MVC/JIT 发布与差异空白检查。测试报告及发布目录位于已被 Git 忽略的 `artifacts/governance/`。

仅检查注释：

```bash
dotnet run --project tools/CreativeFlow.CodeQuality -c Release -- .
```

检查工具使用锁定 SDK 自带的 Roslyn，扫描 `src/`、`tests/`、`tools/`，排除 `bin/`、`obj/` 和显式自动生成文件；不增加生产运行时依赖。真实数据库、Cookie/SignalR 和端到端验收应在隔离环境中另行执行，本脚本不会启动这些服务。
