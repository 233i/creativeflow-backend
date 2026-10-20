using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Mvc;

namespace CreativeFlow.Api.Http;

/// <summary>
/// 供内部 HTTP Handler 使用的 MVC 结果工厂。
/// </summary>
/// <remarks>
/// Handler 不继承 <see cref="ControllerBase"/>，因此由此处集中创建标准 MVC result，
/// Controller 仍只负责路由、绑定和依赖注入。
/// </remarks>
internal static class ApiResults
{
    /// <summary>创建保持原有 MVC 序列化规则的 200 响应。</summary>
    public static IActionResult Ok() => new OkResult();

    /// <summary>创建保持原有 MVC 序列化规则的 200 响应。</summary>
    public static IActionResult Ok<T>(T value) => new OkObjectResult(value);

    /// <summary>创建 400 请求错误响应，不改写调用方提供的错误结构。</summary>
    public static IActionResult BadRequest() => new BadRequestResult();

    /// <summary>创建 400 请求错误响应，不改写调用方提供的错误结构。</summary>
    public static IActionResult BadRequest<T>(T value) => new BadRequestObjectResult(value);

    /// <summary>创建原有的 401 未认证响应。</summary>
    public static IActionResult Unauthorized() => new UnauthorizedResult();

    /// <summary>创建原有的 404 资源不存在响应。</summary>
    public static IActionResult NotFound() => new NotFoundResult();

    /// <summary>创建原有的 404 资源不存在响应。</summary>
    public static IActionResult NotFound<T>(T value) => new NotFoundObjectResult(value);

    /// <summary>创建原有的 409 业务冲突响应。</summary>
    public static IActionResult Conflict() => new ConflictResult();

    /// <summary>创建原有的 409 业务冲突响应。</summary>
    public static IActionResult Conflict<T>(T value) => new ConflictObjectResult(value);

    /// <summary>创建不带响应体的 204 响应。</summary>
    public static IActionResult NoContent() => new NoContentResult();

    /// <summary>创建 201 响应并保留资源 Location。</summary>
    public static IActionResult Created<T>(string location, T value) =>
        new CreatedResult(location, value);

    /// <summary>创建 202 响应并保留状态查询 Location。</summary>
    public static IActionResult Accepted<T>(string location, T value) =>
        new AcceptedResult(location, value);

    /// <summary>创建指向既有回跳地址的重定向响应。</summary>
    public static IActionResult Redirect(string location) => new RedirectResult(location);

    /// <summary>创建保留原有错误体的 500 响应。</summary>
    public static IActionResult InternalServerError<T>(T value) =>
        new ObjectResult(value) { StatusCode = StatusCodes.Status500InternalServerError };

    /// <summary>使用明确的 JSON 元数据创建指定状态码的响应。</summary>
    public static IActionResult Json<T>(
        T value,
        JsonTypeInfo<T> typeInfo,
        int statusCode) =>
        new JsonResult(value, typeInfo.Options) { StatusCode = statusCode };

    /// <summary>交付指定媒体类型的流，并保持调用方选择的 Range 支持。</summary>
    public static IActionResult Stream(
        Stream stream,
        string contentType,
        bool enableRangeProcessing = false) =>
        new FileStreamResult(stream, contentType)
        {
            EnableRangeProcessing = enableRangeProcessing,
        };
}
