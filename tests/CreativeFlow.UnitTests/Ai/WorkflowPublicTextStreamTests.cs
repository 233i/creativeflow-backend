using System.Text.Json.Nodes;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Features.Workflow;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Ai;

public sealed class WorkflowPublicTextStreamTests
{
    [Theory]
    [InlineData("{\"other\":{\"text\":\"secret\"},\"text\":\"回答\\n第二行\\uD83D\\uDE00\",\"citations\":[\"private\"]}", "回答\n第二行😀")]
    [InlineData("{\"text\":\"引号\\\"和斜杠\\\\完成\"}", "引号\"和斜杠\\完成")]
    [InlineData("{\"text\":\"你好\"}", "你好")]
    public void JsonPreviewHandlesEverySplitAndDoesNotLeakOtherProperties(string json, string expected)
    {
        var last = "";
        for (var length = 1; length <= json.Length; length++)
        {
            var text = JsonStringPreview.Read(json[..length], "text");
            if (text is null) continue;
            Assert.StartsWith(last, text); Assert.StartsWith(text, expected); last = text;
        }
        Assert.Equal(expected, last);
    }

    [Fact]
    public async Task OnlyPublishedOutputsArePersistedAndOptInIsRequired()
    {
        var plan = WorkflowV2GraphValidator.Validate(WorkflowV2Fixtures.Graph()).Plan!;
        var model = plan.Nodes.Single(n => n.Type == "llm"); var events = new Events(); var stream = new WorkflowPublicTextStream(events);
        var now = DateTimeOffset.UtcNow;
        var run = new WorkflowRun(WorkflowRunId.New(), WorkflowId.New(), DeviceId.New(), "{}", null, WorkflowRunStatus.Running, null, now, now, now, null)
        { TriggerType = WorkflowRunTriggerType.Api, InputJson = "{}" };
        stream.Configure(run, plan); Assert.Null(stream.Receiver(model));
        stream.Configure(run with { InputJson = "{\"stream\":true}" }, plan);
        Assert.Null(stream.Receiver(model with { Id = "internal" }));
        var receiver = stream.Receiver(model)!;
        await receiver("first", TestContext.Current.CancellationToken); await receiver(" second", TestContext.Current.CancellationToken);
        Assert.Equal("first second", string.Concat(events.Items.Select(item => item["delta"]!.GetValue<string>())));
        Assert.All(events.Items, item => { Assert.Equal("answer", item["output"]!.GetValue<string>()); Assert.True(item["provisional"]!.GetValue<bool>()); });
    }

    [Fact]
    public void KnowledgeAndMergeProjectionDoesNotIncludeCitationsOrIntermediateQuery()
    {
        var graph = WorkflowBranchFixtures.KnowledgeGraph(Guid.NewGuid(), Guid.NewGuid());
        WorkflowV2Fixtures.AddKnowledgeQueryExtraction(graph);
        var plan = WorkflowV2GraphValidator.Validate(graph).Plan!;
        var target = Assert.Single(WorkflowPublicTextStream.Targets(plan, plan.Nodes.Single(n => n.Type == "knowledgeAnswer")));
        Assert.Equal("/text", target.Path); Assert.Equal("text", target.JsonField);
        Assert.Empty(WorkflowPublicTextStream.Targets(plan, plan.Nodes.Single(n => n.Type == "parameterExtract")));
    }

    private sealed class Events : IWorkflowRunEventRepository
    {
        public List<JsonObject> Items { get; } = [];
        public Task AppendAsync(WorkflowRunId runId, string data, CancellationToken ct) { Items.Add(JsonNode.Parse(data)!.AsObject()); return Task.CompletedTask; }
        public Task<IReadOnlyList<WorkflowRunTextEvent>> ReadAsync(WorkflowRunId runId, long after, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> LastSequenceAsync(WorkflowRunId runId, CancellationToken ct) => Task.FromResult((long)Items.Count);
    }
}
