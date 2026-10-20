using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using CreativeFlow.Api.Http.Handlers;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Contracts.Device;
using CreativeFlow.Application.Features.Identity;

namespace CreativeFlow.Api.Controllers;

/// <summary>装配设备身份的 HTTP 入口及其用例/端口依赖。</summary>
[ApiController]
[Route("api/v1/device")]
[Tags("device")]
public sealed class DeviceController(
    ResolveDeviceIdentity resolver,
    IAccountRepository accounts,
    IValidator<DeviceSignalsPayload> validator,
    IHostEnvironment environment) : ControllerBase
{
    /// <summary>解析或创建调用方的设备身份。</summary>
    [HttpPost("handshake", Name = "DeviceHandshake")]
    public async Task<IActionResult> Handshake(
        [FromBody] DeviceSignalsPayload payload,
        CancellationToken ct) =>
        await DeviceHandlers.HandshakeAsync(
            payload,
            HttpContext,
            resolver,
            accounts,
            validator,
            environment,
            ct);

    /// <summary>返回当前 Cookie 附带的设备身份，但不创建身份。</summary>
    [HttpGet("me", Name = "CurrentDevice")]
    public IActionResult GetCurrent() => DeviceHandlers.GetCurrent(HttpContext);
}
