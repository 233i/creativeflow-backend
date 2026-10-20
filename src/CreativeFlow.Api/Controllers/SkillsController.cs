using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using CreativeFlow.Api.Http.Handlers;
using CreativeFlow.Application.Abstractions.Ai;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Infrastructure.Options;

namespace CreativeFlow.Api.Controllers;

/// <summary>装配Skill 包的 HTTP 入口及其用例/端口依赖。</summary>
[ApiController]
[Route("api/v1/skills")]
[Tags("skills")]
public sealed class SkillsController(
    ISkillRepository skills,
    ISkillPackageScanner scanner,
    ISkillStore store,
    IOptions<SkillsOptions> options,
    ILoggerFactory loggerFactory) : ControllerBase
{
    /// <summary>列出Skill 包，保留现有筛选、排序与访问范围。</summary>
    [HttpGet("", Name = "ListSkills")]
    public async Task<IActionResult> List(CancellationToken ct) =>
        await SkillHandlers.ListAsync(HttpContext, skills, options, ct);

    /// <summary>读取Skill 包，保留缺失资源的既有处理。</summary>
    [HttpGet("{id}", Name = "GetSkill")]
    public async Task<IActionResult> Get([FromRoute] string id, CancellationToken ct) =>
        await SkillHandlers.GetAsync(id, HttpContext, skills, ct);

    /// <summary>在当前上传限制内扫描 Skill 包结构与风险。</summary>
    [HttpPost("scan", Name = "ScanSkill")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Scan(IFormFile file, CancellationToken ct) =>
        await SkillHandlers.ScanAsync(file, HttpContext, scanner, options, ct);

    /// <summary>保存已经确认的 Skill 包及其元数据。</summary>
    [HttpPost("", Name = "InstallSkill")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Install(
        IFormFile file,
        [FromQuery] bool confirmDanger,
        CancellationToken ct) =>
        await SkillHandlers.InstallAsync(
            file,
            confirmDanger,
            HttpContext,
            scanner,
            store,
            skills,
            options,
            ct);

    /// <summary>删除Skill 包，保留不存在或不可访问时的返回语义。</summary>
    [HttpDelete("{id}", Name = "DeleteSkill")]
    public async Task<IActionResult> Delete([FromRoute] string id, CancellationToken ct) =>
        await SkillHandlers.DeleteAsync(id, HttpContext, skills, store, loggerFactory, ct);
}
