using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;
using static CreativeFlow.Application.Features.Agent.AgentMessageJson;

namespace CreativeFlow.Application.Features.Agent;

/// <summary>恢复可读图片、编码持久消息并按上游顺序组装流式内容，不执行任何工具。</summary>
public sealed class AgentModelMessageConverter(
    IAgentRepository agents,
    ICanvasAssetRepository assets,
    IAssetStore assetStore,
    AgentRunEventRecorder events)
{
    /// <summary>按会话顺序恢复历史，忽略未知角色并重新验证图片读取权限。</summary>
    internal async Task<List<AgentModelMessage>> LoadHistoryAsync(AgentRun run, CancellationToken ct)
    {
        var history = await agents.ListMessagesAsync(run.DeviceId, run.SessionId, ct);
        var messages = new List<AgentModelMessage>(history.Count);
        var pending = new List<string>();
        AgentRunId? previousRun = null;
        var hasPreviousRun = false;
        foreach (var stored in history)
        {
            if (hasPreviousRun && stored.RunId != previousRun)
            {
                if (previousRun != run.Id) AppendMissingToolResults(messages, pending);
                pending.Clear();
            }
            previousRun = stored.RunId;
            hasPreviousRun = true;
            if (await ToModelMessageAsync(stored, run.DeviceId, ct) is not { } message) continue;
            messages.Add(message);
            foreach (var block in message.Content)
            {
                if (string.IsNullOrEmpty(block.ToolUseId)) continue;
                if (block.Kind == AgentContentKind.ToolUse && !pending.Contains(block.ToolUseId))
                    pending.Add(block.ToolUseId);
                else if (block.Kind == AgentContentKind.ToolResult)
                    pending.Remove(block.ToolUseId);
            }
        }
        if (hasPreviousRun && previousRun != run.Id) AppendMissingToolResults(messages, pending);
        return messages;
    }

    /// <summary>仅在模型输入中闭合已结束轮次缺失的回执，不改写历史、不推断成功，也不重新执行工具。</summary>
    private static void AppendMissingToolResults(List<AgentModelMessage> messages, List<string> pending)
    {
        if (pending.Count == 0) return;
        const string unavailable = """{"status":"unknown","error":"previous_tool_result_unavailable","message":"The previous run ended without a recorded result. Do not assume success or repeat mutations based on this placeholder."}""";
        messages.Add(new AgentModelMessage(AgentMessageRole.User,
            [.. pending.Select(id => new AgentContentBlock(
                AgentContentKind.ToolResult, ToolUseId: id, Json: unavailable, IsError: true))]));
    }

    /// <summary>持久化带会话顺序号的 Agent 消息。</summary>
    internal async Task AppendAsync(
        AgentRun run,
        string role,
        IReadOnlyList<AgentContentBlock> blocks,
        CancellationToken ct)
    {
        var ordinal = await agents.NextMessageOrdinalAsync(run.DeviceId, run.SessionId, ct);
        await agents.InsertMessageAsync(
            new AgentStoredMessage(
                Guid.CreateVersion7(), run.SessionId, run.Id, run.DeviceId, ordinal,
                role, SerializeBlocks(blocks), DateTimeOffset.UtcNow),
            ct);
    }

    /// <summary>消费一个模型流，保持消息块顺序和临时增量事件的既有语义。</summary>
    internal async Task<AgentModelTurn> StreamTurnAsync(
        AgentRun run,
        IAgentModelDispatcher turnModels,
        ProviderType protocol,
        AgentModelRequest request,
        CancellationToken ct)
    {
        // 文本和思考增量使用 sequence=0 即时推送；持久化事件序号由另一条追加路径分配，内容块保持上游顺序。
        var output = new StringBuilder();
        var blocks = new List<AgentContentBlock>();
        string? stopReason = null;
        string? streamError = null;
        int? inputTokens = null;
        int? outputTokens = null;

        await foreach (var item in turnModels.StreamAsync(protocol, request, ct))
        {
            switch (item.Kind)
            {
                case AgentModelEventKind.TextDelta when item.Text is { Length: > 0 } text:
                    output.Append(text);
                    AppendStreamBlock(blocks, AgentContentKind.Text, text);
                    await events.PublishDeltaAsync(run, "text.delta", text, ct);
                    break;

                case AgentModelEventKind.ThinkingDelta when item.Text is { Length: > 0 } text:
                    AppendStreamBlock(blocks, AgentContentKind.Thinking, text);
                    await events.PublishDeltaAsync(run, "thinking.delta", text, ct);
                    break;

                case AgentModelEventKind.ToolUse:
                    blocks.Add(new AgentContentBlock(
                        AgentContentKind.ToolUse,
                        ToolUseId: item.ToolUseId ?? $"tool-{Guid.CreateVersion7():N}",
                        ToolName: item.ToolName,
                        Json: item.Json ?? "{}",
                        ProviderItemId: item.ProviderItemId));
                    break;

                case AgentModelEventKind.Completed:
                    stopReason = item.StopReason;
                    inputTokens = item.InputTokens;
                    outputTokens = item.OutputTokens;
                    break;

                case AgentModelEventKind.Failed:
                    streamError = item.Error ?? "The model stream failed.";
                    break;
            }
        }

        return new AgentModelTurn(
            blocks, output.ToString(), stopReason, inputTokens, outputTokens, streamError);
    }

    /// <summary>将相邻同类文本增量合并为消息块，保持不同内容类型的边界。</summary>
    private static void AppendStreamBlock(
        List<AgentContentBlock> blocks,
        AgentContentKind kind,
        string text)
    {
        if (blocks.Count > 0 && blocks[^1].Kind == kind)
        {
            var previous = blocks[^1];
            blocks[^1] = previous with { Text = (previous.Text ?? string.Empty) + text };
            return;
        }

        blocks.Add(new AgentContentBlock(kind, Text: text));
    }

    /// <summary>将持久消息转为模型输入，仅加载当前范围内可读的图片。</summary>
    private async Task<AgentModelMessage?> ToModelMessageAsync(
        AgentStoredMessage message,
        DeviceId deviceId,
        CancellationToken ct)
    {
        // 按已保存内容块的顺序恢复模型消息；图片需重新加载可访问素材，不能直接相信历史中的外部地址。
        var role = message.Role switch
        {
            "user" or "tool" => AgentMessageRole.User,
            "assistant" => AgentMessageRole.Assistant,
            _ => (AgentMessageRole?)null,
        };
        if (role is null) return null;

        var blocks = new List<AgentContentBlock>();
        foreach (var block in ParseBlocks(message.ContentJson))
        {
            if (block.Kind != AgentContentKind.Image)
            {
                blocks.Add(block);
                continue;
            }

            if (!TryParseAssetUrl(block.ImageUrl, out var assetId)) continue;
            var asset = await assets.FindAsync(deviceId, assetId, ct);
            if (asset is null || asset.IsRecycled || asset.Kind != "image") continue;

            await using var stream = await assetStore.OpenReadAsync(asset.StoragePath, ct);
            if (stream is null) continue;
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            blocks.Add(block with
            {
                MediaType = asset.MediaType,
                ImageBytes = buffer.ToArray(),
            });
        }

        return new AgentModelMessage(role.Value, blocks);
    }

    /// <summary>读取已保存的 Agent 内容块，保留角色和上游关联字段。</summary>
    internal static IReadOnlyList<AgentContentBlock> ParseBlocks(string json)
    {
        var blocks = new List<AgentContentBlock>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return blocks;

            foreach (var value in document.RootElement.EnumerateArray())
            {
                var kind = Read(value, "kind") switch
                {
                    "text" => AgentContentKind.Text,
                    "image" => AgentContentKind.Image,
                    "thinking" => AgentContentKind.Thinking,
                    "tool_use" => AgentContentKind.ToolUse,
                    "tool_result" => AgentContentKind.ToolResult,
                    _ => (AgentContentKind?)null,
                };
                if (kind is null) continue;

                blocks.Add(new AgentContentBlock(
                    kind.Value,
                    Read(value, "text"),
                    Read(value, "toolUseId"),
                    Read(value, "toolName"),
                    value.TryGetProperty("json", out var raw)
                        ? raw.ValueKind == JsonValueKind.String ? raw.GetString() : raw.GetRawText()
                        : null,
                    value.TryGetProperty("isError", out var isError)
                        && isError.ValueKind == JsonValueKind.True,
                    value.TryGetProperty("providerItemId", out var providerItemId)
                        && providerItemId.ValueKind == JsonValueKind.String
                            ? providerItemId.GetString()
                            : null,
                    ImageUrl: Read(value, "imageUrl"),
                    MediaType: Read(value, "mediaType")));
            }
        }
        catch (JsonException)
        {
            // 跳过损坏的历史消息；运行仍可根据后续轮次作答。
        }

        return blocks;
    }

    /// <summary>按现有消息存储格式编码 Agent 内容块。</summary>
    internal static string SerializeBlocks(IReadOnlyList<AgentContentBlock> blocks)
    {
        var result = new JsonArray();
        foreach (var block in blocks)
        {
            var kind = block.Kind switch
            {
                AgentContentKind.Text => "text",
                AgentContentKind.Image => "image",
                AgentContentKind.Thinking => "thinking",
                AgentContentKind.ToolUse => "tool_use",
                AgentContentKind.ToolResult => "tool_result",
                _ => throw new ArgumentOutOfRangeException(),
            };
            result.Add((JsonNode)new JsonObject
            {
                ["kind"] = kind,
                ["text"] = block.Text,
                ["toolUseId"] = block.ToolUseId,
                ["toolName"] = block.ToolName,
                ["json"] = block.Json,
                ["isError"] = block.IsError,
                ["providerItemId"] = block.ProviderItemId,
                ["imageUrl"] = block.ImageUrl,
                ["mediaType"] = block.MediaType,
            });
        }
        return result.ToJsonString();
    }

    /// <summary>仅解析站内素材 URL 的标识，不获取任意外部地址。</summary>
    private static bool TryParseAssetUrl(string? url, out Guid assetId)
    {
        const string prefix = "/api/v1/canvas/assets/";
        assetId = default;
        if (url is null || !url.StartsWith(prefix, StringComparison.Ordinal)) return false;

        var idSegment = url[prefix.Length..].Split('?')[0];
        return Guid.TryParse(idSegment, out assetId) && assetId != Guid.Empty;
    }

}

/// <param name="Blocks">按出现顺序保存的消息内容块。</param>
/// <param name="Text">文本内容或本次流式文本片段。</param>
/// <param name="StopReason">上游模型结束当前轮次的原因。</param>
/// <param name="InputTokens">模型报告或运行累计的输入 token 数量。</param>
/// <param name="OutputTokens">模型报告或运行累计的输出 token 数量。</param>
/// <param name="Error">失败详情或错误分类，具体格式沿用所属契约。</param>
internal sealed record AgentModelTurn(
    IReadOnlyList<AgentContentBlock> Blocks,
    string Text,
    string? StopReason,
    int? InputTokens,
    int? OutputTokens,
    string? Error);
