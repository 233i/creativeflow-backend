using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Features.Agent;
using CreativeFlow.Domain.Ai;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>冻结历史图片权限、混合内容块和流式事件的原始处理规则。</summary>
public sealed class AgentMessageCharacterizationTests
{
    /// <summary>数据库测试只能使用显式设置的隔离测试实例。</summary>
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;

    /// <summary>历史中只恢复当前账号可读且未回收的站内图片，不能信任外部地址或 MIME 声明。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task ImageHistoryRetainsAssetPermissionsAndBlockOrder()
    {
        await using var fixture = await AgentRunFixture.CreateAsync();
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        for (var index = 0; index < ids.Length; index++)
        {
            var owner = index == 1 ? fixture.Database.OtherDevice : fixture.Database.Device;
            var kind = index == 3 ? "video" : "image";
            var file = await fixture.Database.Store.SaveAsync([1, 2, (byte)index], "image/png", ".png", TestContext.Current.CancellationToken);
            await fixture.Database.SqlAsync($"INSERT INTO canvas_asset(asset_id,device_id,kind,media_type,storage_path,byte_size) VALUES ('{ids[index]}','{owner}','{kind}','image/png','{file.StoragePath}',3)");
            if (index != 4) fixture.AssetStore.Available[file.StoragePath] = [1, 2, 3];
        }
        var blocks = new List<AgentContentBlock> { new(AgentContentKind.Text, Text: "before") };
        blocks.AddRange(ids.Select(id => new AgentContentBlock(AgentContentKind.Image,
            ImageUrl: $"/api/v1/canvas/assets/{id}?original=true", MediaType: "untrusted/type")));
        blocks.Add(new(AgentContentKind.Image, ImageUrl: "https://external.invalid/image.png"));
        blocks.Add(new(AgentContentKind.Text, Text: "after"));
        await fixture.Agents.InsertMessageAsync(new AgentStoredMessage(Guid.NewGuid(), fixture.Run.SessionId,
            fixture.Run.Id, fixture.Run.DeviceId, 1, "user", AgentModelMessageConverter.SerializeBlocks(blocks), DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        // 先有历史消息再回收素材；新消息引用已回收素材本来就会被数据库触发器阻止。
        await fixture.Database.SqlAsync($"UPDATE canvas_asset SET deleted_at=now() WHERE asset_id='{ids[2]}'");
        await fixture.ExecuteAsync();
        var content = Assert.Single(fixture.Models.Calls).Request.Messages[1].Content;
        Assert.Equal([AgentContentKind.Text, AgentContentKind.Image, AgentContentKind.Text], content.Select(block => block.Kind));
        Assert.Equal("before", content[0].Text);
        Assert.Equal("after", content[2].Text);
        Assert.Equal("image/png", content[1].MediaType);
        Assert.Equal(new byte[] { 1, 2, 3 }, content[1].ImageBytes);
    }

    /// <summary>相邻同类增量合并但不跨工具边界；临时事件不占持久序号。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task InterleavedStreamKeepsBoundariesAndEventOrder()
    {
        await using var fixture = await AgentRunFixture.CreateAsync();
        fixture.Models.Respond = call => call == 1
            ? [new(AgentModelEventKind.ThinkingDelta, Text: "think-"), new(AgentModelEventKind.ThinkingDelta, Text: "more"),
               new(AgentModelEventKind.TextDelta, Text: "hello"), new(AgentModelEventKind.TextDelta, Text: " world"),
               new(AgentModelEventKind.ToolUse, ToolUseId: "read-1", ToolName: "read_canvas", Json: "{}", ProviderItemId: "fc-read"),
               new(AgentModelEventKind.TextDelta, Text: "after tool"), new(AgentModelEventKind.Completed, StopReason: "tool_calls")]
            : [new(AgentModelEventKind.TextDelta, Text: "done"), new(AgentModelEventKind.Completed, StopReason: "stop")];
        await fixture.ExecuteAsync();
        var content = fixture.Models.Calls[1].Request.Messages[1].Content;
        Assert.Equal([AgentContentKind.Thinking, AgentContentKind.Text, AgentContentKind.ToolUse, AgentContentKind.Text], content.Select(block => block.Kind));
        Assert.Equal("think-more", content[0].Text);
        Assert.Equal("hello world", content[1].Text);
        Assert.Equal("fc-read", content[2].ProviderItemId);
        Assert.Equal("after tool", content[3].Text);
        Assert.Equal(["run.started", "thinking.delta", "thinking.delta", "text.delta", "text.delta", "text.delta",
            "tool.started", "tool.completed", "text.delta", "run.succeeded"], fixture.Browser.Events.Select(item => item.Type));
        Assert.Equal([1L, 0L, 0L, 0L, 0L, 0L, 2L, 3L, 0L, 4L], fixture.Browser.Events.Select(item => item.Sequence));
        var stored = await fixture.MessagesAsync();
        Assert.Equal([0, 1, 2, 3], stored.Select(message => message.Ordinal));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(stored[1].ContentJson), JsonNode.Parse(AgentModelMessageConverter.SerializeBlocks(content))));
    }
}
