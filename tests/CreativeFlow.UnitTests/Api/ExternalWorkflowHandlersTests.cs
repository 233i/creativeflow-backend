using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using CreativeFlow.Api.Http;
using CreativeFlow.Api.Http.Handlers;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;
using CreativeFlow.Infrastructure.Options;
using CreativeFlow.Infrastructure.Security;

namespace CreativeFlow.UnitTests.Api;

/// <summary>验证外部 API 受理、重放与公开契约，未涉及的依赖调用直接失败。</summary>
public sealed class ExternalWorkflowHandlersTests
{
    /// <summary>重新发布后，普通重试、并发重复和队列已满分支都返回原运行及原版本。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(WorkflowRunInsertResult.Duplicate)]
    [InlineData(WorkflowRunInsertResult.QueueFull)]
    public async Task RepublishPreservesIdempotencyAcrossEveryReplayPath(WorkflowRunInsertResult? concurrentResult)
    {
        using var fixture = new Fixture();
        var first = Assert.IsType<ExternalWorkflowRunCreatedResponse>((await fixture.SubmitAsync()).Value);
        fixture.Republish();
        fixture.ConcurrentResult = concurrentResult;

        var response = await fixture.SubmitAsync();
        var replay = Assert.IsType<ExternalWorkflowRunCreatedResponse>(response.Value);

        Assert.Equal(202, response.StatusCode);
        Assert.Equal(first.RunId, replay.RunId);
        Assert.Equal(first.RequestId, replay.RequestId);
        Assert.Equal(1, replay.Version);
        Assert.Equal(1, fixture.InsertedRuns);
        var conflict = await fixture.SubmitAsync("different prompt");
        Assert.Equal(409, conflict.StatusCode);
        Assert.Equal("idempotency_conflict", Assert.IsType<ExternalApiErrorBody>(conflict.Value).Error.Code);
    }

    /// <summary>已经受理的请求不依赖当前契约或发布状态，停用仍阻止新任务。</summary>
    [Fact]
    public async Task AcceptedRunSurvivesChangedContractAndDisabledPublication()
    {
        using var fixture = new Fixture();
        var first = Assert.IsType<ExternalWorkflowRunCreatedResponse>((await fixture.SubmitAsync()).Value);
        fixture.Republish(changeInputKey: true);
        Assert.Equal(first.RunId, Assert.IsType<ExternalWorkflowRunCreatedResponse>((await fixture.SubmitAsync()).Value).RunId);

        fixture.Current = null;
        Assert.Equal(202, (await fixture.SubmitAsync()).StatusCode);
        Assert.Equal(404, (await fixture.SubmitAsync(idempotencyKey: "new-request")).StatusCode);
        fixture.Key = fixture.Key with { Enabled = false };
        Assert.Equal(401, (await fixture.SubmitAsync()).StatusCode);
    }

    /// <summary>目录提供后端补齐的约束，同时继续隐藏节点字段绑定。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogIncludesEffectiveConstraintsWithoutInternalBindings(bool legacySchema)
    {
        using var fixture = new Fixture();
        if (legacySchema)
        {
            var deployment = fixture.Current!.Deployment;
            var inputs = JsonNode.Parse(deployment.InputSchemaJson)!.AsArray();
            inputs[0]!.AsObject().Remove("constraints");
            fixture.Current = fixture.Current with { Deployment = deployment with { InputSchemaJson = inputs.ToJsonString() } };
        }
        var result = Assert.IsType<OkObjectResult>(await ExternalWorkflowHandlers.GetCatalogItemAsync(
            fixture.WorkflowId.ToString(), fixture.External, fixture.Options, TestContext.Current.CancellationToken));
        var catalog = Assert.IsType<ExternalWorkflowCatalogItemResponse>(result.Value);
        var input = catalog.Inputs!.AsArray()[0]!.AsObject();

        Assert.Equal(20_000, input["constraints"]!["maxLength"]!.GetValue<int>());
        Assert.False(input.ContainsKey("target"));
        Assert.False(catalog.Outputs!.AsArray()[0]!.AsObject().ContainsKey("nodeId"));
        var ownerView = WorkflowPublicationHandlers.ToResponse(fixture.Current!.Deployment);
        Assert.Equal(20_000, ownerView.Inputs!.AsArray()[0]!["constraints"]!["maxLength"]!.GetValue<int>());
    }

    /// <summary>通过真实 Handler 计算首个摘要，只替代存储和不会调用上游的依赖。</summary>
    private sealed class Fixture : IDisposable
    {
        /// <summary>符合发布要求且只暴露提示词的图。</summary>
        private const string Graph = """
            {"nodes":[{"id":"text","type":"text","data":{"text":"fixed"}},{"id":"gen","type":"imageGenerate","data":{"count":1}},{"id":"out","type":"imageOutput","data":{}}],
             "edges":[{"id":"e1","source":"text","sourceHandle":"text","target":"gen","targetHandle":"prompt"},{"id":"e2","source":"gen","sourceHandle":"image","target":"out","targetHandle":"image"}],
             "externalApi":{"schemaVersion":1,"inputs":[{"key":"prompt","label":"Prompt","type":"string","required":true,"target":{"nodeId":"text","field":"text"}}],"outputs":[{"key":"result","label":"Result","type":"image","nodeId":"out"}]}}
            """;
        /// <summary>首次成功保存的运行，重试不能覆盖它。</summary>
        private WorkflowRun? stored;
        /// <summary>各个不可变发布版本。</summary>
        private readonly Dictionary<WorkflowDeploymentId, PublishedWorkflowCatalogItem> deployments = [];
        /// <summary>真实 API Key 服务及仅在测试内使用的随机密钥。</summary>
        private readonly WorkflowApiKeyService keys;
        /// <summary>仅在该测试中使用的明文请求凭据。</summary>
        private readonly string plaintext;
        /// <summary>按真实请求流程计数的限流器。</summary>
        private readonly ExternalWorkflowRateLimiter limiter;
        /// <summary>运行仓储的受控替身。</summary>
        private readonly IWorkflowRunRepository runs;
        /// <summary>本用例不允许访问素材库。</summary>
        private readonly ICanvasAssetRepository assets = RepositoryProxy.Create<ICanvasAssetRepository>((name, _) => throw new NotSupportedException(name));
        /// <summary>本次测试的工作流。</summary>
        public WorkflowId WorkflowId { get; } = WorkflowId.New();
        /// <summary>外部 API 配置。</summary>
        public ExternalWorkflowApiOptions Options { get; } = new();
        /// <summary>用于模拟撤销的 API Key 元数据。</summary>
        public WorkflowApiKey Key { get; set; }
        /// <summary>当前公开版本，空值表示已停用。</summary>
        public PublishedWorkflowCatalogItem? Current { get; set; }
        /// <summary>模拟初始查找之后才可见的并发插入结果。</summary>
        public WorkflowRunInsertResult? ConcurrentResult { get; set; }
        /// <summary>实际新增的运行数量。</summary>
        public int InsertedRuns { get; private set; }
        /// <summary>发布仓储替身。</summary>
        public IWorkflowExternalApiRepository External { get; }

        /// <summary>装配真实鉴权与契约逻辑所需的最小存储行为。</summary>
        public Fixture()
        {
            keys = new(Options, new AiOptions { EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) });
            var material = keys.Generate();
            plaintext = material.Plaintext;
            Key = new(WorkflowApiKeyId.New(), WorkflowId, "test", material.Prefix, material.Hint, material.Hash,
                true, null, null, DateTimeOffset.UtcNow, null);
            limiter = new(Options);
            External = RepositoryProxy.Create<IWorkflowExternalApiRepository>((name, args) => name switch
            {
                nameof(IWorkflowExternalApiRepository.FindApiKeyByPrefixAsync) => Task.FromResult<WorkflowApiKey?>(Key),
                nameof(IWorkflowExternalApiRepository.TouchApiKeyAsync) => Task.CompletedTask,
                nameof(IWorkflowExternalApiRepository.FindPublishedAsync) => Task.FromResult(Current),
                nameof(IWorkflowExternalApiRepository.FindDeploymentAsync) => Task.FromResult(deployments.GetValueOrDefault((WorkflowDeploymentId)args![0]!)),
                nameof(IWorkflowExternalApiRepository.MarkExternalAssetsConsumedAsync) => Task.CompletedTask,
                _ => throw new NotSupportedException(name),
            });
            // 隐藏一次已有行以模拟并发创建完成前的初始读取，随后走真实 Handler 的冲突分支。
            var hideRead = false;
            runs = RepositoryProxy.Create<IWorkflowRunRepository>((name, args) =>
            {
                if (name == nameof(IWorkflowRunRepository.FindByIdempotencyAsync))
                {
                    if (ConcurrentResult is not null && !hideRead)
                    {
                        hideRead = true;
                        return Task.FromResult<WorkflowRun?>(null);
                    }
                    return Task.FromResult(stored?.IdempotencyKey == (string)args![1]! ? stored : null);
                }
                if (name == nameof(IWorkflowRunRepository.InsertExternalAsync))
                {
                    if (ConcurrentResult is { } result)
                    {
                        ConcurrentResult = null;
                        return Task.FromResult(result);
                    }
                    stored = (WorkflowRun)args![0]!;
                    InsertedRuns++;
                    return Task.FromResult(WorkflowRunInsertResult.Inserted);
                }
                throw new NotSupportedException(name);
            });
            Republish();
        }

        /// <summary>新增发布快照，保留原快照供重试恢复版本。</summary>
        public void Republish(bool changeInputKey = false)
        {
            var graph = JsonNode.Parse(Graph)!;
            if (changeInputKey) graph["externalApi"]!["inputs"]![0]!["key"] = "new_prompt";
            var contract = WorkflowExternalContractValidator.Validate(graph).Contract!;
            var now = DateTimeOffset.UtcNow;
            var deployment = new WorkflowDeployment(WorkflowDeploymentId.New(), WorkflowId, deployments.Count + 1, 1,
                graph.ToJsonString(), contract.InputSchemaJson, contract.OutputSchemaJson, "test",
                WorkflowDeploymentStatus.Published, 1, now, now, null);
            Current = new(deployment, "test", DeviceId.New());
            deployments.Add(deployment.Id, Current);
        }

        /// <summary>构造正常 Bearer 请求并调用真实外部受理 Handler。</summary>
        public async Task<ObjectResult> SubmitAsync(string prompt = "same prompt", string idempotencyKey = "same-request")
        {
            var http = new DefaultHttpContext();
            http.Request.Headers.Authorization = $"Bearer {plaintext}";
            http.Request.Headers["Idempotency-Key"] = idempotencyKey;
            return Assert.IsAssignableFrom<ObjectResult>(await ExternalWorkflowHandlers.CreateRunAsync(
                WorkflowId.ToString(), new(new JsonObject { ["prompt"] = prompt }), http,
                External, runs, keys, limiter, assets, Options, TestContext.Current.CancellationToken));
        }

        /// <summary>释放测试夹具拥有的外部工作流限流器。</summary>
        public void Dispose() => limiter.Dispose();
    }

    /// <summary>测试专用代理，只开放用例明确规定的端口调用。</summary>
    public class RepositoryProxy : DispatchProxy
    {
        /// <summary>本代理的受控调用处理函数。</summary>
        private Func<string, object?[]?, object?> handler = null!;
        /// <summary>将代理接口方法名和参数交给测试处理函数，以观察调用或返回预设结果。</summary>
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => handler(targetMethod!.Name, args);
        /// <summary>为指定端口创建不访问外部系统的测试替身。</summary>
        public static T Create<T>(Func<string, object?[]?, object?> handler) where T : class
        {
            var proxy = Create<T, RepositoryProxy>();
            ((RepositoryProxy)(object)proxy).handler = handler;
            return proxy;
        }
    }
}
