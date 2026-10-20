using Microsoft.AspNetCore.Mvc;
using CreativeFlow.Api.Middleware;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Knowledge;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Api.Controllers;

/// <summary>管理员专用的向量配置和真实测试入口，不开放公共文本向量化代理。</summary>
[ApiController]
[Route("api/v1/admin/ai/models/{modelId}/embedding")]
[Tags("admin")]
public sealed class AdminEmbeddingController(EmbeddingModelService service) : ControllerBase
{
    /// <summary>读取已保存参数及版本，首次使用时返回默认草稿。</summary>
    [HttpGet("settings", Name = "GetEmbeddingSettings")]
    public async Task<IActionResult> Get(string modelId, CancellationToken ct)
    {
        if (HttpContext.GetAuthenticatedAdmin() is null) return Unauthorized();
        if (!ModelId.TryParse(modelId, out var id)) return InvalidId();
        return Map(await service.GetAsync(id, ct));
    }

    /// <summary>保存不可变参数版本，不触发推理或修改渠道密钥。</summary>
    [HttpPut("settings", Name = "SaveEmbeddingSettings")]
    [RequestSizeLimit(16384)]
    public async Task<IActionResult> Save(string modelId, [FromBody] EmbeddingSettings request, CancellationToken ct)
    {
        if (HttpContext.GetAuthenticatedAdmin() is null) return Unauthorized();
        if (!ModelId.TryParse(modelId, out var id)) return InvalidId();
        return Map(await service.SaveAsync(id, request, ct));
    }

    /// <summary>显式向所选模型发送测试文本，仅返回数量、维度、耗时和配置摘要。</summary>
    [HttpPost("test", Name = "TestEmbeddingModel")]
    [RequestSizeLimit(2097152)]
    public async Task<IActionResult> Test(string modelId, [FromBody] EmbeddingTestRequest request, CancellationToken ct)
    {
        if (HttpContext.GetAuthenticatedAdmin() is null) return Unauthorized();
        if (!ModelId.TryParse(modelId, out var id)) return InvalidId();
        return Map(await service.TestAsync(id, request, ct));
    }

    /// <summary>禁止缓存配置与测试响应，按既有错误体区分无效输入和不可见模型。</summary>
    private IActionResult Map<T>(EmbeddingOperation<T> result) where T : class
    {
        Response.Headers.CacheControl = "private, no-store";
        if (result.NotFound) return NotFound();
        return result.Error is not null ? BadRequest(new ErrorResponse("invalid_request", [result.Error])) : Ok(result.Value);
    }

    /// <summary>拒绝格式错误的模型 ID，不触发资源查询或模型调用。</summary>
    private IActionResult InvalidId() => BadRequest(new ErrorResponse("invalid_request", ["Malformed model id."]));
}
