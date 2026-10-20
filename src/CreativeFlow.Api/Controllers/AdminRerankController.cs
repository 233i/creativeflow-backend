using Microsoft.AspNetCore.Mvc;
using CreativeFlow.Api.Middleware;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Knowledge;
using CreativeFlow.Domain.Ai;

namespace CreativeFlow.Api.Controllers;

/// <summary>管理员重排序配置与测试，独立鉴权并禁止缓存。</summary>
[ApiController]
[Route("api/v1/admin/ai/models/{modelId}/rerank")]
[Tags("admin")]
public sealed class AdminRerankController(RerankModelService service) : ControllerBase
{
    /// <summary>只向管理员读取已保存配置，不调用上游。</summary>
    [HttpGet("settings", Name = "GetRerankSettings")]
    public async Task<IActionResult> Get(string modelId, CancellationToken ct)
    {
        if (HttpContext.GetAuthenticatedAdmin() is null) return Unauthorized();
        if (!ModelId.TryParse(modelId, out var id)) return Invalid();
        return Map(await service.GetAsync(id, ct));
    }
    /// <summary>校验管理员及有界参数后追加不可变配置，不触发推理。</summary>
    [HttpPut("settings", Name = "SaveRerankSettings")]
    [RequestSizeLimit(16384)]
    public async Task<IActionResult> Save(string modelId, [FromBody] RerankSettings value, CancellationToken ct)
    {
        if (HttpContext.GetAuthenticatedAdmin() is null) return Unauthorized();
        if (!ModelId.TryParse(modelId, out var id)) return Invalid();
        return Map(await service.SaveAsync(id, value, ct));
    }
    /// <summary>使用已保存参数显式测试一次，不缓存结果或自动重试。</summary>
    [HttpPost("test", Name = "TestRerankModel")]
    [RequestSizeLimit(2097152)]
    public async Task<IActionResult> Test(string modelId, [FromBody] RerankTestRequest value, CancellationToken ct)
    {
        if (HttpContext.GetAuthenticatedAdmin() is null) return Unauthorized();
        if (!ModelId.TryParse(modelId, out var id)) return Invalid();
        return Map(await service.TestAsync(id, value, ct));
    }
    /// <summary>将配置异常转换为安全的 HTTP 错误，不回显上游正文。</summary>
    private IActionResult Map<T>(EmbeddingOperation<T> operation) where T : class
    {
        Response.Headers.CacheControl = "private, no-store";
        return operation.NotFound ? NotFound() : operation.Error is not null
            ? BadRequest(new ErrorResponse("invalid_request", [operation.Error])) : Ok(operation.Value);
    }
    /// <summary>返回格式或协议无效的安全错误，不交付部分排名。</summary>
    private IActionResult Invalid() => BadRequest(new ErrorResponse("invalid_request", ["Malformed model id."]));
}
