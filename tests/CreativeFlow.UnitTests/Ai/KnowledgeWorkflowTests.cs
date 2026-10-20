using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Knowledge;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Infrastructure.Persistence;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>知识图强类型、权限撤销、固定索引及证据内容校验；只使用随机测试数据库。</summary>
[Collection("Knowledge index database lock")]
public sealed class KnowledgeWorkflowTests
{
    /// <summary>禁止在未指定隔离库时访问开发配置。</summary>
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;
    /// <summary>测试取消令牌。</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>端口名、证据类型、默认值、资源参数与依赖不能绕过知识节点安全边界。</summary>
    [Theory]
    [InlineData("startType")][InlineData("object")][InlineData("default")][InlineData("field")]
    [InlineData("noContext")][InlineData("url")][InlineData("threshold")][InlineData("topK")]
    public void GraphRejectsForgedKnowledge(string mutation)
    {
        var graph = WorkflowV2Fixtures.KnowledgeGraph(Guid.NewGuid(), Guid.NewGuid());
        var answer = graph["nodes"]![1]!["data"]!; var retrieve = graph["nodes"]![3]!["data"]!;
        switch (mutation)
        {
            case "startType": graph["nodes"]![2]!["data"]!["fields"]![0]!["type"] = "contextChunks"; break;
            case "object": answer["fields"]![1]!["type"] = "object"; break;
            case "default": answer["fields"]![1]!["default"] = new JsonObject(); break;
            case "field": answer["fields"]![0]!["key"] = "other"; break;
            case "noContext": graph["edges"]!.AsArray().RemoveAt(2); break;
            case "url": retrieve["url"] = "http://169.254.169.254"; break;
            case "threshold": retrieve["minScore"] = 1.1; break;
            case "topK": retrieve["topK"] = 33; break;
        }
        Assert.False(WorkflowV2GraphValidator.Validate(graph).IsValid);
    }

    /// <summary>未选资源的草稿可保存，但对象或数字不能冒充可渲染的资源标识。</summary>
    [Fact]
    public void DraftResourceIdentifiersRemainStrings()
    {
        var graph = WorkflowV2Fixtures.KnowledgeGraph(Guid.NewGuid(), Guid.NewGuid());
        graph["nodes"]![3]!["data"]!["libraryId"] = "";
        Assert.True(WorkflowV2GraphValidator.Validate(graph, false).IsValid);
        graph["nodes"]![3]!["data"]!["libraryId"] = new JsonObject();
        Assert.False(WorkflowV2GraphValidator.Validate(graph, false).IsValid);
    }

    /// <summary>固定知识版本不随激活切换；模型变更拒绝新检索但不会为成功证据复用额外调用模型。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task FixedVersionDoesNotFollowActivePointerAndNewSearchChecksModel()
    {
        await using var f = await KnowledgeFixture.CreateAsync();
        var (service, subject, _) = await AuthorizeAsync(f);
        var old = await f.ImportAsync(); await f.Processor.ProcessNextAsync(Ct); await f.Repository.ActivateAsync(f.Library.Id, old.Id, Ct);
        var plan = WorkflowV2GraphValidator.Validate(WorkflowV2Fixtures.KnowledgeGraph(f.Library.Id, old.Id)).Plan!;
        var snapshot = new JsonObject { ["knowledge"] = await service.CaptureAsync(plan, subject, Ct) };
        var newer = await f.ImportAsync("新版本合成资料：保修三年。"); await f.Processor.ProcessNextAsync(Ct); await f.Repository.ActivateAsync(f.Library.Id, newer.Id, Ct);
        var result = await service.RetrieveAsync(plan.Nodes.Single(node => node.Type == "knowledgeRetrieve"), snapshot, "保修？", Ct);
        Assert.Equal(old.Id.ToString(), result["context"]!["versionId"]!.GetValue<string>());
        Assert.Contains("两年", result["context"]!["hits"]![0]!["text"]!.GetValue<string>());
        var calls = f.Model.Calls; f.Model.Revision++;
        await service.ValidateAsync(snapshot, subject.Device, Ct); Assert.Equal(calls, f.Model.Calls);
        await Assert.ThrowsAsync<WorkflowV2Exception>(() => service.RetrieveAsync(plan.Nodes.Single(node => node.Type == "knowledgeRetrieve"), snapshot, "保修？", Ct));
        Assert.Equal(calls, f.Model.Calls);
    }

    /// <summary>账号禁用、设备解绑及授权撤销都即时使已捕获的资源快照失效。</summary>
    [Theory(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    [InlineData("account")][InlineData("device")][InlineData("grant")]
    public async Task IdentityChangesInvalidateSnapshots(string mutation)
    {
        await using var f = await KnowledgeFixture.CreateAsync(); var (service, subject, admin) = await AuthorizeAsync(f);
        var version = await f.ImportAsync(); await f.Processor.ProcessNextAsync(Ct);
        var plan = WorkflowV2GraphValidator.Validate(WorkflowV2Fixtures.KnowledgeGraph(f.Library.Id, version.Id)).Plan!;
        var snapshot = new JsonObject { ["knowledge"] = await service.CaptureAsync(plan, subject, Ct) };
        if (mutation == "account") await f.Database.SqlAsync($"UPDATE creativeflow_account SET is_enabled = false WHERE account_id = '{subject.Id}'");
        if (mutation == "device") await f.Database.SqlAsync($"DELETE FROM creativeflow_account_device WHERE device_id = '{subject.Device}'");
        if (mutation == "grant") await new KnowledgeAccessRepository(f.Database.Source).SetGrantAsync(f.Library.Id, subject.Id, admin, false, "revoke", Ct);
        await Assert.ThrowsAsync<WorkflowV2Exception>(() => service.ValidateAsync(snapshot, subject.Device, Ct));
        Assert.Empty(await service.CatalogAsync(subject, Ct)); Assert.Equal(1, f.Model.Calls);
    }

    /// <summary>改写检索词不放宽证据校验，伪造正文、资源、节点或位置及无效检索词均在聊天前被拒绝。</summary>
    [Theory(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    [InlineData("text")][InlineData("emptyQuery")][InlineData("longQuery")][InlineData("version")]
    [InlineData("library")][InlineData("node")][InlineData("document")][InlineData("startLine")][InlineData("endLine")][InlineData("chunk")]
    public async Task AnswerCannotConsumeForgedContext(string mutation)
    {
        await using var f = await KnowledgeFixture.CreateAsync(); var (service, subject, _) = await AuthorizeAsync(f);
        var version = await f.ImportAsync(); await f.Processor.ProcessNextAsync(Ct);
        var plan = WorkflowV2GraphValidator.Validate(WorkflowV2Fixtures.KnowledgeGraph(f.Library.Id, version.Id)).Plan!;
        var models = new WorkflowV2Fixtures.Models();
        var snapshot = new JsonObject { ["knowledge"] = await service.CaptureAsync(plan, subject, Ct), ["models"] = await models.CaptureAsync(plan, Ct) };
        var result = await service.RetrieveAsync(plan.Nodes.Single(node => node.Type == "knowledgeRetrieve"), snapshot, "question", Ct);
        var context = result["context"]!.DeepClone();
        if (mutation == "text") context["hits"]![0]!["text"] = "fake evidence";
        if (mutation == "emptyQuery") context["question"] = " \n ";
        if (mutation == "longQuery") context["question"] = new string('词', 2001);
        if (mutation == "version") context["versionId"] = Guid.NewGuid().ToString();
        if (mutation == "library") context["libraryId"] = Guid.NewGuid().ToString();
        if (mutation == "node") context["retrievalNodeId"] = "not-captured";
        if (mutation == "document") context["hits"]![0]!["document"] = "other.md";
        if (mutation == "startLine") context["hits"]![0]!["startLine"] = 2;
        if (mutation == "endLine") context["hits"]![0]!["endLine"] = 100;
        if (mutation == "chunk") context["hits"]![0]!["id"] = Guid.NewGuid().ToString();
        await Assert.ThrowsAsync<WorkflowV2Exception>(() => service.AnswerAsync(plan.Nodes.Single(node => node.Type == "knowledgeAnswer"), snapshot,
            new JsonObject { ["question"] = "与检索词不同的原始问题", ["context"] = context }, models, Ct));
        Assert.Equal(0, models.Calls);
    }

    /// <summary>原始问题和检索词分别进入问答与向量请求；无命中仍短路，模型调用中撤权仍禁止交付。</summary>
    [Theory(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    [InlineData(false)][InlineData(true)]
    public async Task AnswerUsesOriginalQuestionAndKeepsRewrittenQuery(bool hasEvidence)
    {
        await using var f = await KnowledgeFixture.CreateAsync(); var (service, subject, admin) = await AuthorizeAsync(f);
        var version = await f.ImportAsync(); await f.Processor.ProcessNextAsync(Ct);
        if (!hasEvidence) f.Storage.Points[version.Id].Clear();
        var plan = WorkflowV2GraphValidator.Validate(WorkflowV2Fixtures.KnowledgeGraph(f.Library.Id, version.Id)).Plan!;
        var models = new WorkflowV2Fixtures.Models();
        var snapshot = new JsonObject { ["knowledge"] = await service.CaptureAsync(plan, subject, Ct), ["models"] = await models.CaptureAsync(plan, Ct) };
        const string originalQuestion = "这份合成手册里的产品保修几年？";
        const string retrievalQuery = "保修 期限";
        var retrieved = await service.RetrieveAsync(plan.Nodes.Single(node => node.Type == "knowledgeRetrieve"), snapshot, retrievalQuery, Ct);
        Assert.Equal(retrievalQuery, f.Model.LastQuery);
        Assert.Equal(retrievalQuery, retrieved["context"]!["question"]!.GetValue<string>());
        var input = new JsonObject { ["question"] = originalQuestion, ["context"] = retrieved["context"]!.DeepClone() };
        var answerNode = plan.Nodes.Single(node => node.Type == "knowledgeAnswer");
        var result = await service.AnswerAsync(answerNode, snapshot, input, models, Ct);
        Assert.Equal(hasEvidence, result["answer"]!["grounded"]!.GetValue<bool>());
        Assert.Equal(hasEvidence ? 1 : 0, models.Calls);
        if (!hasEvidence) { Assert.Empty(result["answer"]!["citations"]!.AsArray()); return; }
        var prompt = JsonNode.Parse(models.Prompt!)!;
        Assert.Equal(originalQuestion, prompt["question"]!.GetValue<string>());
        Assert.Equal(retrievalQuery, prompt["retrievalQuery"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(retrieved["context"]!["hits"], prompt["untrustedSources"]));
        var citation = result["answer"]!["citations"]![0]!;
        Assert.Equal(version.Id.ToString(), citation["versionId"]!.GetValue<string>());
        Assert.Equal(f.Library.Id.ToString(), citation["libraryId"]!.GetValue<string>());
        models.BeforeAnswer = () => new KnowledgeAccessRepository(f.Database.Source).SetGrantAsync(f.Library.Id, subject.Id, admin, false, "revoke", Ct);
        await Assert.ThrowsAsync<WorkflowV2Exception>(() => service.AnswerAsync(answerNode, snapshot, input, models, Ct));
        Assert.Equal(2, models.Calls);
        await Assert.ThrowsAsync<WorkflowV2Exception>(() => service.AnswerAsync(answerNode, snapshot, input, models, Ct));
        Assert.Equal(2, models.Calls);
    }

    /// <summary>取消相等约束后仍独立校验原始问题，空白或超长输入不触发聊天请求。</summary>
    [Theory(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    [InlineData(0)][InlineData(1)][InlineData(2001)]
    public async Task OriginalQuestionStillHasItsOwnValidation(int length)
    {
        await using var f = await KnowledgeFixture.CreateAsync(); var (service, subject, _) = await AuthorizeAsync(f);
        var version = await f.ImportAsync(); await f.Processor.ProcessNextAsync(Ct);
        var plan = WorkflowV2GraphValidator.Validate(WorkflowV2Fixtures.KnowledgeGraph(f.Library.Id, version.Id)).Plan!;
        var models = new WorkflowV2Fixtures.Models();
        var snapshot = new JsonObject { ["knowledge"] = await service.CaptureAsync(plan, subject, Ct), ["models"] = await models.CaptureAsync(plan, Ct) };
        var retrieved = await service.RetrieveAsync(plan.Nodes.Single(node => node.Type == "knowledgeRetrieve"), snapshot, "有效检索词", Ct);
        var input = new JsonObject { ["question"] = length == 1 ? " " : new string('问', length), ["context"] = retrieved["context"]!.DeepClone() };
        var error = await Assert.ThrowsAsync<WorkflowV2Exception>(() => service.AnswerAsync(plan.Nodes.Single(node => node.Type == "knowledgeAnswer"), snapshot, input, models, Ct));
        Assert.Contains("原始问题", error.Message); Assert.Equal(0, models.Calls);
    }

    /// <summary>相邻片段属于同一次已授权检索，可以作为答案依据；未知角色不能伪装成合法引用。</summary>
    [Fact]
    public void AnswerValueAcceptsSupportingCitationAndRejectsUnknownRole()
    {
        var citation = new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["document"] = "manual.md",
            ["startLine"] = 2, ["endLine"] = 3, ["text"] = "合成手册规定保修两年。", ["role"] = "supporting",
            ["libraryId"] = Guid.NewGuid().ToString(), ["versionId"] = Guid.NewGuid().ToString() };
        var answer = new JsonObject { ["text"] = "保修两年。", ["grounded"] = true, ["citations"] = new JsonArray(citation) };
        Assert.True(KnowledgeWorkflowValues.Matches("answer", answer));
        citation["role"] = "foreign";
        Assert.False(KnowledgeWorkflowValues.Matches("answer", answer));
    }

    /// <summary>模型引用本次检索的相邻正文能够交付，引用其他片段仍失败关闭且不自动重试。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task AnswerAcceptsVerifiedAdjacentEvidenceButRejectsForeignCitation()
    {
        await using var f = await KnowledgeFixture.CreateAsync(); var (service, subject, _) = await AuthorizeAsync(f);
        var version = await f.ImportAsync(string.Join("\n\n", Enumerable.Range(1, 4).Select(index => $"## 合成章节 {index}\n{new string('文', 180)}")));
        await f.Processor.ProcessNextAsync(Ct);
        var graph = WorkflowV2Fixtures.KnowledgeGraph(f.Library.Id, version.Id); graph["nodes"]![3]!["data"]!["topK"] = 1;
        var plan = WorkflowV2GraphValidator.Validate(graph).Plan!;
        var models = new WorkflowV2Fixtures.Models();
        var snapshot = new JsonObject { ["knowledge"] = await service.CaptureAsync(plan, subject, Ct), ["models"] = await models.CaptureAsync(plan, Ct) };
        var retrieved = await service.RetrieveAsync(plan.Nodes.Single(node => node.Type == "knowledgeRetrieve"), snapshot, "question", Ct);
        var context = retrieved["context"]!.AsObject();
        var adjacent = context["hits"]!.AsArray().First(hit => hit?["role"]?.GetValue<string>() == "supporting")!;
        var id = adjacent["id"]!.GetValue<string>();
        models.AnswerResponse = new JsonObject { ["text"] = "合成答案。", ["citations"] = new JsonArray(JsonValue.Create(id)) }.ToJsonString();
        var input = new JsonObject { ["question"] = "question", ["context"] = context.DeepClone() };
        var result = await service.AnswerAsync(plan.Nodes.Single(node => node.Type == "knowledgeAnswer"), snapshot, input, models, Ct);
        Assert.Equal(id, result["answer"]!["citations"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("supporting", result["answer"]!["citations"]![0]!["role"]!.GetValue<string>());
        Assert.Equal(1, models.Calls);
        models.AnswerResponse = "{\"text\":\"foreign\",\"citations\":[\"not-in-this-retrieval\"]}";
        await Assert.ThrowsAsync<WorkflowV2Exception>(() => service.AnswerAsync(plan.Nodes.Single(node => node.Type == "knowledgeAnswer"), snapshot, input, models, Ct));
        Assert.Equal(2, models.Calls);
    }

    /// <summary>大量中文片段不再受十八千字节裁剪，尾部和前置正文全部交付且问答输入可绑定。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task CompleteContextIsNotClippedBySerializedBytes()
    {
        await using var f = await KnowledgeFixture.CreateAsync(); var (service, subject, _) = await AuthorizeAsync(f);
        var input = new KnowledgeImportRequest([new("long.md", string.Concat(new string('中', 6400), "\nsmall尾部"))], 1600, 0);
        var version = await f.Service.ImportAsync(f.Library.Id, input with { PreviewDigest = KnowledgeTextParser.Preview(input).Digest }, Ct);
        await f.Processor.ProcessNextAsync(Ct);
        var graph = WorkflowV2Fixtures.KnowledgeGraph(f.Library.Id, version.Id); graph["nodes"]![3]!["data"]!["topK"] = 8;
        var plan = WorkflowV2GraphValidator.Validate(graph).Plan!;
        var snapshot = new JsonObject { ["knowledge"] = await service.CaptureAsync(plan, subject, Ct) };
        var result = await service.RetrieveAsync(plan.Nodes.Single(node => node.Type == "knowledgeRetrieve"), snapshot, new string('问', 1000), Ct);
        Assert.False(result["context"]!["truncated"]!.GetValue<bool>());
        Assert.Equal(8, result["context"]!["maxContextChunks"]!.GetValue<int>());
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(result.ToJsonString()) > 32768); // 超过历史 32 KiB 限制，不依赖 HTTP 当前的 1 MiB 预算。
        Assert.Equal(6400, result["context"]!["hits"]!.AsArray().Sum(hit => hit!["text"]!.GetValue<string>().Count(character => character == '中')));
        Assert.Contains(result["context"]!["hits"]!.AsArray(), hit => hit!["text"]!.GetValue<string>().Contains("small尾部", StringComparison.Ordinal));
        var bound = new JsonObject { ["question"] = new string('问', 1000), ["context"] = result["context"]!.DeepClone() };
        Assert.Null(WorkflowV2Values.Bind(KnowledgeWorkflowValues.Inputs("knowledgeAnswer"), bound, out _));
    }

    /// <summary>主命中与相邻片段合计计数；调大上限补齐相邻内容，始终优先保留全部主命中。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task NodeLimitCountsNeighborsAndPreservesPrimaryPriority()
    {
        await using var f = await KnowledgeFixture.CreateAsync(); var (service, subject, _) = await AuthorizeAsync(f);
        var version = await f.ImportAsync(new string('文', 16 * 200)); await f.Processor.ProcessNextAsync(Ct);
        var chunks = (await f.Repository.ReadChunksAsync(f.Library.Id, version.Id, null, null, 0, 32, Ct)).OrderBy(chunk => chunk.Ordinal).ToArray();
        Assert.Equal(16, chunks.Length);
        var seeds = new[] { 2, 6, 10, 14 }.Select(index => chunks[index].Id).ToArray();
        f.Storage.Points[version.Id].Clear();
        f.Storage.Points[version.Id].UnionWith(seeds.Concat(chunks.Select(chunk => chunk.Id)));
        var graph = WorkflowV2Fixtures.KnowledgeGraph(f.Library.Id, version.Id);
        var plan = WorkflowV2GraphValidator.Validate(graph).Plan!;
        var node = plan.Nodes.Single(node => node.Type == "knowledgeRetrieve");
        foreach (var limit in new[] { 8, 12 })
        {
            if (limit == 12) node.Data["maxContextChunks"] = limit;
            // 每次显式修改策略代表一次新提交，必须冻结该策略，不能篡改已有运行快照。
            var snapshot = new JsonObject { ["knowledge"] = await service.CaptureAsync(plan, subject, Ct) };
            var result = await service.RetrieveAsync(node, snapshot, "合成问题", Ct);
            var context = result["context"]!; var hits = context["hits"]!.AsArray();
            Assert.Equal(limit, hits.Count);
            Assert.Equal(seeds.Select(id => id.ToString()), hits.Take(4).Select(hit => hit!["id"]!.GetValue<string>()));
            Assert.All(hits.Skip(4), hit => Assert.Equal("supporting", hit!["role"]!.GetValue<string>()));
            Assert.Equal(limit, hits.Select(hit => hit!["id"]!.GetValue<string>()).Distinct().Count());
            Assert.Equal(limit < 12, context["truncated"]!.GetValue<bool>());
            if (limit == 8) Assert.Equal(4, context["omittedCount"]!.GetValue<int>());
        }
    }

    /// <summary>节点数量设置控制完整片段交付，长中文证据可进入问答并带全部引用恢复，不重新调用模型。</summary>
    [Theory(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    [InlineData(1)][InlineData(8)][InlineData(12)][InlineData(32)]
    public async Task ConfiguredChunkLimitSurvivesAnswerAndCheckpoint(int limit)
    {
        await using var f = await KnowledgeFixture.CreateAsync(new() { MaxDocumentBytes = 1048576, MaxVersionBytes = 1048576 });
        var (service, subject, _) = await AuthorizeAsync(f);
        var import = new KnowledgeImportRequest([new("synthetic-long.md", new string('文', 32 * 1600))], 1600, 0);
        var version = await f.Service.ImportAsync(f.Library.Id, import with { PreviewDigest = f.Service.Preview(import).Digest }, Ct);
        await f.Processor.ProcessNextAsync(Ct);
        var graph = WorkflowV2Fixtures.KnowledgeGraph(f.Library.Id, version.Id);
        graph["nodes"]![3]!["data"]!["topK"] = 32;
        graph["nodes"]![3]!["data"]!["maxContextChunks"] = limit;
        var validation = WorkflowV2GraphValidator.Validate(graph);
        Assert.True(validation.IsValid, string.Join(";", validation.Errors));
        var plan = validation.Plan!;
        var retrieve = plan.Nodes.Single(node => node.Type == "knowledgeRetrieve");
        var answer = plan.Nodes.Single(node => node.Type == "knowledgeAnswer");
        var models = new WorkflowV2Fixtures.Models();
        var snapshot = new JsonObject { ["knowledge"] = await service.CaptureAsync(plan, subject, Ct), ["models"] = await models.CaptureAsync(plan, Ct) };
        var result = await service.RetrieveAsync(retrieve, snapshot, "合成问题", Ct);
        var context = result["context"]!.AsObject();
        var hits = context["hits"]!.AsArray();
        Assert.Equal(limit, hits.Count);
        Assert.All(hits, hit => Assert.Equal(1600, hit!["text"]!.GetValue<string>().Length));
        Assert.Equal(limit < 32, context["truncated"]!.GetValue<bool>());
        if (limit < 32)
        {
            Assert.Equal(32 - limit, context["omittedCount"]!.GetValue<int>());
            Assert.Equal("chunk_limit", context["truncationReason"]!.GetValue<string>());
        }
        else Assert.False(context.ContainsKey("omittedCount"));
        Assert.True(WorkflowV2Values.TryRestore(retrieve, WorkflowV2Values.Output(result).ToJsonString(), out var restored));
        Assert.True(JsonNode.DeepEquals(result, restored));
        var input = new JsonObject { ["question"] = "合成问题", ["context"] = restored["context"]!.DeepClone() };
        Assert.Null(WorkflowV2Values.Bind(KnowledgeWorkflowValues.Inputs("knowledgeAnswer"), input, out var bound));
        models.AnswerResponse = new JsonObject { ["text"] = new string('答', 8000), ["citations"] = new JsonArray(hits.Select(hit => hit!["id"]!.DeepClone()).ToArray()) }.ToJsonString();
        var answerResult = await service.AnswerAsync(answer, snapshot, bound, models, Ct);
        Assert.True(JsonNode.DeepEquals(hits, JsonNode.Parse(models.Prompt!)!["untrustedSources"]));
        Assert.Equal(limit, answerResult["answer"]!["citations"]!.AsArray().Count);
        Assert.True(WorkflowV2Values.TryRestore(answer, WorkflowV2Values.Output(answerResult).ToJsonString(), out _));
        Assert.True(WorkflowV2Values.TryRestore(plan.Nodes.Single(node => node.Type == "end"), WorkflowV2Values.Output(answerResult).ToJsonString(), out _));
        Assert.Equal(1, models.Calls);
    }

    /// <summary>仅在随机测试 schema 创建管理员并授权本测试账号。</summary>
    private static async Task<(KnowledgeWorkflowService Service, KnowledgeSubject Subject, Guid Admin)> AuthorizeAsync(KnowledgeFixture f)
    {
        var admin = Guid.NewGuid();
        await f.Database.SqlAsync($"INSERT INTO creativeflow_admin(admin_id,username,password_hash,must_change_password,created_at,updated_at) VALUES ('{admin}','rag-test','synthetic-hash-not-usable',false,now(),now())");
        var access = new KnowledgeAccessRepository(f.Database.Source);
        await access.SetGrantAsync(f.Library.Id, f.Database.Account, admin, true, "synthetic", Ct);
return (new(f.Repository, access, f.Model, f.Storage, new(true), f.Retrieval), new("account", f.Database.Account, f.Database.Device), admin);
    }
}
