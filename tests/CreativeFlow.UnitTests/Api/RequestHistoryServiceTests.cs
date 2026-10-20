using System.Text.Json;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Features.Administration;
using CreativeFlow.Domain.Identity;
using CreativeFlow.UnitTests.Support;

namespace CreativeFlow.UnitTests.Api;

/// <summary>锁定请求产物解析、历史输入和媒体访问范围。</summary>
public sealed class RequestHistoryServiceTests
{
    /// <summary>创建同设备的媒体元数据，避免测试依赖真实存储。</summary>
    private static CanvasAsset Asset(Guid id, Guid device) => new(id, new DeviceId(device), "image", "image/png", "private/key", 4, null, DateTimeOffset.UnixEpoch);

    /// <summary>多图保留全部位置，缺失和其他用户素材只返回占位。</summary>
    [Fact]
    public async Task MissingForeignAndExternalAssetsNeverBecomeAdminUrls()
    {
        var owned = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var missing = Guid.NewGuid();
        var document = RecordingRequestHistory.CreateDocument(output: JsonSerializer.Serialize(new
        {
            assets = new[] { new { url = $"/api/v1/canvas/assets/{owned}" }, new { url = $"/api/v1/canvas/assets/{foreign}" },
                new { url = $"/api/v1/canvas/assets/{missing}" }, new { url = "https://untrusted.invalid/image.png" } },
        }));
        var repository = new RecordingRequestHistory { Document = document };
        repository.Assets.AddRange([Asset(owned, document.DeviceId), Asset(foreign, Guid.NewGuid())]);
        var service = new RequestHistoryService(repository);
        var id = Guid.Parse(document.Summary.Id);
        var result = await service.GetAsync("canvas", id, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal(4, result.Artifacts.Length);
        Assert.Equal($"/api/v1/admin/requests/canvas/{id}/assets/{owned}", result.Artifacts[0].Url);
        Assert.All(result.Artifacts.Skip(1), artifact => { Assert.True(artifact.Unavailable); Assert.Null(artifact.Url); });
        Assert.Null(await service.GetAssetAsync("canvas", id, foreign, TestContext.Current.CancellationToken));
        Assert.Null(await service.GetAssetAsync("canvas", id, Guid.NewGuid(), TestContext.Current.CancellationToken));
        Assert.Null(await service.GetAssetAsync("workflow", id, owned, TestContext.Current.CancellationToken));
        Assert.NotNull(await service.GetAssetAsync("canvas", id, owned, TestContext.Current.CancellationToken));
    }

    /// <summary>工作流按输出节点汇总交付物，所有节点保留运行时参数与复用状态。</summary>
    [Fact]
    public async Task WorkflowUsesSnapshotAndFinalOutputsWhilePreservingFailedNodeDetails()
    {
        var media = Guid.NewGuid();
        var document = RecordingRequestHistory.CreateDocument("workflow", "workflow") with
        {
            InputJson = "{\"nodes\":[{\"id\":\"generate\",\"data\":{\"prompt\":\"运行时提示词\"}}]}",
            Nodes = [
                new("generate", "imageGenerate", "succeeded", true, "{\"texts\":[\"中间文本\"]}", null, null, null),
                new("output", "imageOutput", "succeeded", false, $"{{\"images\":[\"/api/v1/canvas/assets/{media}\"]}}", null, null, null),
                new("failed", "videoGenerate", "failed", false, null, "渠道拒绝请求", null, null),
            ],
        };
        var repository = new RecordingRequestHistory { Document = document };
        repository.Assets.Add(Asset(media, document.DeviceId));
        var result = await new RequestHistoryService(repository).GetAsync("workflow", Guid.Parse(document.Summary.Id), TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal(media.ToString(), Assert.Single(result.Artifacts).AssetId);
        Assert.Equal("运行时提示词", result.Nodes[0].Input.GetProperty("prompt").GetString());
        Assert.True(result.Nodes[0].Reused);
        Assert.Equal("中间文本", Assert.Single(result.Nodes[0].Artifacts).Text);
        Assert.Equal("渠道拒绝请求", result.Nodes[2].Error);
    }

    /// <summary>没有结果节点的失败运行仍显示已完成产物，损坏节点不影响其他节点。</summary>
    [Fact]
    public async Task FailedWorkflowKeepsPartialOutputsAndToleratesMalformedLegacyJson()
    {
        var document = RecordingRequestHistory.CreateDocument("workflow", "workflow") with
        {
            InputJson = "broken",
            Nodes = [new("text", "textInput", "succeeded", false, "{\"texts\":[\"保留正文\"]}", null, null, null),
                new("bad", "imageGenerate", "failed", false, "broken", "失败", null, null)],
        };
        var result = await new RequestHistoryService(new RecordingRequestHistory { Document = document })
            .GetAsync("workflow", Guid.Parse(document.Summary.Id), TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal("保留正文", Assert.Single(result.Artifacts).Text);
        Assert.Empty(result.Nodes[1].Artifacts);
    }
}
