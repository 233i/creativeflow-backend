using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Infrastructure.Ai.Protocols;

/// <summary>四种文本协议共用一次上游请求；只转交回答正文，结束回执不由连接关闭代替。</summary>
internal static class TextStreamProtocol
{
    public static async Task<TextGenerationResult> SendAsync(IHttpClientFactory factory, TextGenerationRequest request,
        ProviderType protocol, string url, JsonObject body, Action<HttpRequestMessage> authorize, CancellationToken ct)
    {
        if (protocol == ProviderType.Gemini) url = url.Replace(":generateContent", ":streamGenerateContent?alt=sse", StringComparison.Ordinal);
        else body["stream"] = true;
        if (protocol == ProviderType.OpenAiChat) body["stream_options"] = new JsonObject { ["include_usage"] = true };
        using var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        message.Headers.Accept.ParseAdd("text/event-stream");
        authorize(message);
        using var response = await factory.CreateClient(request.HttpClientName).SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        var status = (int)response.StatusCode;
        var state = new StreamState(protocol);
        foreach (var header in new[] { "x-request-id", "request-id", "x-tt-logid" })
            if (response.Headers.TryGetValues(header, out var values)) { state.RequestId = values.FirstOrDefault(); break; }
        if (!response.IsSuccessStatusCode) return Result(false, "上游流式请求返回错误；未自动重试。");
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
            return Result(false, "上游未返回 SSE，请核对模型是否支持流式调用；未自动重试。");
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var bounded = new LimitedStream(source, 16 * 1024 * 1024);
        var full = new StringBuilder();
        var pending = new StringBuilder();
        var lastFlush = Stopwatch.GetTimestamp();
        try
        {
            await foreach (var frame in SseReader.ReadAsync(bounded, ct))
            {
                var delta = state.Read(frame);
                if (delta.Length > 0)
                {
                    full.Append(delta); pending.Append(delta);
                    if (full.Length > 256 * 1024) throw new InvalidDataException("模型流式输出超过大小限制。");
                    // 首段立即发送，随后合并很小的 token 帧，避免每个字符产生一次数据库事务。
                    if (full.Length == delta.Length || pending.Length >= 256 || Stopwatch.GetElapsedTime(lastFlush).TotalMilliseconds >= 100)
                        await FlushAsync();
                }
                if (state.Error is not null) return Result(false, state.Error);
                if (state.Done) break;
            }
            await FlushAsync();
            if (!state.Done && !(protocol == ProviderType.Gemini && state.Finish is not null))
                return Result(false, "上游流中断，未收到完整结束回执；未自动重试。");
            return Result(!string.IsNullOrWhiteSpace(full.ToString()), full.Length == 0 ? "上游未返回回答正文。" : null, full.ToString());
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidDataException or IOException or HttpRequestException)
        { return Result(false, "上游流中断或格式无效；未自动重试。"); }

        async Task FlushAsync()
        {
            if (pending.Length == 0) return;
            await request.OnTextDelta!(pending.ToString(), ct);
            pending.Clear(); lastFlush = Stopwatch.GetTimestamp();
        }

        TextGenerationResult Result(bool ok, string? error, string? text = null) => new(ok, ok ? text : null, status, error)
        { ResponseStatusCode = status, Usage = state.Usage.Count > 0 ? state.Usage : null,
            UpstreamRequestId = state.RequestId is { Length: <= 200 } ? state.RequestId : null,
            FinishReason = state.Finish is { Length: <= 100 } ? state.Finish : null };
    }

    private sealed class StreamState(ProviderType protocol)
    {
        public bool Done { get; private set; }
        public string? Finish { get; private set; }
        public string? Error { get; private set; }
        public string? RequestId { get; set; }
        public JsonObject Usage { get; } = new();

        public string Read(SseFrame frame)
        {
            if (protocol == ProviderType.OpenAiChat && frame.Data == "[DONE]")
            {
                Done = Finish is not null;
                if (!Done) Error = "上游没有返回结束原因；未自动重试。";
                return "";
            }
            var data = JsonNode.Parse(frame.Data) as JsonObject ?? throw new InvalidDataException();
            var type = Text(data["type"]);
            if (data["error"] is not null || type is "error" or "response.failed") { Error = "上游流式生成失败；未自动重试。"; return ""; }
            switch (protocol)
            {
                case ProviderType.OpenAiChat:
                    RequestId ??= TextOrNull(data["id"]);
                    CaptureUsage(data["usage"], "prompt_tokens", "completion_tokens", "total_tokens");
                    var choice = (data["choices"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(item => item["index"]?.GetValue<int>() is null or 0);
                    Finish = TextOrNull(choice?["finish_reason"]) ?? Finish;
                    return Text(choice?["delta"]?["content"]); // reasoning_content / tool_calls 不属于回答正文。
                case ProviderType.OpenAiResponses:
                    if (data["response"] is JsonObject result)
                    {
                        RequestId ??= TextOrNull(result["id"]);
                        CaptureUsage(result["usage"], "input_tokens", "output_tokens", "total_tokens");
                        if (type is "response.completed" or "response.incomplete")
                        {
                            Done = true;
                            Finish = type == "response.completed" ? "stop" : Text(result["incomplete_details"]?["reason"]) switch
                            { "max_output_tokens" => "length", "content_filter" => "content_filter", _ => "incomplete" };
                        }
                    }
                    return type == "response.output_text.delta" ? Text(data["delta"]) : "";
                case ProviderType.Anthropic:
                    if (type == "message_start")
                    {
                        RequestId ??= TextOrNull(data["message"]?["id"]);
                        CaptureUsage(data["message"]?["usage"], "input_tokens", "output_tokens");
                    }
                    if (type == "message_delta")
                    {
                        var finish = TextOrNull(data["delta"]?["stop_reason"]);
                        Finish = finish == "max_tokens" ? "length" : finish ?? Finish;
                        CaptureUsage(data["usage"], "input_tokens", "output_tokens");
                    }
                    if (type == "message_stop") { Done = Finish is not null; if (!Done) Error = "上游没有返回结束原因；未自动重试。"; }
                    return type == "content_block_delta" && Text(data["delta"]?["type"]) == "text_delta" ? Text(data["delta"]?["text"])
                        : type == "content_block_start" && Text(data["content_block"]?["type"]) == "text" ? Text(data["content_block"]?["text"]) : "";
                case ProviderType.Gemini:
                    RequestId ??= TextOrNull(data["responseId"]);
                    CaptureUsage(data["usageMetadata"], "promptTokenCount", "candidatesTokenCount", "totalTokenCount");
                    var candidate = (data["candidates"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(item => item["index"]?.GetValue<int>() is null or 0);
                    if (TextOrNull(candidate?["finishReason"]) is { } reason) Finish = reason switch { "MAX_TOKENS" => "length", "STOP" => "stop", _ => "content_filter" };
                    if (data["promptFeedback"]?["blockReason"] is not null) { Error = "上游拒绝了本次生成；未自动重试。"; return ""; }
                    return string.Concat((candidate?["content"]?["parts"] as JsonArray ?? []).OfType<JsonObject>()
                        .Where(part => part["thought"]?.GetValue<bool>() != true).Select(part => Text(part["text"])));
                default: throw new InvalidDataException();
            }
        }

        private void CaptureUsage(JsonNode? usage, params string[] keys)
        {
            if (usage is not JsonObject reported) return;
            foreach (var key in keys)
                if (reported[key] is JsonValue value && value.TryGetValue<long>(out var count) && count >= 0)
                    Usage[key switch { "input_tokens" or "promptTokenCount" => "prompt_tokens", "output_tokens" or "candidatesTokenCount" => "completion_tokens", "totalTokenCount" => "total_tokens", _ => key }] = count;
        }
        private static string Text(JsonNode? node) => TextOrNull(node) ?? "";
        private static string? TextOrNull(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    /// <summary>在 SSE 分帧之前限制字节总量，超长单行也不能绕过内存边界。</summary>
    private sealed class LimitedStream(Stream inner, long limit) : Stream
    {
        private long _read;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var count = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, 8192)], ct);
            _read += count;
            if (_read > limit) throw new InvalidDataException("Upstream stream exceeded its byte limit.");
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
