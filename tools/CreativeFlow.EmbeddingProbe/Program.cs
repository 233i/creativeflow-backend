using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Infrastructure;

namespace CreativeFlow.EmbeddingProbe;

/// <summary>显式运行的本地模型探针，使用生产向量适配器，但不启动应用、数据库、队列或 Worker。</summary>
internal static class Program
{
    /// <summary>仅接受回环 Base URL 和用户指定的模型 ID，发送三条合成文本并核对结果维度与顺序。</summary>
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 2 || !Uri.TryCreate(args[0], UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != "http" || endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0
            || endpoint.Fragment.Length > 0 || !IPAddress.TryParse(endpoint.Host, out var address)
            || !IPAddress.IsLoopback(address) || string.IsNullOrWhiteSpace(args[1]))
        {
            Console.Error.WriteLine("Usage: CreativeFlow.EmbeddingProbe http://127.0.0.1:1234/v1 <loaded-model-id>. Only numeric loopback HTTP is permitted.");
            return 2;
        }
        var baseUrl = endpoint.AbsoluteUri.TrimEnd('/');
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            // 仅满足未解析的基础设施配置；本工具绝不访问这个不可用数据库或使用真实加密密钥。
            ["Ai:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            ["Postgres:ConnectionString"] = "Host=127.0.0.1;Port=1;Database=unused;Username=unused",
            ["DeviceIdentity:Pepper"] = "embedding-probe-unused-pepper",
            ["Embedding:AllowedEndpoints:0"] = baseUrl + "/embeddings",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.ClearProviders());
        services.AddSingleton<IHostEnvironment>(new ProbeEnvironment());
        services.AddInfrastructure(config);
        await using var provider = services.BuildServiceProvider();
        var id = ProviderId.New();
        var model = new AiModel(ModelId.New(), id, args[1], args[1], true, ProviderType.OpenAiEmbeddings,
            ModelCategory.Embedding, null, null, null, false, false, false, 0, DateTimeOffset.UtcNow);
        var channel = new AiProvider(id, null, "Local embedding probe", null, null, ProviderType.OpenAiEmbeddings,
            null, baseUrl, true, null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) { Models = [model] };
        EmbeddingInput[] inputs = [new("workflow", "CreativeFlow 支持图片和视频工作流。"),
            new("database", "Qdrant 用于存储向量和检索文档。"), new("question", "哪个数据库可以存储向量？")];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var started = Stopwatch.GetTimestamp();
        var result = await provider.GetRequiredService<IEmbeddingDispatcher>().EmbedAsync(
            new(channel, model, "", new EmbeddingSettings(), inputs, "document"), timeout.Token);
        if (!result.Ok)
        {
            Console.Error.WriteLine($"Embedding failed: {result.Code}; {result.Error}");
            return 1;
        }
        if (!inputs.Select(input => input.Id).SequenceEqual(result.Vectors.Select(vector => vector.Id)))
            throw new InvalidOperationException("Input order was not preserved.");
        Console.WriteLine($"Model: {args[1]}; vectors: {result.Vectors.Count}; dimensions: {result.Dimensions}; tokens: {result.TotalTokens}; elapsedMs: {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}");
        Console.WriteLine($"Configuration fingerprint (not verified weights): {result.ConfigurationFingerprint}");
        return 0;
    }

    /// <summary>只为本地探针开启 Development 回环策略，不更改真实宿主环境。</summary>
    private sealed class ProbeEnvironment : IHostEnvironment
    {
        /// <summary>探针固定为开发环境。</summary>
        public string EnvironmentName { get; set; } = Environments.Development;
        /// <summary>探针装配名称。</summary>
        public string ApplicationName { get; set; } = "CreativeFlow.EmbeddingProbe";
        /// <summary>无文件读取用途的内容根目录。</summary>
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        /// <summary>拒绝读取宿主私有配置文件。</summary>
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
