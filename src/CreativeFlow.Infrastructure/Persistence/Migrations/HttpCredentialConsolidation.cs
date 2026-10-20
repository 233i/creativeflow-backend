using System.Text.Json.Nodes;
using Npgsql;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Domain.Ai;
using static CreativeFlow.Application.Features.Workflow.WorkflowV2Values;

namespace CreativeFlow.Infrastructure.Persistence.Migrations;

/// <summary>一次性将固定查询迁到 HTTP 节点；不保留旧运行引擎。所有写入与迁移版本记录共用事务。</summary>
public static class HttpCredentialConsolidation
{
    /// <summary>迁移期间锁定引用表；活动旧任务必须先停机排空，不能重发结果不明确的请求。</summary>
    public static async Task ApplyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, IApiKeyProtector? protector, CancellationToken ct)
    {
        await Execute("LOCK TABLE workflow, workflow_template, workflow_deployment, workflow_run, workflow_node_run, workflow_http_operation, workflow_http_revision, workflow_http_grant, workflow_http_grant_audit, workflow_http_credential, workflow_http_credential_revision IN SHARE ROW EXCLUSIVE MODE");
        if ((await Rows("SELECT to_jsonb(o)::text FROM workflow_http_operation o JOIN workflow_http_credential c ON c.id=o.id")).Count != 0)
            throw new InvalidOperationException("HTTP consolidation: operation and credential IDs collide; refusing to merge their permissions.");
        foreach (var existing in await Rows("SELECT definition_json::text FROM workflow_http_credential_revision"))
            if (!HttpCredentialPolicy.ValidRequests(existing["requests"], Text(existing["origin"])))
                throw new InvalidOperationException("HTTP consolidation: an existing credential has no exact request scopes. Assign reviewed method/path scopes before migration; origin-wide grants cannot be inferred safely.");
        var runs = await Rows("SELECT to_jsonb(r)::text FROM workflow_run r WHERE graph_snapshot @> '{\"nodes\":[{\"type\":\"externalCall\"}]}' OR input_json ? 'external' OR EXISTS(SELECT 1 FROM workflow_node_run n WHERE n.run_id=r.run_id AND n.node_type='externalCall')");
        if (runs.Any(run => Text(run["status"]) == "running")) throw new InvalidOperationException("HTTP consolidation: drain running externalCall tasks before migration. No data was changed.");
        var revisions = await Rows("SELECT (to_jsonb(r) - 'secret_cipher' - 'secret_nonce' - 'secret_tag' - 'secret_hint')::text FROM workflow_http_revision r ORDER BY created_at,id");
        var converted = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var definitions = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var row in revisions)
        {
            var id = Text(row["id"]); var operation = Text(row["operation_id"]);
            var definition = row["definition_json"]!.AsObject();
            var data = ConvertDefinition(definition, operation, id);
            var headers = new JsonObject();
            var names = new JsonArray();
            if (definition["authentication"] is JsonObject auth)
            {
                if (protector is null) throw new InvalidOperationException("HTTP consolidation requires the configured encryption key to migrate credentials.");
                await using var command = Command("SELECT secret_cipher,secret_nonce,secret_tag,secret_hint FROM workflow_http_revision WHERE id=@id", ("id", Guid.Parse(id)));
                await using var reader = await command.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct);
                if (reader.IsDBNull(0)) throw new InvalidOperationException($"HTTP consolidation: credential secret missing for revision {id}.");
                var secret = new ApiKeySecret(reader.GetFieldValue<byte[]>(0), reader.GetFieldValue<byte[]>(1), reader.GetFieldValue<byte[]>(2), reader.GetString(3));
                headers[Text(auth["header"])] = Text(auth["prefix"]) + protector.Unprotect(secret);
                names.Add((JsonNode)JsonValue.Create(Text(auth["header"]))!);
            }
            // 历史普通头中的敏感常量也转入密文，不能复制到可导出的节点图中。
            foreach (var oldHeader in definition["headers"]!.AsArray().OfType<JsonObject>().Where(header => HttpRequestDefinition.SensitiveHeader(Text(header["name"]))))
            {
                if (oldHeader["value"] is not JsonObject mapping || !mapping.TryGetPropertyValue("constant", out var constant) || constant is not JsonValue)
                    throw new InvalidOperationException($"HTTP consolidation: dynamic authentication header in revision {id} requires explicit credential configuration.");
                headers[Text(oldHeader["name"])] = Scalar(constant); names.Add((JsonNode)JsonValue.Create(Text(oldHeader["name"]))!);
            }
            var metadata = new JsonObject
            {
                ["origin"] = HttpRequestDefinition.Origin(Text(definition["url"])), ["headers"] = names,
                ["requests"] = new JsonArray(new JsonObject { ["method"] = definition["method"]!.DeepClone(), ["url"] = HttpCredentialPolicy.Endpoint(Text(definition["url"])) }),
                ["requestDigest"] = HttpCredentialPolicy.RequestDigest(data),
            };
            if (!HttpCredentialPolicy.ValidRequests(metadata["requests"], Text(metadata["origin"]))) throw new InvalidOperationException($"HTTP consolidation: invalid scope in revision {id}.");
            definitions[id] = metadata; converted[id] = data;
            if (headers.Count != 0 && protector is null) throw new InvalidOperationException("HTTP consolidation requires the configured encryption key.");
            var protectedSecret = headers.Count == 0 ? null : protector!.Protect(headers.ToJsonString());
            await Execute("INSERT INTO workflow_http_credential(id,name,description,enabled,created_at,updated_at) SELECT id,name,description,enabled,created_at,updated_at FROM workflow_http_operation WHERE id=@id AND NOT EXISTS(SELECT 1 FROM workflow_http_credential WHERE id=@id)", ("id", Guid.Parse(operation)));
            // 重用不透明 ID，任何碰撞均回滚，绝不覆盖现有凭据或静默合并授权。
            await Execute("""
                INSERT INTO workflow_http_credential_revision(id,credential_id,definition_json,digest,secret_cipher,secret_nonce,secret_tag,secret_hint,created_by,created_at)
                VALUES(@id,@credential,CAST(@definition AS jsonb),@digest,@cipher,@nonce,@tag,@hint,@admin,@created)
                """, ("id", Guid.Parse(id)), ("credential", Guid.Parse(operation)), ("definition", metadata.ToJsonString()), ("digest", HttpRequestDefinition.Digest(metadata)),
                ("cipher", protectedSecret?.Cipher), ("nonce", protectedSecret?.Nonce), ("tag", protectedSecret?.Tag), ("hint", protectedSecret is null ? null : "••••"),
                ("admin", Guid.Parse(Text(row["created_by"]))), ("created", DateTimeOffset.Parse(Text(row["created_at"]), System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime()));
        }
        await Execute("""
            INSERT INTO workflow_http_credential(id,name,description,enabled,created_at,updated_at)
                SELECT id,name,description,enabled,created_at,updated_at FROM workflow_http_operation o WHERE NOT EXISTS(SELECT 1 FROM workflow_http_credential c WHERE c.id=o.id);
            UPDATE workflow_http_credential c SET current_revision=o.current_revision FROM workflow_http_operation o WHERE c.id=o.id;
            INSERT INTO workflow_http_credential_grant SELECT * FROM workflow_http_grant;
            INSERT INTO workflow_http_credential_grant_audit SELECT * FROM workflow_http_grant_audit;
            """);
        var counts = new JsonObject { ["credentials"] = (await Rows("SELECT to_jsonb(o)::text FROM workflow_http_operation o")).Count };
        foreach (var (table, key, column) in new[] { ("workflow", "workflow_id", "graph"), ("workflow_template", "template_id", "graph"), ("workflow_deployment", "deployment_id", "graph_snapshot") })
        {
            var rows = await Rows($"SELECT to_jsonb(t)::text FROM {table} t WHERE {column} @> '{{\"nodes\":[{{\"type\":\"externalCall\"}}]}}'");
            foreach (var row in rows)
            {
                var graph = row[column]!.AsObject(); ConvertGraph(graph);
                if (table == "workflow_deployment")
                {
                    var snapshot = row["execution_snapshot"]!.AsObject(); ConvertSnapshot(snapshot, graph);
                    await Execute($"UPDATE {table} SET {column}=CAST(@graph AS jsonb),execution_snapshot=CAST(@snapshot AS jsonb) WHERE {key}=@id",
                        ("graph", graph.ToJsonString()), ("snapshot", snapshot.ToJsonString()), ("id", Guid.Parse(Text(row[key]))));
                }
                else await Execute($"UPDATE {table} SET {column}=CAST(@graph AS jsonb) WHERE {key}=@id", ("graph", graph.ToJsonString()), ("id", Guid.Parse(Text(row[key]))));
            }
            counts[table] = rows.Count;
        }
        var deleted = 0; var queued = 0;
        // 清理结果不释放已消费的幂等键，避免外部客户端重放旧请求再次调用业务接口。
        await Execute("""
            CREATE TABLE IF NOT EXISTS workflow_run_retired_idempotency (
                api_key_id uuid NOT NULL REFERENCES workflow_api_key(api_key_id) ON DELETE CASCADE,
                idempotency_key text NOT NULL,
                retired_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                PRIMARY KEY(api_key_id,idempotency_key)
            )
            """);
        foreach (var run in runs)
        {
            var id = Guid.Parse(Text(run["run_id"]));
            if (Text(run["status"]) != "queued")
            {
                // 用户明确允许清理旧结果；仅删除终态旧 HTTP 运行，其节点、事件、运行绑定由 FK 级联。
                if (Text(run["status"]) is not ("succeeded" or "failed" or "cancelled")) throw new InvalidOperationException("HTTP consolidation: unknown run status; refusing history deletion.");
                await Execute("""
                    INSERT INTO workflow_run_retired_idempotency(api_key_id,idempotency_key)
                    SELECT api_key_id,idempotency_key FROM workflow_run WHERE run_id=@id AND trigger_type='api'
                    ON CONFLICT DO NOTHING
                    """, ("id", id));
                await Execute("DELETE FROM workflow_run WHERE run_id=@id", ("id", id)); deleted++; continue;
            }
            if ((await Rows("SELECT to_jsonb(n)::text FROM workflow_node_run n WHERE run_id=@id AND started_at IS NOT NULL", ("id", id))).Count != 0)
                throw new InvalidOperationException("HTTP consolidation: queued task has started nodes; reconcile it before migration.");
            var graph = run["graph_snapshot"]!.AsObject(); ConvertGraph(graph);
            var snapshot = run["input_json"]!.AsObject(); ConvertSnapshot(snapshot, graph);
            await Execute("UPDATE workflow_run SET graph_snapshot=CAST(@graph AS jsonb),input_json=CAST(@snapshot AS jsonb) WHERE run_id=@id; UPDATE workflow_node_run SET node_type='httpRequest' WHERE run_id=@id AND node_type='externalCall'",
                ("graph", graph.ToJsonString()), ("snapshot", snapshot.ToJsonString()), ("id", id)); queued++;
        }
        counts["deletedHistoricalRuns"] = deleted; counts["queuedRuns"] = queued;
        await Execute("CREATE TABLE IF NOT EXISTS workflow_http_consolidation_audit(version integer PRIMARY KEY,summary jsonb NOT NULL,created_at timestamptz NOT NULL DEFAULT clock_timestamp()); INSERT INTO workflow_http_consolidation_audit(version,summary) VALUES(37,CAST(@summary AS jsonb))", ("summary", counts.ToJsonString()));

        void ConvertGraph(JsonObject graph)
        {
            foreach (var node in graph["nodes"]!.AsArray().OfType<JsonObject>().Where(node => Text(node["type"]) == "externalCall"))
            {
                var old = node["data"]!.AsObject();
                if (!converted.TryGetValue(Text(old["revisionId"]), out var data) || Text(data["credentialId"]) != Text(old["operationId"])
                    || !JsonNode.DeepEquals(old["fields"], data["fields"]) || !JsonNode.DeepEquals(old["outputFields"], data["outputFields"]))
                    throw new InvalidOperationException($"HTTP consolidation: unresolved operation or changed schema at node {Text(node["id"])}.");
                var copy = data.DeepClone().AsObject(); if (old["title"] is { } title) copy["title"] = title.DeepClone();
                node["data"] = copy; node["type"] = "httpRequest";
            }
        }

        void ConvertSnapshot(JsonObject snapshot, JsonObject graph)
        {
            if (snapshot["external"] is not JsonObject old || old["references"] is not JsonObject references)
                throw new InvalidOperationException("HTTP consolidation: a frozen resource snapshot is missing.");
            var target = snapshot["http"] as JsonObject ?? new JsonObject { ["references"] = new JsonObject() };
            if (old["subject"] is { } subject)
            {
                if (target["subject"] is { } other && !JsonNode.DeepEquals(subject, other)) throw new InvalidOperationException("HTTP consolidation: resource subjects differ.");
                target["subject"] = subject.DeepClone();
            }
            foreach (var (nodeId, reference) in references)
            {
                var node = graph["nodes"]!.AsArray().OfType<JsonObject>().Single(n => Text(n["id"]) == nodeId);
                var data = node["data"]!.AsObject(); var revision = Text(data["credentialRevisionId"]);
                var oldRevision = revisions.Single(r => Text(r["id"]) == revision);
                if (Text(reference?["revisionId"]) != revision || Text(reference?["operationId"]) != Text(data["credentialId"]) || Text(reference?["digest"]) != Text(oldRevision["digest"]))
                    throw new InvalidOperationException("HTTP consolidation: frozen operation identity mismatch.");
                if (target["references"]![nodeId] is not null) throw new InvalidOperationException("HTTP consolidation: duplicate HTTP reference.");
                target["references"]![nodeId] = new JsonObject { ["digest"] = HttpRequestDefinition.Digest(data), ["credentialId"] = data["credentialId"]!.DeepClone(),
                    ["revisionId"] = revision, ["credentialDigest"] = HttpRequestDefinition.Digest(definitions[revision]) };
            }
            snapshot.Remove("external"); if (snapshot["http"] is null) snapshot["http"] = target;
        }

        NpgsqlCommand Command(string sql, params (string Name, object? Value)[] values)
        {
            var command = new NpgsqlCommand(sql, connection, transaction);
            foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return command;
        }
        async Task Execute(string sql, params (string Name, object? Value)[] values)
        { await using var command = Command(sql, values); await command.ExecuteNonQueryAsync(ct); }
        async Task<List<JsonObject>> Rows(string sql, params (string Name, object? Value)[] values)
        {
            await using var command = Command(sql, values); await using var reader = await command.ExecuteReaderAsync(ct); var result = new List<JsonObject>();
            while (await reader.ReadAsync(ct)) result.Add(JsonNode.Parse(reader.GetString(0))!.AsObject()); return result;
        }
    }

    /// <summary>把旧声明转换成可见、可导出的 HTTP 配置；转义常量，绝不把文字误当成新变量。</summary>
    public static JsonObject ConvertDefinition(JsonObject definition, string credential, string revision)
    {
        var data = new JsonObject
        {
            ["fields"] = definition["inputs"]!.DeepClone(), ["outputFields"] = definition["outputs"]!.DeepClone(), ["extract"] = definition["extract"]!.DeepClone(),
            ["credentialId"] = credential, ["credentialRevisionId"] = revision, ["responseMode"] = "fields",
            ["http"] = new JsonObject
            {
                ["method"] = definition["method"]!.DeepClone(), ["url"] = definition["url"]!.DeepClone(), ["query"] = Rows("query"), ["headers"] = Rows("headers"),
                ["bodyType"] = definition.ContainsKey("body") ? "json" : "none", ["body"] = definition.ContainsKey("body") ? Template(definition["body"]!)?.ToJsonString() ?? "null" : "",
                ["form"] = new JsonArray(), ["timeoutSeconds"] = definition["timeoutSeconds"]!.DeepClone(), ["responseLimitBytes"] = HttpRequestDefinition.MaxResponseBytes, ["failOnHttpError"] = true,
            },
        };
        if (definition["readOnly"]?.GetValue<bool>() != true || Text(definition["method"]) is not ("GET" or "POST") || HttpRequestDefinition.Validate(data) is { })
            throw new InvalidOperationException($"HTTP consolidation: invalid legacy definition {revision}.");
        return data;

        JsonArray Rows(string key) => new(definition[key]!.AsArray().OfType<JsonObject>()
            .Where(row => key != "headers" || !HttpRequestDefinition.SensitiveHeader(Text(row["name"])))
            .Select(row => (JsonNode)new JsonObject { ["name"] = row["name"]!.DeepClone(), ["value"] = row["value"]!["input"] is { } input
                ? "{{" + Text(input) + "}}" : Escape(Scalar(row["value"]!["constant"])) }).ToArray());
    }

    /// <summary>递归转换类型化 JSON 模板，输入引用保留 JSON 原始类型。</summary>
    private static JsonNode? Template(JsonNode template) => template.AsObject() switch
    {
        var obj when obj.ContainsKey("input") => JsonValue.Create("{{" + Text(obj["input"]) + "}}"),
        var obj when obj.ContainsKey("constant") => EscapeValue(obj["constant"]),
        var obj when obj["object"] is JsonObject values => new JsonObject(values.Select(pair => KeyValuePair.Create(pair.Key, Template(pair.Value!)))),
        var obj when obj["array"] is JsonArray values => new JsonArray(values.Select(value => Template(value!)).ToArray()),
        _ => throw new InvalidOperationException("HTTP consolidation: unknown template."),
    };
    /// <summary>常量字符串中的模板标记必须作为字面量发送。</summary>
    private static JsonNode? EscapeValue(JsonNode? value) => value switch
    {
        JsonObject obj => new JsonObject(obj.Select(pair => KeyValuePair.Create(pair.Key, EscapeValue(pair.Value)))),
        JsonArray array => new JsonArray(array.Select(EscapeValue).ToArray()),
        JsonValue scalar when scalar.TryGetValue<string>(out var text) => JsonValue.Create(Escape(text)), _ => value?.DeepClone(),
    };
    /// <summary>双重模板括号表示字面量，与 HTTP 节点模板语法一致。</summary>
    private static string Escape(string value) => value.Replace("{{", "{{{{", StringComparison.Ordinal).Replace("}}", "}}}}", StringComparison.Ordinal);
    /// <summary>查询和头中的标量保持旧请求的字符串表示。</summary>
    private static string Scalar(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : value?.ToJsonString() ?? "null";
}
