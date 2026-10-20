using Microsoft.AspNetCore.Mvc;
using CreativeFlow.Api.Http.Handlers;
using CreativeFlow.Api.Http;
using CreativeFlow.Application.Abstractions.Persistence;
using CreativeFlow.Application.Abstractions.Realtime;
using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Api.Controllers;

/// <summary>装配工作流的 HTTP 入口及其用例/端口依赖。</summary>
[ApiController]
[Route("api/v1/admin/workflow-apis")]
[Tags("admin")]
[TypeFilter(typeof(WorkflowApiAccessFilter), Arguments = new object[] { true })]
public sealed class AdminWorkflowApisController(
    IWorkflowRepository workflows,
    IWorkflowRunRepository runs,
    IRealtimeNotifier realtime) : ControllerBase
{
    /// <summary>获取后台编辑器可用的工作流节点能力。</summary>
    [HttpGet("capabilities", Name = "AdminWorkflowCapabilities")]
    public IActionResult Capabilities() => WorkflowV2Handlers.Capabilities(HttpContext);

    /// <summary>在后台提交工作流输入；不改变已发布外部 API 的契约。</summary>
    [HttpPost("{id}/runs", Name = "AdminStartWorkflowRun")]
    public Task<IActionResult> StartRun([FromRoute] string id,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] System.Text.Json.Nodes.JsonObject? request,
        [FromServices] CreativeFlow.Application.Abstractions.Ai.IWorkflowV2Models models,
        [FromServices] CreativeFlow.Application.Abstractions.Ai.IWorkflowScriptRunner scripts, CancellationToken ct) =>
        WorkflowHandlers.StartAsync(id, request, HttpContext, workflows, runs, models, scripts, ct);

    /// <summary>管理后台创建独立节点调试，使用管理员资源授权但不执行上下游。</summary>
    [HttpPost("{id}/nodes/{nodeId}/debug-runs", Name = "AdminStartWorkflowNodeDebug")]
    public Task<IActionResult> DebugNode([FromRoute] string id, [FromRoute] string nodeId,
        [FromBody] System.Text.Json.Nodes.JsonObject request,
        [FromServices] CreativeFlow.Application.Abstractions.Ai.IWorkflowV2Models models,
        [FromServices] CreativeFlow.Application.Abstractions.Ai.IWorkflowScriptRunner scripts, CancellationToken ct) =>
        WorkflowNodeDebugHandlers.StartAsync(id, nodeId, request, HttpContext, workflows, runs, models, scripts, ct);

    /// <summary>读取同工作流的历史绑定结果，知识证据保留当前资源权限检查。</summary>
    [HttpGet("{id}/nodes/{nodeId}/debug-inputs", Name = "AdminGetWorkflowNodeDebugInputs")]
    public Task<IActionResult> DebugInputs([FromRoute] string id, [FromRoute] string nodeId, [FromQuery] string sourceRunId, CancellationToken ct) =>
        WorkflowNodeDebugHandlers.InputsAsync(id, nodeId, sourceRunId, HttpContext, workflows, runs, ct);

    /// <summary>列出工作流，保留现有筛选、排序与访问范围。</summary>
    [HttpGet("", Name = "AdminListWorkflows")]
    public async Task<IActionResult> List(CancellationToken ct) =>
        await AdminWorkflowApiHandlers.ListAsync(HttpContext, workflows, ct);

    /// <summary>校验输入并创建工作流。</summary>
    [HttpPost("", Name = "AdminCreateWorkflow")]
    public async Task<IActionResult> Create(
        [FromBody] CreateWorkflowRequest request,
        CancellationToken ct) =>
        await WorkflowHandlers.CreateAsync(request, HttpContext, workflows, ct);

    /// <summary>读取工作流，保留缺失资源的既有处理。</summary>
    [HttpGet("{id}", Name = "AdminGetWorkflow")]
    public async Task<IActionResult> Get([FromRoute] string id, CancellationToken ct) =>
        await WorkflowHandlers.GetAsync(id, HttpContext, workflows, ct);

    /// <summary>更新工作流，保留原有访问范围和并发结果。</summary>
    [HttpPut("{id}", Name = "AdminUpdateWorkflow")]
    public async Task<IActionResult> Update(
        [FromRoute] string id,
        [FromBody] UpdateWorkflowRequest request,
        CancellationToken ct) =>
        await WorkflowHandlers.UpdateAsync(id, request, HttpContext, workflows, ct);

    /// <summary>基于失败运行的快照创建续跑，并复用可恢复的成功输出。</summary>
    [HttpPost("{id}/runs/{runId}/resume", Name = "AdminResumeWorkflowRun")]
    public async Task<IActionResult> ResumeRun(
        [FromRoute] string id,
        [FromRoute] string runId,
        CancellationToken ct) =>
        await WorkflowHandlers.ResumeRunAsync(id, runId, HttpContext, runs, ct);

    /// <summary>计算局部重跑范围及可复用节点，供用户预览。</summary>
    [HttpGet(
        "{id}/runs/{runId}/partial-rerun/preview",
        Name = "AdminPreviewPartialWorkflowRerun")]
    public async Task<IActionResult> PreviewPartialRerun(
        [FromRoute] string id,
        [FromRoute] string runId,
        [FromQuery] string? fromNodeId,
        CancellationToken ct) =>
        await WorkflowHandlers.PreviewPartialRerunAsync(
            id,
            runId,
            fromNodeId,
            HttpContext,
            runs,
            ct);

    /// <summary>按已验证的局部重跑计划创建新运行。</summary>
    [HttpPost("{id}/runs/{runId}/partial-rerun", Name = "AdminPartialWorkflowRerun")]
    public async Task<IActionResult> PartialRerun(
        [FromRoute] string id,
        [FromRoute] string runId,
        [FromBody] PartialRerunWorkflowRequest request,
        CancellationToken ct) =>
        await WorkflowHandlers.PartialRerunAsync(
            id,
            runId,
            request,
            HttpContext,
            runs,
            ct);

    /// <summary>从指定节点开始重试该节点及其下游；失败源运行的上游结果会复用。</summary>
    [HttpPost("{id}/runs/{runId}/nodes/{nodeId}/retry", Name = "AdminRetryWorkflowNode")]
    public Task<IActionResult> RetryNode(
        [FromRoute] string id, [FromRoute] string runId, [FromRoute] string nodeId, CancellationToken ct) =>
        WorkflowHandlers.PartialRerunAsync(id, runId, new PartialRerunWorkflowRequest(nodeId), HttpContext, runs, ct);

    /// <summary>跳过一个未完成节点并保留运行审计，可随后从失败运行续跑。</summary>
    [HttpPost("{id}/runs/{runId}/nodes/{nodeId}/skip", Name = "AdminSkipWorkflowNode")]
    public Task<IActionResult> SkipNode(
        [FromRoute] string id, [FromRoute] string runId, [FromRoute] string nodeId,
        [FromBody] SkipWorkflowNodeRequest request, CancellationToken ct) =>
        WorkflowHandlers.SkipNodeAsync(id, runId, nodeId, request, HttpContext, runs, ct);

    /// <summary>从检查点创建补偿性回滚运行；不会撤销已经发出的外部副作用。</summary>
    [HttpPost("{id}/runs/{runId}/rollback", Name = "AdminRollbackWorkflowRun")]
    public Task<IActionResult> Rollback(
        [FromRoute] string id, [FromRoute] string runId,
        [FromBody] PartialRerunWorkflowRequest request, CancellationToken ct) =>
        WorkflowHandlers.PartialRerunAsync(id, runId, request, HttpContext, runs, ct);

    /// <summary>管理员读取此 API 工作流最近五十次调试的安全摘要。</summary>
    [HttpGet("{id}/runs", Name = "AdminListWorkflowRuns")]
    public Task<IActionResult> ListRuns([FromRoute] string id, CancellationToken ct) => WorkflowHandlers.ListRunsAsync(id, HttpContext, runs, ct);

    /// <summary>读取工作流最近一次交互式运行。</summary>
    [HttpGet("{id}/runs/latest", Name = "AdminGetLatestWorkflowRun")]
    public async Task<IActionResult> GetLatestRun([FromRoute] string id, CancellationToken ct) =>
        await WorkflowHandlers.GetLatestRunAsync(id, HttpContext, runs, ct);

    /// <summary>读取指定运行及其当前持久化状态。</summary>
    [HttpGet("{id}/runs/{runId}", Name = "AdminGetWorkflowRun")]
    public async Task<IActionResult> GetRun(
        [FromRoute] string id,
        [FromRoute] string runId,
        CancellationToken ct) =>
        await WorkflowHandlers.GetRunAsync(id, runId, HttpContext, runs, ct);

    /// <summary>管理员读取运行当时的只读图快照；不读取或改写当前草稿。</summary>
    [HttpGet("{id}/runs/{runId}/snapshot", Name = "AdminGetWorkflowRunSnapshot")]
    public Task<IActionResult> GetRunSnapshot([FromRoute] string id, [FromRoute] string runId, CancellationToken ct) =>
        WorkflowHandlers.GetRunAsync(id, runId, HttpContext, runs, ct, snapshot: true);

    /// <summary>请求取消指定运行，沿用当前终止状态规则。</summary>
    [HttpDelete("{id}/runs/{runId}", Name = "AdminCancelWorkflowRun")]
    public async Task<IActionResult> CancelRun(
        [FromRoute] string id,
        [FromRoute] string runId,
        CancellationToken ct) =>
        await WorkflowHandlers.CancelRunAsync(id, runId, HttpContext, runs, realtime, ct);
}
