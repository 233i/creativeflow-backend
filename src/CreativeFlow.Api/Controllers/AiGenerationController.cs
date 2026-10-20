using Microsoft.AspNetCore.Mvc;
using CreativeFlow.Api.Http.Handlers;
using CreativeFlow.Application.Contracts.Ai;
using CreativeFlow.Application.Features.Generation;

namespace CreativeFlow.Api.Controllers;

/// <summary>装配图片与文本生成的 HTTP 入口及其用例/端口依赖。</summary>
[ApiController]
[Route("api/v1/ai")]
[Tags("ai")]
public sealed class AiGenerationController(
    GenerateImagesUseCase generateImages,
    GenerateTextUseCase generateText) : ControllerBase
{
    /// <summary>按原有模型和素材策略生成图片并保存结果。</summary>
    [HttpPost("images", Name = "GenerateImages")]
    public async Task<IActionResult> GenerateImages(
        [FromBody] GenerateImageRequest request,
        CancellationToken ct) =>
        await AiGenerationHandlers.GenerateImagesAsync(
            request,
            HttpContext,
            generateImages,
            ct);

    /// <summary>按所选协议生成文本，保留参考图的现有处理策略。</summary>
    [HttpPost("text", Name = "GenerateText")]
    public async Task<IActionResult> GenerateText(
        [FromBody] GenerateTextRequest request,
        CancellationToken ct) =>
        await AiGenerationHandlers.GenerateTextAsync(
            request,
            HttpContext,
            generateText,
            ct);
}
