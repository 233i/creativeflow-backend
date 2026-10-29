using System.Text.Json.Nodes;
using Npgsql;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Realtime;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Infrastructure.Persistence.Migrations;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

/// <summary>在真实隔离 SQL 上区分画布观察回执与新素材输入，不调用收费模型或真实浏览器。</summary>
public sealed class AgentAssetReferenceTests
{
    /// <summary>未显式指定隔离数据库时禁止回落到应用数据库。</summary>
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;
    /// <summary>读取当前用例的取消令牌，不使用正式后台宿主。</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>观察包含不可用素材的画布时，工具、消息、事件和最终回答均保存，且只执行一次工具。</summary>
    [Theory(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    [InlineData("recycled", "read_canvas")]
    [InlineData("cleaning", "read_canvas")]
    [InlineData("purged", "read_canvas")]
    [InlineData("recycled", "get_node_status")]
    [InlineData("cleaning", "get_node_status")]
    [InlineData("purged", "get_node_status")]
    [InlineData("recycled", "subscribe_node")]
    [InlineData("cleaning", "subscribe_node")]
    [InlineData("purged", "subscribe_node")]
    [InlineData("recycled", "wait_for_node_event")]
    [InlineData("cleaning", "wait_for_node_event")]
    [InlineData("purged", "wait_for_node_event")]
    public async Task CanvasObservationsPersistWithoutSelectingUnavailableAssets(string state, string tool)
    {
        await using var f = await AgentRunFixture.CreateAsync();
        var asset = await SeedAssetAsync(f, state);
        var url = $"/api/v1/canvas/assets/{asset}";
        var receipt = $$$"""{"status":"ok","nodes":[{"data":{"assetUrl":"{{{url}}}"}}]}""";
        f.Browser.ToolResult = new AgentCanvasToolResult(receipt, false);
        f.Models.Respond = iteration => iteration == 1
            ? [new(AgentModelEventKind.ToolUse, ToolUseId: "read-1", ToolName: tool, Json: "{}"),
                new(AgentModelEventKind.Completed, StopReason: "tool_use")]
            : [new(AgentModelEventKind.TextDelta, Text: $"Observed an existing image at {url}."),
                new(AgentModelEventKind.Completed, StopReason: "stop")];

        await f.ExecuteAsync();

        Assert.Equal(AgentRunStatus.Succeeded, (await f.ReadRunAsync()).Status);
        Assert.Equal(["read-1"], f.Browser.ExecutedTools);
        Assert.Equal(2, f.Models.Calls.Count);
        var result = Assert.Single(f.Models.Calls[1].Request.Messages[^1].Content);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(receipt), JsonNode.Parse(result.Json!)));
        Assert.Contains(await f.MessagesAsync(), m => m.Role == "user" && m.ContentJson.Contains("tool_result", StringComparison.Ordinal));
        Assert.Equal(["run.started", "tool.started", "tool.completed", "text.delta", "run.succeeded"], f.Browser.Events.Select(e => e.Type));
        Assert.Equal(1, await f.Database.CountAsync("SELECT count(*) FROM agent_tool_call WHERE status='succeeded' AND result IS NOT NULL"));
        Assert.Equal(0, await f.Database.CountAsync("SELECT count(*) FROM agent_tool_call WHERE status='running'"));
        foreach (var kind in new[] { "agent_tool_call", "agent_message", "agent_event" })
            Assert.True(await f.Database.CountAsync($"SELECT count(*) FROM asset_reference WHERE asset_id='{asset}' AND source_kind='{kind}'") > 0);
        Assert.Equal(0, await f.Database.CountAsync($"SELECT count(*) FROM asset_reference_context WHERE asset_id='{asset}' AND protection='active'"));
        Assert.Equal(1, await f.Database.CountAsync($"SELECT count(*) FROM canvas_asset WHERE asset_id='{asset}' AND deleted_at IS NOT NULL"));
    }

    /// <summary>工具回执不能让同一条消息中新传的图片绕过可用性检查；新工具输入也保持原保护。</summary>
    [Theory(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    [InlineData("recycled")]
    [InlineData("cleaning")]
    [InlineData("purged")]
    public async Task NewImageAttachmentsAndToolInputsRemainGuarded(string state)
    {
        await using var f = await AgentRunFixture.CreateAsync();
        var asset = await SeedAssetAsync(f, state);
        var content = $$"""[{"kind":"tool_result","toolUseId":"old","json":"{}"},{"kind":"image","imageUrl":"/api/v1/canvas/assets/{{asset}}"}]""";
        var image = new AgentStoredMessage(Guid.NewGuid(), f.Run.SessionId, f.Run.Id, f.Run.DeviceId,
            1, "user", content, DateTimeOffset.UtcNow);
        var attachmentFailure = await Assert.ThrowsAsync<PostgresException>(() => f.Agents.InsertMessageAsync(image, Ct));
        Assert.Equal("asset_not_available", attachmentFailure.ConstraintName);
        var input = $$"""{"assetUrl":"/api/v1/canvas/assets/{{asset}}"}""";
        var toolFailure = await Assert.ThrowsAsync<PostgresException>(() => f.Agents.InsertToolCallAsync(ToolCall(f, "edit_image", input), Ct));
        Assert.Equal("asset_not_available", toolFailure.ConstraintName);
        Assert.Equal(0, await f.Database.CountAsync("SELECT count(*) FROM agent_tool_call"));
        Assert.Equal(1, await f.Database.CountAsync("SELECT count(*) FROM agent_message"));
    }

    /// <summary>从 0045 原样升级，重现旧回执写入失败，再证明修复保留现有行及完整结果。</summary>
    [Fact(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    public async Task MigrationFrom45PreservesRowsAndAllowsThePreviouslyRejectedReceipt()
    {
        await using var f = await AgentRunFixture.CreateAsync(migrationsThrough: 45);
        var asset = await SeedAssetAsync(f, "purged");
        var tool = ToolCall(f, "read_canvas", "{}");
        await f.Agents.InsertToolCallAsync(tool, Ct);
        var receipt = $$"""{"nodes":[{"assetUrl":"/api/v1/canvas/assets/{{asset}}"}]}""";
        var failure = await Assert.ThrowsAsync<PostgresException>(() => f.Agents.CompleteToolCallAsync(
            f.Run.DeviceId, f.Run.Id, tool.ToolUseId, "succeeded", receipt, null, DateTimeOffset.UtcNow, Ct));
        Assert.Equal("asset_not_available", failure.ConstraintName);

        await using var stream = typeof(MigrationRunner).Assembly.GetManifestResourceStream("CreativeFlow.Migrations.0046__agent_observation_asset_references.sql")!;
        using var reader = new StreamReader(stream);
        await f.Database.SqlAsync(await reader.ReadToEndAsync(Ct));
        Assert.True(await f.Agents.CompleteToolCallAsync(
            f.Run.DeviceId, f.Run.Id, tool.ToolUseId, "succeeded", receipt, null, DateTimeOffset.UtcNow, Ct));

        Assert.Equal(1, await f.Database.CountAsync("SELECT count(*) FROM agent_run"));
        Assert.Equal(1, await f.Database.CountAsync("SELECT count(*) FROM agent_message"));
        Assert.Equal(1, await f.Database.CountAsync("SELECT count(*) FROM agent_tool_call WHERE status='succeeded'"));
        Assert.Equal(1, await f.Database.CountAsync($"SELECT count(*) FROM canvas_asset WHERE asset_id='{asset}' AND purged_at IS NOT NULL"));
        Assert.Equal(1, await f.Database.CountAsync($"SELECT count(*) FROM asset_reference WHERE asset_id='{asset}' AND source_kind='agent_tool_call'"));
    }

    /// <summary>创建一次合成画布工具调用，便于核对输入准入与结果保存的不同边界。</summary>
    private static AgentStoredToolCall ToolCall(AgentRunFixture f, string name, string input) =>
        new(Guid.NewGuid(), f.Run.Id, f.Run.DeviceId, "read-1", name, "canvas", "running", input,
            null, null, DateTimeOffset.UtcNow, null);

    /// <summary>只在夹具独占的存储和数据库中创建不可用素材，不执行线上回收或删除。</summary>
    private static async Task<Guid> SeedAssetAsync(AgentRunFixture f, string state)
    {
        var asset = Guid.NewGuid();
        var stored = await f.Database.Store.SaveAsync([1, 2, 3], "image/png", ".png", Ct);
        await f.Database.SqlAsync($$"""
            INSERT INTO canvas_asset(asset_id,device_id,kind,media_type,storage_path,byte_size)
            VALUES ('{{asset}}','{{f.Database.Device}}','image','image/png','{{stored.StoragePath}}',3);
            UPDATE canvas_asset SET deleted_at=now(),purge_pending={{(state == "cleaning" ? "true" : "false")}},
                purged_at={{(state == "purged" ? "now()" : "NULL")}} WHERE asset_id='{{asset}}';
            """);
        return asset;
    }
}
