# 运行时与 Dapper.AOT 约束

## 当前运行时决策

`CreativeFlow.Api` 使用 ASP.NET Core MVC Controllers，并以 JIT 模式运行和发布。不得为 API 项目设置
`PublishAot=true`：截至 .NET 10，微软仍将 MVC 列为
[不支持 Native AOT](https://learn.microsoft.com/aspnet/core/fundamentals/native-aot?view=aspnetcore-10.0)
的功能。

这项选择只改变 HTTP 宿主的发布模型。以下编译期与分层防护继续保留：

- `CreativeFlow.Infrastructure` 使用 Dapper.AOT 生成静态数据映射；
- MVC 与 SignalR 共用源生成的 `ApiJsonContext`；
- RabbitMQ 与 Redis 使用独立的 `IntegrationJsonContext`；
- 验证器、消息处理器和关键运行时服务继续显式注册；
- `CreativeFlow.Api` 不引用 Dapper，数据访问只能位于 Infrastructure。

完整决策及取舍见 [ADR 0005](adr/0005-mvc-controllers.md)。

## JSON 序列化

MVC 与 SignalR 使用各自的 JSON 配置入口：

```csharp
builder.Services.AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default));

builder.Services.Configure<JsonHubProtocolOptions>(options =>
    options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default));
```

两条链必须同时保留，避免同一 DTO 在 HTTP 与实时通道使用不同的命名、枚举或元数据策略。

新增跨 HTTP 或 SignalR 边界的 DTO 时，应同步登记到 `ApiJsonContext`。JIT 模式虽然允许反射
回退，但显式登记能让协议行为稳定，并避免不同交付通道悄悄分叉。

## Dapper.AOT

Dapper.AOT 通过 C# interceptor 工作，生成器只处理其所在编译单元中的调用点。因此：

- `Dapper.AOT` 包、`[module: DapperAot]` 和 interceptor 配置只位于 `CreativeFlow.Infrastructure`；
- 所有 Dapper 调用必须位于 `CreativeFlow.Infrastructure`；
- `CreativeFlow.Api` 与 `CreativeFlow.Application` 不得直接引用 Dapper；
- `DAP001;DAP013;DAP015;DAP016;DAP017` 继续作为构建错误。

以下调用会绕过生成映射或不受 Dapper.AOT 支持，禁止使用：

- 多映射 `Query<T1,T2,TResult>`；
- `QueryMultiple` / `QueryMultipleAsync`；
- 非泛型 `Query(typeof(Foo))`；
- `object`、`dynamic`、`DynamicParameters` 或泛型 `T` 参数；
- 元组返回（使用命名的 `readonly record struct`）；
- `SqlMapper.Settings` 与 `ITypeHandler`。

端口签名中的 `JsonTypeInfo<T>` 也继续保留，使缓存和消息序列化策略在接口层显式可见。

## 仍然保留的保守约束

下列约束不再是 Native AOT 发布的硬要求，但能减少反射、运行时扫描和环境差异，因此继续采用：

| 约束 | 当前原因 |
|---|---|
| 普通 `Hub` + `SendAsync` | 避免强类型 Hub 动态代理，保持 SignalR 协议显式 |
| JSON 协议 | 与 `ApiJsonContext` 共用契约，避免额外 MessagePack 协议面 |
| 显式注册 FluentValidation validator | 启动时即可验证关键注册，避免扫描范围漂移 |
| Serilog 代码配置 | sink 与格式在所有环境一致，不依赖程序集扫描 |
| 手写 Options 绑定 | 对缺失配置执行明确校验，避免生成全默认值对象 |
| Npgsql 静态 JSON 转换 | 数据库存储格式与应用契约保持显式 |

## 发布与验证

框架依赖发布：

```bash
dotnet publish src/CreativeFlow.Api -c Release
```

Linux 自包含单文件发布：

```bash
dotnet publish src/CreativeFlow.Api -c Release -r linux-x64 \
  --self-contained true /p:PublishSingleFile=true
```

标准验证：

```bash
dotnet build CreativeFlow.slnx
dotnet test CreativeFlow.slnx
```

路由迁移由 `ControllerRouteTests` 锁定；它逐条比较 HTTP 方法、URL 和端点名称，防止 Controller
拆分时出现兼容性回归。

需要检查 Dapper 是否生成 interceptor 时：

```bash
dotnet build src/CreativeFlow.Infrastructure \
  /p:EmitCompilerGeneratedFiles=true \
  /p:CompilerGeneratedFilesOutputPath=/tmp/creativeflow-dapper-generated

grep -c 'InterceptsLocation' /tmp/creativeflow-dapper-generated/Dapper.AOT.Analyzers/*/*.cs
```

生成目录必须位于项目外，避免生成文件被下一次构建重复编译。

## 历史说明

项目在迁移 MVC Controllers 前曾以 Minimal API 形式验证 Native AOT、SignalR、Redis backplane、
Npgsql 和 Dapper.AOT。那些结果可解释当前许多保守实现，但不再代表受支持的部署目标。当前唯一
受支持的 HTTP 宿主是 MVC/JIT。
