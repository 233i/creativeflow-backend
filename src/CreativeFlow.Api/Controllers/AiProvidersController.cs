using Microsoft.AspNetCore.Mvc;
using CreativeFlow.Api.Http.Handlers;
using CreativeFlow.Application.Abstractions.Persistence;

namespace CreativeFlow.Api.Controllers;

/// <summary>装配全局渠道目录的 HTTP 入口及其用例/端口依赖。</summary>
[ApiController]
[Route("api/v1/ai")]
[Tags("ai")]
public sealed class AiProvidersController(
    IProviderRepository providers,
    IModelSlotRepository slots) : ControllerBase
{
    /// <summary>列出全局渠道目录，保留现有筛选、排序与访问范围。</summary>
    [HttpGet("providers", Name = "ListProviders")]
    public async Task<IActionResult> List(CancellationToken ct) =>
        await AiProviderHandlers.ListAsync(HttpContext, providers, ct);

    /// <summary>读取管理员配置的默认模型角色。</summary>
    [HttpGet("model-slots", Name = "ListModelSlots")]
    public async Task<IActionResult> ListModelSlots(CancellationToken ct) =>
        await AiProviderHandlers.ListModelSlotsAsync(HttpContext, slots, ct);
}
