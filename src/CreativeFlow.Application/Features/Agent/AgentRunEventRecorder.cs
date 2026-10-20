using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Realtime;
using CreativeFlow.Application.Contracts.Agent;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Application.Features.Agent;

/// <summary>拥有 Agent 事件的持久化与推送顺序，不重试通知或重新执行业务。</summary>
public sealed class AgentRunEventRecorder(IAgentRepository agents, IAgentRealtimeSink realtime)
{
    /// <summary>先追加持久事件，再发送带仓储序号的实时通知。</summary>
    internal async Task RecordAsync(
        AgentRun run,
        string type,
        string payloadJson,
        string? text,
        string? error,
        CancellationToken ct)
    {
        var stored = await agents.AppendEventAsync(run.Id, run.DeviceId, type, payloadJson, ct);
        if (stored is null) return;

        await realtime.SendEventAsync(
            run.DeviceId,
            new AgentEventEnvelope(
                run.Id.ToString(), stored.Sequence, type, text, payloadJson, error,
                stored.CreatedAt.ToString("O")),
            ct);
    }

    /// <summary>文本和思考增量只即时发送，序号固定为零，不占用持久事件序号。</summary>
    internal Task PublishDeltaAsync(AgentRun run, string type, string text, CancellationToken ct) =>
        realtime.SendEventAsync(run.DeviceId,
            new AgentEventEnvelope(run.Id.ToString(), 0, type, text, null, null,
                DateTimeOffset.UtcNow.ToString("O")), ct);
}
