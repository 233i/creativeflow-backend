using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Npgsql;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Infrastructure.Options;
using CreativeFlow.Infrastructure.Persistence;
using CreativeFlow.Infrastructure.Persistence.Migrations;
using CreativeFlow.Infrastructure.Security;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>一次性迁移、授权边界和历史清理使用隔离 schema 复验。</summary>
public sealed class HttpCredentialConsolidationTests
{
    /// <summary>未显式配置隔离测试连接时不访问数据库。</summary>
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;
    /// <summary>沿用测试框架的取消信号。</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    /// <summary>只用于本次测试的随机加密主密钥。</summary>
    private static AesGcmApiKeyProtector Protector() => new(new AiOptions { EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) });
    /// <summary>包含字面量模板括号和固定认证头的旧查询配置。</summary>
    private static JsonObject Definition() => JsonNode.Parse("""
        {"method":"POST","url":"https://example.com/query","readOnly":true,
        "inputs":[{"key":"question","type":"string","required":true}],"outputs":[{"key":"text","type":"string","required":true}],
        "query":[],"headers":[],"body":{"object":{"q":{"input":"question"},"constant":{"constant":"{{question}}"}}},
        "extract":{"text":"/data/result"},"authentication":{"header":"Authorization","prefix":"Bearer "},"timeoutSeconds":30,"responseLimitBytes":1048576}
        """)!.AsObject();

    /// <summary>转换不解释旧常量为变量，也不放宽旧方法、参数映射和输出契约。</summary>
    [Fact]
    public void ConvertedTemplatesPreserveConstantsTypesAndExactRequestPolicy()
    {
        var data = HttpCredentialConsolidation.ConvertDefinition(Definition(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        Assert.Null(HttpRequestDefinition.Validate(data));
        var body = HttpRequestDefinition.ExpandJson(JsonNode.Parse(WorkflowV2Values.Text(data["http"]!["body"])), new JsonObject { ["question"] = "real" });
        Assert.Equal("real", WorkflowV2Values.Text(body!["q"])); Assert.Equal("{{question}}", WorkflowV2Values.Text(body["constant"]));
        var definition = Policy(data); var revision = new HttpCredentialRevision(Guid.NewGuid(), Guid.NewGuid(), definition.ToJsonString(), "digest", null);
        HttpCredentialPolicy.Require(revision, data);
        var original = data.DeepClone().AsObject(); data["title"] = "只改名字"; HttpCredentialPolicy.Require(revision, data);
        foreach (var change in new Action<JsonObject>[] {
            node => node["http"]!["url"] = "https://example.com/admin",
            node => node["http"]!["method"] = "DELETE",
            node => node["http"]!["body"] = "{\"q\":\"unapproved SQL\"}",
            node => node["extract"]!["text"] = "",
            node => node["fields"]![0]!["required"] = false,
        })
        { var tampered = original.DeepClone().AsObject(); change(tampered); Assert.Throws<WorkflowHttpException>(() => HttpCredentialPolicy.Require(revision, tampered)); }
    }

    /// <summary>有密钥和无密钥旧查询均保持主体授权，草稿和快照转换后只删除旧终态结果。</summary>
    [Theory(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    [InlineData(true)][InlineData(false)]
    public async Task MigratesDraftTemplatePublicationQueueAndPermissionsButDeletesOnlyLegacyHistory(bool authenticated)
    {
        await using var db = await ImageJobDatabase.CreateAsync(36); var protector = Protector(); var seed = await Seed(db, protector, authenticated);
        await Apply(db, protector);
        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM information_schema.tables WHERE table_schema=current_schema() AND table_name='workflow_http_operation'"));
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM workflow_http_credential_grant WHERE enabled"));
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM workflow_http_credential_grant_audit WHERE reason='Original scope'"));
        foreach (var (table, column) in new[] { ("workflow", "graph"), ("workflow_template", "graph"), ("workflow_deployment", "graph_snapshot"), ("workflow_run", "graph_snapshot") })
            Assert.Equal(0, await db.CountAsync($"SELECT count(*) FROM {table} WHERE {column} @> '{{\"nodes\":[{{\"type\":\"externalCall\"}}]}}'"));
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM workflow_run WHERE status='queued'"));
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM workflow_run WHERE status='succeeded'")); // unrelated new history survives
        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM workflow_node_run WHERE node_type='externalCall'"));
        Assert.Equal(1, await db.CountAsync("SELECT (summary->>'deletedHistoricalRuns')::bigint FROM workflow_http_consolidation_audit"));
        await db.ApplyRemainingAsync(37);
        var repository = new HttpCredentialRepository(db.Source);
        var migrated = (await repository.FindRevisionAsync(seed.Credential, seed.Revision, Ct))!;
        if (authenticated) Assert.Equal("Bearer synthetic-secret", WorkflowV2Values.Text(JsonNode.Parse(protector.Unprotect(migrated.Secret!))!["Authorization"]));
        else Assert.Null(migrated.Secret);
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM workflow_run_retired_idempotency WHERE idempotency_key='retired-query'"));
        Assert.True(await repository.CanUseAsync(seed.Credential, new("account", db.Account, db.Device), Ct));
        Assert.False(await repository.CanUseAsync(seed.Credential, new("account", db.OtherAccount, db.OtherDevice), Ct));
        Assert.True(await repository.IsRestrictedEndpointAsync("https://example.com/query", Ct));
        Assert.True(await repository.IsRestrictedEndpointAsync("https://example.com/QUERY", Ct));
        Assert.False(await repository.IsRestrictedEndpointAsync("https://other.example/query", Ct));
        var snapshot = JsonNode.Parse(await Read(db, "SELECT input_json::text FROM workflow_run WHERE status='queued'"))!.AsObject();
        Assert.Null(snapshot["external"]); Assert.Equal(db.Account.ToString(), WorkflowV2Values.Text(snapshot["http"]!["subject"]!["id"]));
        var graph = await Read(db, "SELECT graph_snapshot::text FROM workflow_run WHERE status='queued'");
        var plan = WorkflowV2GraphValidator.Validate(JsonNode.Parse(graph)).Plan!;
        var service = new HttpRequestService(repository, new HttpRequestFixtures.Transport(), new(true));
        await service.ValidateAsync(snapshot, db.Device, Ct);
        var captured = await service.CaptureAsync(plan, new("account", db.Account, db.Device), Ct);
        Assert.True(JsonNode.DeepEquals(snapshot["http"], captured));
        var anonymous = plan.Nodes.Single(n => n.Type == "httpRequest").Data.DeepClone().AsObject();
        anonymous.Remove("credentialId"); anonymous.Remove("credentialRevisionId");
        var withoutCredential = new WorkflowPlan(plan.Nodes.Select(node => node.Type == "httpRequest" ? node with { Data = anonymous } : node).ToArray());
        await Assert.ThrowsAsync<WorkflowHttpException>(() => service.CaptureAsync(withoutCredential, new("account", db.Account, db.Device), Ct));
        await db.SqlAsync($"UPDATE workflow_http_credential_grant SET enabled=false WHERE credential_id='{seed.Credential}'");
        await Assert.ThrowsAsync<WorkflowHttpException>(() => service.ValidateAsync(snapshot, db.Device, Ct));
    }

    /// <summary>活动任务、缺失引用、资源冲突或快照篡改都会回滚整个迁移。</summary>
    [Theory(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    [InlineData("running")][InlineData("missing-revision")][InlineData("credential-collision")][InlineData("changed-snapshot")]
    public async Task UnsafeMigrationRollsBackEverything(string failure)
    {
        await using var db = await ImageJobDatabase.CreateAsync(36); var protector = Protector(); var seed = await Seed(db, protector);
        if (failure == "running") await db.SqlAsync("UPDATE workflow_run SET status='running' WHERE status='queued'");
        if (failure == "missing-revision") await db.SqlAsync("UPDATE workflow SET graph=jsonb_set(graph,'{nodes,1,data,revisionId}','\"00000000-0000-0000-0000-000000000000\"')");
        if (failure == "credential-collision") await db.SqlAsync($"INSERT INTO workflow_http_credential(id,name) VALUES('{seed.Credential}','unrelated')");
        if (failure == "changed-snapshot") await db.SqlAsync("UPDATE workflow_deployment SET execution_snapshot=jsonb_set(execution_snapshot,'{external,references,http,digest}','\"tampered\"')");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Apply(db, protector));
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM workflow_http_operation"));
        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM workflow_http_credential_revision"));
        Assert.Equal(3, await db.CountAsync("SELECT count(*) FROM workflow_run"));
    }

    /// <summary>构造保留完整旧请求摘要的最小许可。</summary>
    private static JsonObject Policy(JsonObject data) => new()
    {
        ["origin"] = "https://example.com", ["headers"] = new JsonArray("Authorization"),
        ["requests"] = new JsonArray(new JsonObject { ["method"] = "POST", ["url"] = "https://example.com/query" }),
        ["requestDigest"] = HttpCredentialPolicy.RequestDigest(data),
    };

    /// <summary>用真实迁移顺序在一个事务内转换数据和退休旧表。</summary>
    private static async Task Apply(ImageJobDatabase db, AesGcmApiKeyProtector protector)
    {
        await using var connection = await db.Source.OpenConnectionAsync(Ct); await using var transaction = await connection.BeginTransactionAsync(Ct);
        await HttpCredentialConsolidation.ApplyAsync(connection, transaction, protector, Ct);
        using var reader = new StreamReader(typeof(MigrationRunner).Assembly.GetManifestResourceStream("CreativeFlow.Migrations.0037__consolidate_http_credentials.sql")!);
        await using var command = new NpgsqlCommand(await reader.ReadToEndAsync(Ct), connection, transaction); await command.ExecuteNonQueryAsync(Ct);
        await transaction.CommitAsync(Ct);
    }

    /// <summary>读取隔离 schema 内的单个 JSON 断言值。</summary>
    private static async Task<string> Read(ImageJobDatabase db, string sql)
    { await using var command = db.Source.CreateCommand(sql); return (string)(await command.ExecuteScalarAsync(Ct))!; }

    /// <summary>同时准备旧草稿、模板、发布、未启动队列、终态 API 结果和一条应保留的新历史。</summary>
    private static async Task<(Guid Credential, Guid Revision)> Seed(ImageJobDatabase db, AesGcmApiKeyProtector protector, bool authenticated = true)
    {
        var credential = Guid.NewGuid(); var revision = Guid.NewGuid(); var admin = Guid.NewGuid(); var workflow = Guid.NewGuid();
        var definition = Definition(); if (!authenticated) definition.Remove("authentication");
        var digest = HttpRequestDefinition.Digest(definition); var secret = protector.Protect("synthetic-secret");
        var graph = HttpRequestFixtures.Graph(); graph["nodes"]![1]!["type"] = "externalCall";
        graph["nodes"]![1]!["data"] = new JsonObject { ["fields"] = definition["inputs"]!.DeepClone(), ["outputFields"] = definition["outputs"]!.DeepClone(), ["operationId"] = credential.ToString(), ["revisionId"] = revision.ToString() };
        graph["edges"]![0]!["sourceHandle"] = "text";
        var resource = new JsonObject { ["references"] = new JsonObject { ["http"] = new JsonObject { ["operationId"] = credential.ToString(), ["revisionId"] = revision.ToString(), ["digest"] = digest } } };
        var publication = new JsonObject { ["schemaVersion"] = 1, ["models"] = new JsonObject(), ["external"] = resource.DeepClone() };
        resource["subject"] = new JsonObject { ["kind"] = "account", ["id"] = db.Account.ToString(), ["device"] = db.Device.ToString() };
        var snapshot = new JsonObject { ["schemaVersion"] = 1, ["inputs"] = new JsonObject { ["question"] = "synthetic" }, ["models"] = new JsonObject(), ["external"] = resource };
        var history = Guid.NewGuid(); var queue = Guid.NewGuid(); var deployment = Guid.NewGuid(); var apiKey = Guid.NewGuid();
        static string Json(JsonNode value) => value.ToJsonString().Replace("'", "''", StringComparison.Ordinal);
        await db.SqlAsync($$"""
            INSERT INTO workflow_http_operation(id,name) VALUES('{{credential}}','Original query');
            INSERT INTO workflow_http_revision(id,operation_id,definition_json,digest,secret_cipher,secret_nonce,secret_tag,secret_hint,created_by)
            VALUES('{{revision}}','{{credential}}','{{Json(definition)}}','{{digest}}',decode('{{Convert.ToHexString(secret.Cipher)}}','hex'),decode('{{Convert.ToHexString(secret.Nonce)}}','hex'),decode('{{Convert.ToHexString(secret.Tag)}}','hex'),'saved','{{admin}}');
            UPDATE workflow_http_operation SET current_revision='{{revision}}';
            INSERT INTO workflow_http_grant VALUES('{{credential}}','account','{{db.Account}}',true,now());
            INSERT INTO workflow_http_grant_audit VALUES('{{Guid.NewGuid()}}','{{credential}}','account','{{db.Account}}','{{admin}}',true,'Original scope',now());
            INSERT INTO workflow(workflow_id,device_id,name,graph,created_at,updated_at) VALUES('{{workflow}}','{{db.Device}}','legacy','{{Json(graph)}}',now(),now());
            INSERT INTO workflow_template(template_id,name,graph,created_at,updated_at) VALUES('{{Guid.NewGuid()}}','legacy template','{{Json(graph)}}',now(),now());
            INSERT INTO workflow_deployment(deployment_id,workflow_id,version,schema_version,graph_snapshot,input_schema,output_schema,execution_snapshot,status,created_at,published_at)
            VALUES('{{deployment}}','{{workflow}}',1,1,'{{Json(graph)}}','{{Json(graph["nodes"]![0]!["data"]!["fields"]!)}}','{{Json(graph["nodes"]![2]!["data"]!["fields"]!)}}','{{Json(publication)}}','disabled',now(),now());
            INSERT INTO workflow_api_key(api_key_id,workflow_id,name,key_prefix,key_hint,key_hash,created_at)
            VALUES('{{apiKey}}','{{workflow}}','Retired query test','synthetic','test',decode('{{new string('0',64)}}','hex'),now());
            INSERT INTO workflow_run(run_id,workflow_id,device_id,graph_snapshot,input_json,status,created_at,updated_at) VALUES
            ('{{history}}','{{workflow}}','{{db.Device}}','{{Json(graph)}}','{{Json(snapshot)}}','succeeded',now(),now()),
            ('{{queue}}','{{workflow}}','{{db.Device}}','{{Json(graph)}}','{{Json(snapshot)}}','queued',now(),now()),
            ('{{Guid.NewGuid()}}','{{workflow}}','{{db.Device}}','{{Json(HttpRequestFixtures.Graph())}}','{}','succeeded',now(),now());
            INSERT INTO workflow_node_run(run_id,node_id,node_type,status,output) VALUES
            ('{{history}}','http','externalCall','succeeded',jsonb_build_object('schemaVersion',1,'fields',jsonb_build_object('text','old result'))),
            ('{{queue}}','http','externalCall','queued',NULL);
            UPDATE workflow_run SET trigger_type='api',deployment_id='{{deployment}}',api_key_id='{{apiKey}}',request_id='old-query',
                idempotency_key='retired-query',request_hash=decode('{{new string('0',64)}}','hex') WHERE run_id='{{history}}';
            """);
        if (!authenticated) await db.SqlAsync("UPDATE workflow_http_revision SET secret_cipher=NULL,secret_nonce=NULL,secret_tag=NULL,secret_hint=NULL");
        return (credential, revision);
    }
}
