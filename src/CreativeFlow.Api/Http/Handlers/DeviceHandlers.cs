using FluentValidation;
using CreativeFlow.Api.Hubs;
using CreativeFlow.Api.Middleware;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Device;
using CreativeFlow.Application.Features.Identity;

namespace CreativeFlow.Api.Http.Handlers;

internal static class DeviceHandlers
{
    /// <summary>
    /// 解析或创建调用方的设备身份。
    /// </summary>
    /// <remarks>
    /// 该操作具有幂等性，因此客户端可在每次页面加载时调用。提供有效 Cookie 的调用方
    /// 无需指纹识别，即可按 1.0 置信度取回相同身份。
    /// </remarks>
    internal static async Task<IActionResult> HandshakeAsync(
        DeviceSignalsPayload payload,
        HttpContext http,
        ResolveDeviceIdentity resolver,
        IAccountRepository accounts,
        IValidator<DeviceSignalsPayload> validator,
        IHostEnvironment environment,
        CancellationToken ct)
    {
        // 设备是账户作用域机制，而不是替代凭据。注册和登录可在没有设备的情况下运行；
        // 它们的会话 Cookie 会授权首次握手。
        if (http.GetAuthenticatedAccountId() is null)
        {
            return ApiResults.Unauthorized();
        }

        var validation = await validator.ValidateAsync(payload, ct);
        if (!validation.IsValid)
        {
            return ApiResults.BadRequest(new ErrorResponse(
                "invalid_signals",
                validation.Errors.Select(e => e.ErrorMessage).ToArray()));
        }

        var result = await resolver.HandleAsync(
            payload,
            DeviceCookie.Read(http),
            http.Connection.RemoteIpAddress,
            ct);

        // 每次握手都会重新签发，包括概率性匹配，使确定性层尽快重新接管，
        // 弱指纹路径不再影响后续请求。
        DeviceCookie.Write(http, result.DeviceId, environment.IsDevelopment());

        // 已登录用户可能只带会话 Cookie 在新浏览器中访问。握手时，该新的确定性设备会成为账户设备，
        // 此后所有账户作用域仓储都能看到云端数据。
        if (http.GetAuthenticatedAccountId() is { } accountId)
        {
            await accounts.LinkDeviceAsync(accountId, result.DeviceId, ct);
        }

        return ApiResults.Ok(new DeviceHandshakeResponse(
            result.DeviceId.ToString(),
            result.VisitorId.ToString(),
            result.IsNewDevice,
            result.Confidence.Value,
            result.Method.ToString()));
    }

    /// <summary>
    /// 返回当前 Cookie 附带的身份。绝不创建身份；没有 Cookie 的调用方会收到 404，
    /// 并应调用握手端点。
    /// </summary>
    internal static IActionResult GetCurrent(HttpContext http)
    {
        if (http.GetDeviceId() is not { } deviceId)
        {
            return ApiResults.NotFound();
        }

        var visitorId = http.GetVisitorId();

        return ApiResults.Ok(new DeviceHandshakeResponse(
            deviceId.ToString(),
            visitorId?.ToString() ?? string.Empty,
            IsNewDevice: false,
            Confidence: 1.0d,
            Method: "Deterministic"));
    }
}
