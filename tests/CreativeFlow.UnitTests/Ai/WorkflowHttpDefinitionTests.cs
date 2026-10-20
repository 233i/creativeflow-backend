using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Infrastructure;
using CreativeFlow.Infrastructure.Ai.HttpRequests;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>HTTP 图契约与真实依赖注入的网络开关回归。</summary>
public sealed class WorkflowHttpDefinitionTests
{
    /// <summary>HTTP 扩容不改变模型正文和嵌套结构化文本的原有限制。</summary>
    [Fact]
    public void HttpCapacityDoesNotWidenModelOutputLimits()
    {
        foreach (var value in new JsonNode[] { JsonValue.Create(new string('a', 16001))!, JsonValue.Create(new string('中', 8000))!, new JsonObject { ["text"] = new string('a', 16001) } })
        { Assert.True(WorkflowV2Values.IsBounded(value)); Assert.False(WorkflowV2Values.IsModelOutputBounded(value)); }
        Assert.True(WorkflowV2Values.IsModelOutputBounded(JsonValue.Create(new string('a', 16000))));
    }
    /// <summary>JSONB 对象重排不能破坏固定版本，输入类型和路径转义仍可验证。</summary>
    [Fact]
    public void GraphContractsRemainStableAcrossJsonObjectReordering()
    {
        var graph = HttpRequestFixtures.Graph(); Assert.True(WorkflowV2GraphValidator.Validate(graph).IsValid);
        var left = JsonNode.Parse("""{"b":{"c":3,"a":1},"a":[2,1]}""")!; var right = JsonNode.Parse("""{"a":[2,1],"b":{"a":1,"c":3}}""")!;
        Assert.Equal(HttpRequestDefinition.Digest(left), HttpRequestDefinition.Digest(right));
        right["a"]![0] = 3; Assert.NotEqual(HttpRequestDefinition.Digest(left), HttpRequestDefinition.Digest(right));
        Assert.True(HttpRequestDefinition.TryExtract(JsonNode.Parse("""{"a/b":{"~key":[null,7]}}"""), "/a~1b/~0key/0", out var value)); Assert.Null(value);
        Assert.False(HttpRequestDefinition.TryExtract(left, "/unknown", out _));
        graph["nodes"]![1]!["data"]!["outputFields"] = JsonNode.Parse("""[{"key":"body","type":"string","required":true}]""");
        Assert.False(WorkflowV2GraphValidator.Validate(graph).IsValid);
        Assert.NotNull(HttpRequestDefinition.Validate(new JsonObject { ["http"] = new JsonObject() }));
    }

    /// <summary>空目标白名单禁止访问，未指定时允许公网 HTTPS；错误开关值不隐式开启。</summary>
    [Theory]
    [InlineData("{\"Workflow\":{\"Enabled\":true}}", true, true)]
    [InlineData("{\"Workflow\":{\"Enabled\":true,\"HttpRequests\":{\"AllowedEndpoints\":[]}}}", true, false)]
    [InlineData("{\"Workflow\":{\"Enabled\":true,\"HttpRequests\":{\"Enabled\":false}}}", false, true)]
    [InlineData("{\"Workflow\":{\"Enabled\":true,\"HttpRequests\":{\"Enabled\":\"invalid\"}}}", false, true)]
    public async Task NetworkDefaultsAndExplicitConfigurationAreRespected(string json, bool enabled, bool publicAllowed)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        configuration["Ai:EncryptionKey"] = Convert.ToBase64String(new byte[32]);
        var services = new ServiceCollection(); services.AddLogging(); services.AddInfrastructure(configuration);
        await using var provider = services.BuildServiceProvider();
        Assert.Equal(enabled, provider.GetRequiredService<WorkflowHttpOptions>().Enabled);
        var network = provider.GetRequiredService<WorkflowHttpNetwork>(); var uri = new Uri("https://8.8.8.8/query");
        if (publicAllowed) Assert.NotEmpty(await network.ResolveAsync(uri, TestContext.Current.CancellationToken));
        else await Assert.ThrowsAsync<WorkflowHttpException>(() => network.ResolveAsync(uri, TestContext.Current.CancellationToken));
    }
}
