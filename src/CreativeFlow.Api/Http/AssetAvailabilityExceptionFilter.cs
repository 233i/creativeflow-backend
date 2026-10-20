using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Npgsql;
using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Api.Http;

/// <summary>把数据库最后一道并发素材保护转换为可理解的冲突，而不是生成接口的 500。</summary>
public sealed class AssetAvailabilityExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not PostgresException { ConstraintName: "asset_not_available" }) return;
        context.Result = new ConflictObjectResult(new ErrorResponse("asset_not_available", ["素材已进入回收站或被清理，请先恢复或重新选择素材。"]));
        context.ExceptionHandled = true;
    }
}
