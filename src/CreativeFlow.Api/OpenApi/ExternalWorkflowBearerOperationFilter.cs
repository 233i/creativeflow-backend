using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace CreativeFlow.Api.OpenApi;

/// <summary>
/// 只有需要 API Key 的外部工作流接口使用 Bearer 鉴权。目录接口保持公开，站内接口继续使用
/// 现有的设备和账号 Cookie，避免 Swagger 错误地把全部接口标记成 Bearer 鉴权。
/// </summary>
internal sealed class ExternalWorkflowBearerOperationFilter : IOperationFilter
{
    /// <summary>为需要 Key 的外部工作流操作声明既有 OpenAPI Bearer 安全要求。</summary>
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var path = $"/{context.ApiDescription.RelativePath?.Split('?', 2)[0]}";
        if (!path.StartsWith("/api/external/v1/", StringComparison.Ordinal)
            || IsPublicCatalogRequest(path, context.ApiDescription.HttpMethod))
        {
            return;
        }

        operation.Security ??= [];
        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("bearer", context.Document)] = [],
        });
    }

    /// <summary>识别允许公开读取的外部工作流目录端点。</summary>
    private static bool IsPublicCatalogRequest(string path, string? method) =>
        string.Equals(method, HttpMethods.Get, StringComparison.OrdinalIgnoreCase)
        && path is "/api/external/v1/workflows" or "/api/external/v1/workflows/{id}";
}
