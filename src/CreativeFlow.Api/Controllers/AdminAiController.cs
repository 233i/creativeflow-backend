using Microsoft.AspNetCore.Mvc;
using CreativeFlow.Api.Http.Handlers;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Security;
using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Api.Controllers;

/// <summary>装配全局 AI 配置的 HTTP 入口及其用例/端口依赖。</summary>
[ApiController]
[Route("api/v1/admin/ai")]
[Tags("admin")]
public sealed class AdminAiController(
    IProviderRepository providers,
    IApiKeyProtector protector,
    IUpstreamModelCatalog catalog,
    IModelSlotRepository slots,
    IImageGenerationSettingsRepository imageSettings) : ControllerBase
{
    /// <summary>列出管理员可管理的全局 AI 渠道。</summary>
    [HttpGet("providers", Name = "ListAdminProviders")]
    public async Task<IActionResult> ListProviders(CancellationToken ct) =>
        await AiProviderHandlers.ListAdminAsync(HttpContext, providers, ct);

    /// <summary>创建全局 AI 渠道配置。</summary>
    [HttpPost("providers", Name = "CreateProvider")]
    public async Task<IActionResult> CreateProvider(
        [FromBody] CreateProviderRequest request,
        CancellationToken ct) =>
        await AiProviderHandlers.CreateAsync(request, HttpContext, providers, protector, ct);

    /// <summary>更新全局 AI 渠道配置，保留原有密钥更新语义。</summary>
    [HttpPatch("providers/{id}", Name = "UpdateProvider")]
    public async Task<IActionResult> UpdateProvider(
        [FromRoute] string id,
        [FromBody] UpdateProviderRequest request,
        CancellationToken ct) =>
        await AiProviderHandlers.UpdateAsync(
            id,
            request,
            HttpContext,
            providers,
            protector,
            ct);

    /// <summary>删除指定全局 AI 渠道。</summary>
    [HttpDelete("providers/{id}", Name = "DeleteProvider")]
    public async Task<IActionResult> DeleteProvider([FromRoute] string id, CancellationToken ct) =>
        await AiProviderHandlers.DeleteAsync(id, HttpContext, providers, ct);

    /// <summary>检查指定渠道的上游可用性。</summary>
    [HttpPost("providers/{id}/check", Name = "CheckProvider")]
    public async Task<IActionResult> CheckProvider([FromRoute] string id, CancellationToken ct) =>
        await AiProviderHandlers.CheckAsync(
            id,
            HttpContext,
            providers,
            protector,
            catalog,
            ct);

    /// <summary>读取指定渠道提供的上游模型目录。</summary>
    [HttpGet("providers/{id}/upstream-models", Name = "ListUpstreamModels")]
    public async Task<IActionResult> ListUpstreamModels(
        [FromRoute] string id,
        CancellationToken ct) =>
        await AiProviderHandlers.ListUpstreamModelsAsync(
            id,
            HttpContext,
            providers,
            protector,
            catalog,
            ct);

    /// <summary>向指定渠道添加模型配置。</summary>
    [HttpPost("providers/{id}/models", Name = "AddModel")]
    public async Task<IActionResult> AddModel(
        [FromRoute] string id,
        [FromBody] CreateModelRequest request,
        CancellationToken ct) =>
        await AiProviderHandlers.AddModelAsync(id, request, HttpContext, providers, ct);

    /// <summary>导入模型目录，保留已有模型的用户配置。</summary>
    [HttpPost("providers/{id}/models/import", Name = "ImportModels")]
    public async Task<IActionResult> ImportModels(
        [FromRoute] string id,
        [FromBody] ImportModelsRequest request,
        CancellationToken ct) =>
        await AiProviderHandlers.ImportModelsAsync(id, request, HttpContext, providers, ct);

    /// <summary>更新指定模型的能力与参数配置。</summary>
    [HttpPatch("models/{modelId}", Name = "UpdateModel")]
    public async Task<IActionResult> UpdateModel(
        [FromRoute] string modelId,
        [FromBody] UpdateModelRequest request,
        CancellationToken ct) =>
        await AiProviderHandlers.UpdateModelAsync(
            modelId,
            request,
            HttpContext,
            providers,
            ct);

    /// <summary>删除指定模型配置。</summary>
    [HttpDelete("models/{modelId}", Name = "DeleteModel")]
    public async Task<IActionResult> DeleteModel(
        [FromRoute] string modelId,
        CancellationToken ct) =>
        await AiProviderHandlers.DeleteModelAsync(modelId, HttpContext, providers, ct);

    /// <summary>读取管理员配置的默认模型角色。</summary>
    [HttpGet("model-slots", Name = "ListAdminModelSlots")]
    public async Task<IActionResult> ListModelSlots(CancellationToken ct) =>
        await AiProviderHandlers.ListModelSlotsAsync(HttpContext, slots, ct);

    /// <summary>修改指定默认模型角色的选择。</summary>
    [HttpPut("model-slots/{kind}", Name = "UpdateAdminModelSlot")]
    public async Task<IActionResult> UpdateModelSlot(
        [FromRoute] string kind,
        [FromBody] UpdateModelSlotRequest request,
        CancellationToken ct) =>
        await AiProviderHandlers.UpdateModelSlotAsync(
            kind,
            request,
            HttpContext,
            providers,
            slots,
            ct);

    /// <summary>读取管理员设置的图片生成策略。</summary>
    [HttpGet("image-generation-settings", Name = "GetAdminImageGenerationSettings")]
    public async Task<IActionResult> GetImageGenerationSettings(CancellationToken ct) =>
        await AiProviderHandlers.GetImageGenerationSettingsAsync(
            HttpContext,
            imageSettings,
            ct);

    /// <summary>更新应用级图片生成策略。</summary>
    [HttpPut("image-generation-settings", Name = "UpdateAdminImageGenerationSettings")]
    public async Task<IActionResult> UpdateImageGenerationSettings(
        [FromBody] UpdateImageGenerationSettingsRequest request,
        CancellationToken ct) =>
        await AiProviderHandlers.UpdateImageGenerationSettingsAsync(
            request,
            HttpContext,
            imageSettings,
            ct);
}
