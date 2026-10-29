using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using CreativeFlow.Api.Hubs;
using CreativeFlow.Application.Abstractions.Caching;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Realtime;
using CreativeFlow.Application.Contracts.Agent;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;
using CreativeFlow.Infrastructure.Ai;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Api;

/// <summary>真实 WebSocket/SignalR 帧与隔离 SQL，验证大画布回执、UTF-8 上限及连接授权。</summary>
public sealed class AgentHubTransportTests
{
    public static bool NoDatabase => ImageJobDatabase.NoDatabase;

    [Theory(Skip = "需要隔离测试库 CREATIVEFLOW_TEST_POSTGRES", SkipWhen = nameof(NoDatabase))]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolReceiptsAboveTheDefaultFrameLimitRemainBoundedAndAuthorized(bool oversized)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await AgentRunFixture.CreateAsync();
        fixture.Context.AccountId = new AccountId(fixture.Database.Account);
        await fixture.Agents.ClearExecutorConnectionAsync(fixture.Run.DeviceId, "fixture-browser", ct);
        var bridge = new AgentCanvasBridge(new SilentSink());
        await using var app = await CreateServerAsync(fixture, bridge, ct);
        var server = app.GetTestServer();
        using var socket = await server.CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/hubs/test"), ct);
        await SendAsync(socket, "{\"protocol\":\"json\",\"version\":1}", ct);
        _ = await ReceiveAsync(socket, ct);
        var claimed = await InvokeAsync(socket, "claim", "AttachAgentRun", [fixture.Run.Id.ToString(), fixture.Run.CanvasId.ToString()], ct);
        Assert.False(claimed.TryGetProperty("error", out _), claimed.GetRawText());
        var pending = bridge.ExecuteAsync(fixture.Run, "read-1", "read_canvas", "{}", TimeSpan.FromSeconds(10), ct);
        var resultJson = "{\"text\":\"" + new string('界', oversized ? 90_000 : 30_000) + "\"}";
        Assert.True(Encoding.UTF8.GetByteCount(resultJson) > 32 * 1024);
        var response = await InvokeAsync(socket, "result", "SubmitToolResult", [fixture.Run.Id.ToString(), "read-1", resultJson, false], ct);
        if (oversized)
        {
            Assert.True(response.TryGetProperty("error", out _));
            Assert.False(pending.IsCompleted);
            _ = await InvokeAsync(socket, "small-result", "SubmitToolResult", [fixture.Run.Id.ToString(), "read-1", "{}", false], ct);
            Assert.Equal("{}", (await pending).Json);
        }
        else
        {
            Assert.False(response.TryGetProperty("error", out _));
            Assert.Equal(resultJson, (await pending).Json);
            using var otherSocket = await server.CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/hubs/test"), ct);
            await SendAsync(otherSocket, "{\"protocol\":\"json\",\"version\":1}", ct);
            _ = await ReceiveAsync(otherSocket, ct);
            var denied = await InvokeAsync(otherSocket, "unauthorized-result", "SubmitToolResult", [fixture.Run.Id.ToString(), "read-1", "{}", false], ct);
            Assert.True(denied.TryGetProperty("error", out _));
        }
        var ping = await InvokeAsync(socket, "ping", "Ping", [], ct);
        Assert.Equal("pong", ping.GetProperty("result").GetString());
        socket.Abort();
    }

    private static async Task<WebApplication> CreateServerAsync(AgentRunFixture fixture, IAgentCanvasBridge bridge, CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IPresenceTracker, Presence>();
        builder.Services.AddSingleton<IAgentRepository>(fixture.Agents);
        builder.Services.AddSingleton(bridge);
        builder.Services.AddSignalR(options => options.MaximumReceiveMessageSize = AgentTransportLimits.HubMessageBytes);
        var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Items[DeviceContextItems.DeviceId] = fixture.Run.DeviceId;
            context.Items[DeviceContextItems.AccountId] = new AccountId(fixture.Database.Account);
            return next(context);
        });
        app.MapHub<CreativeFlowHub>("/hubs/test", options => options.ApplicationMaxBufferSize = AgentTransportLimits.HubMessageBytes);
        await app.StartAsync(ct);
        return app;
    }

    private static async Task<JsonElement> InvokeAsync(WebSocket socket, string id, string target, object[] arguments, CancellationToken ct)
    {
        await SendAsync(socket, JsonSerializer.Serialize(new { type = 1, invocationId = id, target, arguments }), ct);
        while (true)
        {
            var response = await ReceiveAsync(socket, ct);
            if (response.TryGetProperty("invocationId", out var invocation) && invocation.GetString() == id) return response;
        }
    }

    private static Task SendAsync(WebSocket socket, string json, CancellationToken ct) =>
        socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json + '\u001e')), WebSocketMessageType.Text, true, ct);

    private static async Task<JsonElement> ReceiveAsync(WebSocket socket, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        WebSocketReceiveResult part;
        do
        {
            part = await socket.ReceiveAsync(new ArraySegment<byte>(bytes), deadline.Token);
            Assert.NotEqual(WebSocketMessageType.Close, part.MessageType);
            buffer.Write(bytes, 0, part.Count);
        } while (!part.EndOfMessage);
        using var document = JsonDocument.Parse(Encoding.UTF8.GetString(buffer.ToArray()).TrimEnd('\u001e'));
        return document.RootElement.Clone();
    }

    private sealed class Presence : IPresenceTracker
    {
        public Task MarkOnlineAsync(DeviceId deviceId, string connectionId, CancellationToken ct) => Task.CompletedTask;
        public Task MarkOfflineAsync(DeviceId deviceId, string connectionId, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> IsOnlineAsync(DeviceId deviceId, CancellationToken ct) => Task.FromResult(true);
        public Task<long> ConnectionCountAsync(DeviceId deviceId, CancellationToken ct) => Task.FromResult(1L);
    }

    private sealed class SilentSink : IAgentRealtimeSink
    {
        public Task SendEventAsync(DeviceId deviceId, AgentEventEnvelope payload, CancellationToken ct) => Task.CompletedTask;
        public Task SendToolCallAsync(string connectionId, AgentToolCallEnvelope payload, CancellationToken ct) => Task.CompletedTask;
        public Task SendApprovalRequestAsync(string connectionId, AgentApprovalEnvelope payload, CancellationToken ct) => Task.CompletedTask;
    }
}
