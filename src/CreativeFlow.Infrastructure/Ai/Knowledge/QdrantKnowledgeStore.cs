using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Features.Knowledge;

namespace CreativeFlow.Infrastructure.Ai.Knowledge;

/// <summary>受控的 Qdrant 连接；开发和生产都只接受服务器回环地址及各自的命名空间。</summary>
internal sealed record KnowledgeStorageOptions
{
    /// <summary>受信任部署配置开关。</summary>
    public bool Enabled { get; init; }
    /// <summary>开发环境标志，不能由 API 请求覆盖；生产使用 cfprod 命名空间。</summary>
    public bool Development { get; init; }
    /// <summary>仅支持服务器回环 HTTP；开发可使用隧道端口，生产固定使用 6333。</summary>
    public string Endpoint { get; init; } = "";
    /// <summary>凭据只存在进程配置，不进入文档或快照。</summary>
    public string ApiKey { get; init; } = "";
    /// <summary>部署级独立命名空间，集合名随后只拼接服务器生成的版本 UUID。</summary>
    public string Namespace { get; init; } = "";
}

/// <summary>Qdrant REST 适配器，正文不写入向量库，只保存资源和版本过滤标识。</summary>
internal sealed partial class QdrantKnowledgeStore(IHttpClientFactory clients, KnowledgeStorageOptions options) : IKnowledgeIndexStore
{
    /// <summary>端点和与环境匹配的命名空间都有效时才允许索引受理。</summary>
    public bool Available => options.Enabled && NamespacePattern().IsMatch(options.Namespace)
        && options.Namespace.StartsWith(options.Development ? "cfdev_" : "cfprod_", StringComparison.Ordinal)
        && Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var uri) && uri.Scheme == "http"
        && uri.Host == "127.0.0.1" && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.UserInfo.Length == 0
        && (options.Development || uri.Port == 6333);

    /// <summary>名称只能是环境专用 ASCII 前缀，拒绝路径字符及跨环境集合。</summary>
    [GeneratedRegex("^cf(?:dev|prod)_[a-z0-9_]{4,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamespacePattern();

    /// <summary>检查现有集合的维度和距离；只在明确不存在时创建。</summary>
    public async Task PrepareAsync(Guid library, Guid version, int dimensions, CancellationToken ct)
    {
        if (dimensions is < 1 or > 8192) throw new KnowledgeException("向量维度超出试点上限。");
        var path = Collection(version);
        var existing = await SendAsync(HttpMethod.Get, path, null, ct, allowMissing: true);
        if (existing is null)
            await SendAsync(HttpMethod.Put, path, new JsonObject
            {
                ["vectors"] = new JsonObject { ["size"] = dimensions, ["distance"] = "Cosine", ["on_disk"] = true },
                ["shard_number"] = 1,
            }, ct);
        else if (existing is not JsonObject || ReadPath(existing, "config", "params", "vectors", "size") is not JsonValue size
            || !size.TryGetValue<int>(out var storedDimensions) || storedDimensions != dimensions
            || Text(ReadPath(existing, "config", "params", "vectors", "distance")) != "Cosine")
            throw new KnowledgeException("候选集合的维度或距离与索引配置不一致，禁止覆盖。");
        foreach (var field in new[] { "library", "version" })
            await SendAsync(HttpMethod.Put, path + "/index?wait=true", new JsonObject { ["field_name"] = field, ["field_schema"] = "keyword" }, ct);
    }

    /// <summary>固定稳定 ID 与库/版本 Payload，等待持久操作完成后才返回。</summary>
    public async Task WriteAsync(Guid library, Guid version, IReadOnlyList<EmbeddingVector> vectors, CancellationToken ct)
    {
        if (vectors.Count is < 1 or > 32 || vectors.Any(v => !Guid.TryParse(v.Id, out _) || v.Values.Length is < 1 or > 8192 || v.Values.Any(n => !float.IsFinite(n))))
            throw new KnowledgeException("索引写入批次无效。");
        var points = new JsonArray(vectors.Select(vector => (JsonNode)new JsonObject
        {
            ["id"] = vector.Id, ["vector"] = new JsonArray(vector.Values.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
            ["payload"] = new JsonObject { ["library"] = library.ToString(), ["version"] = version.ToString() },
        }).ToArray());
        var result = await SendAsync(HttpMethod.Put, Collection(version) + "/points?wait=true", new JsonObject { ["points"] = points }, ct);
        if (Text(ReadPath(result, "status")) != "completed") throw new KnowledgeException("Qdrant 未确认写入完成，可以从检查点重写索引。");
    }

    /// <summary>使用强制过滤精确计数，不能以全库计数判断候选成功。</summary>
    public async Task<int> CountAsync(Guid library, Guid version, CancellationToken ct)
    {
        var result = await SendAsync(HttpMethod.Post, Collection(version) + "/points/count",
            new JsonObject { ["filter"] = Filter(library, version), ["exact"] = true }, ct);
        return ReadPath(result, "count") is JsonValue value && value.TryGetValue<int>(out var count) && count >= 0
            ? count : throw new KnowledgeException("Qdrant 未返回有效计数。");
    }

    /// <summary>清理只针对服务器生成的单版本集合；404 代表已完成，可安全恢复断连后的删除。</summary>
    public async Task DeleteAsync(Guid library, Guid version, CancellationToken ct)
    {
        if (library == Guid.Empty) throw new KnowledgeException("知识库标识无效。");
        await SendAsync(HttpMethod.Delete, Collection(version), null, ct, allowMissing: true);
    }

    /// <summary>过滤前后都限定资源，向量与正文均不从上游响应直接交付。</summary>
    public async Task<KnowledgeIndexHit[]> SearchAsync(Guid library, Guid version, float[] query, int topK, CancellationToken ct)
    {
        // 旧单向量布局保留有界候选查询；主命中与邻接交付数量由共享检索策略单独限制。
        if (topK is < 1 or > 32 || query.Length is < 1 or > 8192 || query.Any(v => !float.IsFinite(v))) throw new KnowledgeException("检索参数无效。");
        var result = await SendAsync(HttpMethod.Post, Collection(version) + "/points/query", new JsonObject
        {
            ["query"] = new JsonArray(query.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
            ["filter"] = Filter(library, version), ["limit"] = topK, ["with_payload"] = true, ["with_vector"] = false,
        }, ct);
        if (ReadPath(result, "points") is not JsonArray points || points.Count > topK) throw new KnowledgeException("检索响应不符合约定。");
        var hits = new List<KnowledgeIndexHit>();
        foreach (var point in points)
        {
            if (Text(ReadPath(point, "payload", "library")) != library.ToString()
                || Text(ReadPath(point, "payload", "version")) != version.ToString()
                || !Guid.TryParse(Text(ReadPath(point, "id")), out var id)
                || ReadPath(point, "score") is not JsonValue score || !score.TryGetValue<double>(out var value) || !double.IsFinite(value)
                || hits.Any(hit => hit.Id == id)) throw new KnowledgeException("检索结果归属或分数异常，未交付任何片段。");
            hits.Add(new(id, value));
        }
        return hits.ToArray();
    }

    /// <summary>严格读取对象路径，畸形数组或原始值不能冒充协议对象。</summary>
    private static JsonNode? ReadPath(JsonNode? node, params string[] keys)
    {
        foreach (var key in keys) node = node is JsonObject value ? value[key] : null;
        return node;
    }

    /// <summary>只接受 JSON 字符串，不隐式转换数字或其他响应类型。</summary>
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>每次请求都包含服务端生成的库及版本过滤条件。</summary>
    private static JsonObject Filter(Guid library, Guid version) => new()
    {
        ["must"] = new JsonArray(new JsonObject { ["key"] = "library", ["match"] = new JsonObject { ["value"] = library.ToString() } },
            new JsonObject { ["key"] = "version", ["match"] = new JsonObject { ["value"] = version.ToString() } }),
    };

    /// <summary>固定前缀和 UUID 生成唯一集合路径，拒绝空版本。</summary>
    private string Collection(Guid version)
    {
        if (!Available || version == Guid.Empty) throw new KnowledgeException("知识索引未配置：需要环境匹配的独立命名空间及回环端点。");
        return "/collections/" + options.Namespace + "_" + version.ToString("N");
    }

    /// <summary>只向固定回环发送，禁止重定向，限制超时和响应大小且不回显上游正文。</summary>
    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct, bool allowMissing = false)
    {
        if (!Available) throw new KnowledgeException("知识索引尚未配置。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var client = clients.CreateClient("knowledge-qdrant");
            using var request = new HttpRequestMessage(method, options.Endpoint.TrimEnd('/') + path);
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            if (options.ApiKey.Length > 0) request.Headers.Add("api-key", options.ApiKey);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (allowMissing && response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode) throw new KnowledgeException($"Qdrant 返回 HTTP {(int)response.StatusCode}，未自动重试。");
            await response.Content.LoadIntoBufferAsync(1048576, deadline.Token);
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
            // 服务信息根路径没有 result/status 包装，仅供混合索引的版本预检使用。
            if (path == "/") return json is JsonObject ? json : throw new KnowledgeException("Qdrant 服务版本响应无效。");
            if (json?["status"]?.GetValue<string>() != "ok") throw new KnowledgeException("Qdrant 未确认操作成功。");
            return json["result"]?.DeepClone() ?? throw new KnowledgeException("Qdrant 响应缺少 result。");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new KnowledgeException("Qdrant 超时；已保存向量可重写索引，不必重新向量化。"); }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or InvalidOperationException or FormatException)
        { throw new KnowledgeException("Qdrant 连接或响应无效，请检查开发隧道和服务配置。"); }
    }
}
