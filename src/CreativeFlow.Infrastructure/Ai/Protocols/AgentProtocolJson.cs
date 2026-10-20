using System.Text.Json;
using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;

namespace CreativeFlow.Infrastructure.Ai.Protocols;

internal static class AgentProtocolJson
{
    /// <summary>将工具参数或结果字符串解析为 JSON 节点，保持损坏输入的现有回退。</summary>
    public static JsonNode ParseNode(string json, string fallback)
    {
        try
        {
            return JsonNode.Parse(json) ?? JsonNode.Parse(fallback)!;
        }
        catch (JsonException)
        {
            return JsonNode.Parse(fallback)!;
        }
    }

    /// <summary>将持久消息块映射为 Anthropic 消息与工具结果结构。</summary>
    public static JsonArray AnthropicMessages(IReadOnlyList<AgentModelMessage> messages)
    {
        var result = new JsonArray();
        foreach (var message in messages)
        {
            var content = new JsonArray();
            foreach (var block in message.Content)
            {
                switch (block.Kind)
                {
                    case AgentContentKind.Text:
                        content.Add((JsonNode)new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = block.Text ?? string.Empty,
                        });
                        break;
                    case AgentContentKind.Image:
                        if (block.ImageBytes is { Length: > 0 }
                            && !string.IsNullOrWhiteSpace(block.MediaType))
                        {
                            content.Add((JsonNode)new JsonObject
                            {
                                ["type"] = "image",
                                ["source"] = new JsonObject
                                {
                                    ["type"] = "base64",
                                    ["media_type"] = block.MediaType,
                                    ["data"] = Convert.ToBase64String(block.ImageBytes),
                                },
                            });
                        }
                        break;
                    case AgentContentKind.ToolUse:
                        content.Add((JsonNode)new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = block.ToolUseId,
                            ["name"] = block.ToolName,
                            ["input"] = ParseNode(block.Json ?? "{}", "{}"),
                        });
                        break;
                    case AgentContentKind.ToolResult:
                        content.Add((JsonNode)new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = block.ToolUseId,
                            ["content"] = block.Text ?? block.Json ?? string.Empty,
                            ["is_error"] = block.IsError,
                        });
                        break;
                    // 思考内容用于界面进度，但第一阶段不会请求它。未来支持思考的客户端在重放前
                    // 必须保留 Anthropic 签名；静默重放重建文本会被拒绝。
                    case AgentContentKind.Thinking:
                        break;
                }
            }

            result.Add((JsonNode)new JsonObject
            {
                ["role"] = message.Role == AgentMessageRole.Assistant ? "assistant" : "user",
                ["content"] = content,
            });
        }

        return result;
    }

    /// <summary>将统一工具描述映射为 Anthropic 工具声明。</summary>
    public static JsonArray AnthropicTools(IReadOnlyList<AgentToolDefinition> tools)
    {
        var result = new JsonArray();
        foreach (var tool in tools)
        {
            result.Add((JsonNode)new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["input_schema"] = ParseNode(tool.InputSchemaJson, "{\"type\":\"object\"}"),
            });
        }
        return result;
    }

    /// <summary>将持久消息块映射为 Chat Completions 的角色和工具调用结构。</summary>
    public static JsonArray OpenAiMessages(
        string systemPrompt,
        IReadOnlyList<AgentModelMessage> messages)
    {
        // 工具调用和工具结果必须保留关联 ID 与原始次序，不能将不同内容块合并成普通文本。
        var result = new JsonArray();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            result.Add((JsonNode)new JsonObject { ["role"] = "system", ["content"] = systemPrompt });
        }

        foreach (var message in messages)
        {
            if (message.Role == AgentMessageRole.User)
            {
                foreach (var block in message.Content.Where(block => block.Kind == AgentContentKind.ToolResult))
                {
                    result.Add((JsonNode)new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = block.ToolUseId,
                        ["content"] = block.Text ?? block.Json ?? string.Empty,
                    });
                }

                var text = string.Concat(message.Content
                    .Where(block => block.Kind == AgentContentKind.Text)
                    .Select(block => block.Text));
                var images = message.Content
                    .Where(block => block.Kind == AgentContentKind.Image)
                    .Select(ImageDataUrl)
                    .Where(url => url is not null)
                    .Select(url => url!)
                    .ToList();
                if (text.Length > 0 && images.Count == 0)
                {
                    result.Add((JsonNode)new JsonObject { ["role"] = "user", ["content"] = text });
                }
                else if (text.Length > 0 || images.Count > 0)
                {
                    var content = new JsonArray();
                    if (text.Length > 0)
                    {
                        content.Add((JsonNode)new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = text,
                        });
                    }
                    foreach (var image in images)
                    {
                        content.Add((JsonNode)new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject { ["url"] = image },
                        });
                    }
                    result.Add((JsonNode)new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = content,
                    });
                }
                continue;
            }

            var assistant = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = string.Concat(message.Content
                    .Where(block => block.Kind == AgentContentKind.Text)
                    .Select(block => block.Text)),
            };
            var calls = new JsonArray();
            foreach (var block in message.Content.Where(block => block.Kind == AgentContentKind.ToolUse))
            {
                calls.Add((JsonNode)new JsonObject
                {
                    ["id"] = block.ToolUseId,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = block.ToolName,
                        ["arguments"] = block.Json ?? "{}",
                    },
                });
            }
            if (calls.Count > 0) assistant["tool_calls"] = calls;
            result.Add((JsonNode)assistant);
        }

        return result;
    }

    /// <summary>将统一工具描述映射为 Chat Completions 工具声明。</summary>
    public static JsonArray OpenAiTools(IReadOnlyList<AgentToolDefinition> tools)
    {
        var result = new JsonArray();
        foreach (var tool in tools)
        {
            result.Add((JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = ParseNode(tool.InputSchemaJson, "{\"type\":\"object\"}"),
                },
            });
        }
        return result;
    }

    /// <summary>
    /// 将持久化智能体历史转换为 Responses 输入项。Responses 将函数调用及其结果保留为独立顶层项，
    /// 而不是 Chat Completions 消息。
    /// </summary>
    public static JsonArray ResponsesInput(IReadOnlyList<AgentModelMessage> messages)
    {
        // Responses 的消息项和函数调用项使用各自的原生结构，保留 provider item ID 供后续轮次关联。
        var result = new JsonArray();

        foreach (var message in messages)
        {
            if (message.Role == AgentMessageRole.User)
            {
                foreach (var block in message.Content.Where(
                    block => block.Kind == AgentContentKind.ToolResult))
                {
                    result.Add((JsonNode)new JsonObject
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = block.ToolUseId,
                        ["output"] = block.Text ?? block.Json ?? string.Empty,
                    });
                }

                var text = string.Concat(message.Content
                    .Where(block => block.Kind == AgentContentKind.Text)
                    .Select(block => block.Text));
                var images = message.Content
                    .Where(block => block.Kind == AgentContentKind.Image)
                    .Select(ImageDataUrl)
                    .Where(url => url is not null)
                    .Select(url => url!)
                    .ToList();
                if (text.Length > 0 || images.Count > 0)
                {
                    var content = new JsonArray();
                    if (text.Length > 0)
                    {
                        content.Add((JsonNode)new JsonObject
                        {
                            ["type"] = "input_text",
                            ["text"] = text,
                        });
                    }
                    foreach (var image in images)
                    {
                        content.Add((JsonNode)new JsonObject
                        {
                            ["type"] = "input_image",
                            ["image_url"] = image,
                        });
                    }
                    result.Add((JsonNode)new JsonObject
                    {
                        ["type"] = "message",
                        ["role"] = "user",
                        ["content"] = content,
                    });
                }

                continue;
            }

            var assistantText = string.Concat(message.Content
                .Where(block => block.Kind == AgentContentKind.Text)
                .Select(block => block.Text));
            if (assistantText.Length > 0)
            {
                result.Add((JsonNode)new JsonObject
                {
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "output_text", ["text"] = assistantText }),
                });
            }

            foreach (var block in message.Content.Where(
                block => block.Kind == AgentContentKind.ToolUse))
            {
                var functionCall = new JsonObject
                {
                    ["type"] = "function_call",
                    ["call_id"] = block.ToolUseId,
                    ["name"] = block.ToolName,
                    ["arguments"] = block.Json ?? "{}",
                };
                if (block.ProviderItemId is { Length: > 0 } itemId)
                {
                    functionCall["id"] = itemId;
                }
                result.Add((JsonNode)functionCall);
            }
        }

        return result;
    }

    /// <summary>将图片字节编码为带 MIME 类型的 data URL。</summary>
    private static string? ImageDataUrl(AgentContentBlock block)
    {
        return block.ImageBytes is { Length: > 0 }
            && !string.IsNullOrWhiteSpace(block.MediaType)
            ? $"data:{block.MediaType};base64,{Convert.ToBase64String(block.ImageBytes)}"
            : null;
    }

    /// <summary>将统一工具描述映射为 Responses 工具声明。</summary>
    public static JsonArray ResponsesTools(IReadOnlyList<AgentToolDefinition> tools)
    {
        var result = new JsonArray();
        foreach (var tool in tools)
        {
            result.Add((JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = ParseNode(tool.InputSchemaJson, "{\"type\":\"object\"}"),
            });
        }
        return result;
    }

    /// <summary>按已支持的上游错误结构提取模型错误说明。</summary>
    public static string Error(string body, int statusCode)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String) return error.GetString() ?? $"HTTP {statusCode}";
                if (error.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString() ?? $"HTTP {statusCode}";
                }
            }
        }
        catch (JsonException)
        {
            // 代理可能返回 HTML。下方返回长度受限的正文。
        }

        var text = body.Trim();
        if (text.Length == 0) return $"Upstream request failed ({statusCode}).";
        return text.Length <= 400 ? text : text[..400] + "…";
    }
}
