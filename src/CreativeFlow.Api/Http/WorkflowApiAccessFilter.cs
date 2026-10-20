using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using CreativeFlow.Api.Middleware;
using CreativeFlow.Application.Abstractions.Identity;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;
using CreativeFlow.Infrastructure.Options;

namespace CreativeFlow.Api.Http;

/// <summary>隔离普通工作流和后台 API，并在已验证的后台请求内恢复资源归属。</summary>
public sealed class WorkflowApiAccessFilter(
    bool administrative,
    IWorkflowRepository workflows,
    IAccountRepository accounts,
    IAccountContext accountContext,
    ExternalWorkflowApiOptions options) : IAsyncResourceFilter
{
    /// <summary>在资源执行前验证设备或管理员身份，按持久化工作流归属建立可访问范围后交给后续管道。</summary>
    public async Task OnResourceExecutionAsync(
        ResourceExecutingContext context,
        ResourceExecutionDelegate next)
    {
        // 权限在模型绑定和共享用例之前判定；后台范围只来自持久化资源归属，不信任客户端设备参数。
        var http = context.HttpContext;
        var id = http.Request.RouteValues["id"]?.ToString();
        if (!administrative)
        {
            if (http.GetDeviceId() is null)
            {
                context.Result = ApiResults.Unauthorized();
                return;
            }
            if (WorkflowId.TryParse(id, out var workflowId)
                && await workflows.FindApiAsync(workflowId, http.RequestAborted, includeDeleted: true) is not null)
            {
                context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
                return;
            }
            await next();
            return;
        }

        if (http.GetAuthenticatedAdmin() is null)
        {
            context.Result = ApiResults.Unauthorized();
            return;
        }
        if (!options.Enabled)
        {
            context.Result = ApiResults.NotFound();
            return;
        }

        // 删除后只保留既有素材读取权限，不放开素材上传、草稿或执行端点。
        var retainedAssetRead = HttpMethods.IsGet(http.Request.Method)
            && context.ActionDescriptor.AttributeRouteInfo?.Name == "GetAdminWorkflowApiAsset";
        DeviceId deviceId;
        if (id is null)
        {
            deviceId = DeviceId.New();
        }
        else if (WorkflowId.TryParse(id, out var workflowId)
            && await workflows.FindApiAsync(workflowId, http.RequestAborted, retainedAssetRead) is { } workflow)
        {
            deviceId = workflow.DeviceId;
        }
        else
        {
            context.Result = ApiResults.NotFound();
            return;
        }

        var previousAccount = accountContext.AccountId;
        try
        {
            accountContext.AccountId = id is null ? null
                : await accounts.FindAccountForDeviceAsync(deviceId, http.RequestAborted);
            http.Items[WorkflowApiRequestContext.DeviceKey] = deviceId;
            http.Response.Headers.CacheControl = "no-store";
            await next();
        }
        finally
        {
            http.Items.Remove(WorkflowApiRequestContext.DeviceKey);
            accountContext.AccountId = previousAccount;
        }
    }
}

internal static class WorkflowApiRequestContext
{
    /// <summary>仅后台资源过滤器能设置的请求内设备标记。</summary>
    internal static readonly object DeviceKey = new();

    /// <summary>确认后台过滤器已验证管理员并限定 API 资源。</summary>
    internal static bool IsAdminWorkflowApi(this HttpContext http) =>
        http.GetAuthenticatedAdmin() is not null && http.Items[DeviceKey] is DeviceId;

    /// <summary>共享用例读取后台已验证资源归属或普通用户设备范围。</summary>
    internal static DeviceId? GetWorkflowDeviceId(this HttpContext http) =>
        http.IsAdminWorkflowApi() ? (DeviceId)http.Items[DeviceKey]! : http.GetDeviceId();

    /// <summary>为共享用例生成当前入口下的工作流地址。</summary>
    internal static string WorkflowBasePath(this HttpContext http) =>
        http.IsAdminWorkflowApi() ? "/api/v1/admin/workflow-apis" : "/api/v1/workflows";
}
