using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Features.Agent;
using CreativeFlow.Infrastructure.Ai.Protocols;
using CreativeFlow.Infrastructure.Ai.Maf;
using CreativeFlow.UnitTests.Architecture;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>用迁移前固定混合历史锁定三协议差异；不得为了统一 MAF 反改这些金样本。</summary>
public sealed class AgentHistoryGoldenTests
{
    /// <summary>历史持久化保留全部五类块、空值、错误标记、调用顺序及 ProviderItemId。</summary>
    [Fact]
    public void StoredHistoryRoundTripKeepsAllExistingFields()
    {
        var fixture = Fixture();
        foreach (var message in fixture["messages"]!.AsArray())
        {
            var blocks = AgentModelMessageConverter.ParseBlocks(message!["content"]!.ToJsonString());
            var encoded = AgentModelMessageConverter.SerializeBlocks(blocks);
            // 旧解析器将显式 JSON null 读取为字符串 "null"；锁定此差异，不借迁移修复历史。
            Assert.Equal(blocks.Select(block => block with { Json = block.Json ?? "null" }), AgentModelMessageConverter.ParseBlocks(encoded));
            foreach (var value in JsonNode.Parse(encoded)!.AsArray())
            {
                Assert.Equal(new[] { "kind", "text", "toolUseId", "toolName", "json", "isError", "providerItemId", "imageUrl", "mediaType" },
                    value!.AsObject().Select(pair => pair.Key));
            }
        }
        var assistant = AgentModelMessageConverter.ParseBlocks(fixture["messages"]![1]!["content"]!.ToJsonString());
        Assert.Equal(AgentContentKind.Thinking, assistant[0].Kind);
        Assert.Equal(new[] { "fc-a", "fc-b" }, assistant.Skip(2).Select(block => block.ProviderItemId));
    }

    /// <summary>三个现有协议按各自既有规则构建请求；保留旧版对 Thinking 的省略，不擅自修复。</summary>
    [Theory]
    [InlineData("openaiChat", false)]
    [InlineData("openaiChat", true)]
    [InlineData("responses", false)]
    [InlineData("responses", true)]
    [InlineData("anthropic", false)]
    [InlineData("anthropic", true)]
    public void MixedHistoryMatchesFrozenProtocolGolden(string protocol, bool roundTripThroughMaf)
    {
        var fixture = Fixture();
        var messages = fixture["messages"]!.AsArray().Select(message => new AgentModelMessage(
            (string?)message!["role"] == "user" ? AgentMessageRole.User : AgentMessageRole.Assistant,
            AgentModelMessageConverter.ParseBlocks(message["content"]!.ToJsonString()).Select(block =>
                block.Kind == AgentContentKind.Image ? block with { ImageBytes = [1, 2, 3] } : block).ToArray())).ToArray();
        if (roundTripThroughMaf) messages = messages.Select(message => MafModelDispatcher.FromChatMessage(MafModelDispatcher.ToChatMessage(message))).ToArray();
        var actual = protocol switch
        {
            "openaiChat" => AgentProtocolJson.OpenAiMessages((string)fixture["systemPrompt"]!, messages),
            "responses" => AgentProtocolJson.ResponsesInput(messages),
            _ => AgentProtocolJson.AnthropicMessages(messages),
        };
        Assert.True(JsonNode.DeepEquals(fixture[protocol], actual), actual.ToJsonString());
    }

    /// <summary>旧解析器继续跳过未知块、兼容对象形态的工具 JSON，损坏消息不变成新异常。</summary>
    [Fact]
    public void LegacyMalformedAndObjectJsonRulesRemainExplicit()
    {
        Assert.Empty(AgentModelMessageConverter.ParseBlocks("{broken"));
        Assert.Empty(AgentModelMessageConverter.ParseBlocks("{}"));
        var parsed = AgentModelMessageConverter.ParseBlocks("""[{"kind":"future"},{"kind":"tool_result","toolUseId":"old","json":{"ok":false},"isError":true}]""");
        var result = Assert.Single(parsed);
        Assert.Equal("{\"ok\":false}", result.Json);
        Assert.True(result.IsError);
        Assert.Equal("old", result.ToolUseId);
    }

    /// <summary>从版本化合成 fixture 加载输入与完整预期，不读取真实会话或媒体。</summary>
    private static JsonObject Fixture() => JsonNode.Parse(File.ReadAllText(Path.Combine(
        DependencyBoundaryTests.RepositoryRoot(), "tests/CreativeFlow.UnitTests/Contracts/maf/agent-history-v1.json")))!.AsObject();
}
