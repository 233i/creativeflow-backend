using Microsoft.AspNetCore.Mvc;
using CreativeFlow.Api.Http.Handlers;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Api.Controllers;

/// <summary>装配渠道 URL 配置导入的 HTTP 入口及其用例/端口依赖。</summary>
[ApiController]
[Route("api/v1/admin/ai/config")]
[Tags("admin")]
public sealed class AiConfigImportController(
    IProviderRepository providers,
    IApiKeyProtector protector) : ControllerBase
{
    /// <summary>校验并导入 URL 中的渠道配置，保持原有重复项处理策略。</summary>
    [HttpPost("import", Name = "ImportAiConfig")]
    public async Task<IActionResult> Import(
        [FromBody] ImportConfigRequest request,
        CancellationToken ct) =>
        await AiConfigImportHandlers.ImportAsync(request, HttpContext, providers, protector, ct);
}
