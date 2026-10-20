using CreativeFlow.Application.Abstractions.Ai;

namespace CreativeFlow.Application.Features.Agent;

public enum AgentToolSite
{
    /// <summary>工具由已绑定的浏览器画布执行。</summary>
    Canvas,
    /// <summary>工具在服务端通过既有适配器执行。</summary>
    Server,
}

public enum AgentApprovalPolicy
{
    /// <summary>该工具不额外要求用户审批。</summary>
    Never,
    /// <summary>该工具每次执行都必须经过用户审批。</summary>
    Always,
    /// <summary>仅指定的破坏性操作需要用户审批。</summary>
    DestructiveAction,
}

/// <param name="Name">实体或配置项的名称。</param>
/// <param name="Site">工具在服务器端还是浏览器画布上执行。</param>
/// <param name="Approval">工具执行采用的审批策略。</param>
/// <param name="Description">面向用户或模型展示的说明文本。</param>
/// <param name="InputSchemaJson">对外输入契约的 JSON schema。</param>
/// <param name="TimeoutMilliseconds">工具执行超时，单位为毫秒。</param>
public sealed record AgentToolDescriptor(
    string Name,
    AgentToolSite Site,
    AgentApprovalPolicy Approval,
    string Description,
    string InputSchemaJson,
    int TimeoutMilliseconds = 30_000)
{
    /// <summary>将工具目录描述转换为模型可调用的工具定义。</summary>
    public AgentToolDefinition ToModelDefinition() => new(Name, Description, InputSchemaJson);
}

/// <summary>
/// 工具名称、架构、执行位置、审批和超时的服务器端权威定义。
/// </summary>
public static class AgentToolCatalog
{
    /// <summary>完整的内置工具描述列表。</summary>
    public static IReadOnlyList<AgentToolDescriptor> All { get; } =
    [
        new(
            "read_canvas",
            AgentToolSite.Canvas,
            AgentApprovalPolicy.Never,
            "Read the current canvas graph. Returns nodes, edges, node kinds, positions, sizes, selected state, and editable data.",
            """
            {"type":"object","properties":{},"additionalProperties":false}
            """),
        new(
            "get_node_status",
            AgentToolSite.Canvas,
            AgentApprovalPolicy.Never,
            "Read execution status and output information for one or more canvas nodes.",
            """
            {"type":"object","properties":{"nodeIds":{"type":"array","items":{"type":"string"},"minItems":1,"maxItems":100}},"required":["nodeIds"],"additionalProperties":false}
            """),
        new(
            "subscribe_node",
            AgentToolSite.Canvas,
            AgentApprovalPolicy.Never,
            "Read a node's current execution snapshot and obtain a stable subscription descriptor for wait_for_node_event.",
            """
            {"type":"object","properties":{"nodeId":{"type":"string"}},"required":["nodeId"],"additionalProperties":false}
            """),
        new(
            "wait_for_node_event",
            AgentToolSite.Canvas,
            AgentApprovalPolicy.Never,
            "Wait without polling until a node reaches one of the requested execution statuses, changes execution, disappears, or the timeout expires.",
            """
            {"type":"object","properties":{"nodeId":{"type":"string"},"statuses":{"type":"array","items":{"type":"string","enum":["idle","queued","running","succeeded","failed","cancelled","interrupted"]},"maxItems":7},"timeoutMs":{"type":"integer","minimum":1000,"maximum":600000}},"required":["nodeId"],"additionalProperties":false}
            """,
            TimeoutMilliseconds: 600_000),
        new(
            "create_node",
            AgentToolSite.Canvas,
            AgentApprovalPolicy.Never,
            "Create one CreativeFlow canvas node. Valid kinds are text, image, video, imageGen, and videoGen. Returns the created node id.",
            """
            {"type":"object","properties":{"kind":{"type":"string","enum":["text","image","video","imageGen","videoGen"]},"position":{"type":"object","properties":{"x":{"type":"number"},"y":{"type":"number"}},"required":["x","y"],"additionalProperties":false},"data":{"type":"object"}},"required":["kind"],"additionalProperties":false}
            """),
        new(
            "update_node",
            AgentToolSite.Canvas,
            AgentApprovalPolicy.Never,
            "Update editable fields on an existing node. Fields are validated against that node kind; React Flow internals cannot be changed.",
            """
            {"type":"object","properties":{"nodeId":{"type":"string"},"data":{"type":"object","minProperties":1}},"required":["nodeId","data"],"additionalProperties":false}
            """),
        new(
            "connect_nodes",
            AgentToolSite.Canvas,
            AgentApprovalPolicy.Never,
            "Connect a source node to a target node. Cycles and duplicate edges are rejected.",
            """
            {"type":"object","properties":{"sourceId":{"type":"string"},"targetId":{"type":"string"}},"required":["sourceId","targetId"],"additionalProperties":false}
            """),
        new(
            "select_nodes",
            AgentToolSite.Canvas,
            AgentApprovalPolicy.Never,
            "Replace the canvas selection with the supplied node ids. An empty array clears selection.",
            """
            {"type":"object","properties":{"nodeIds":{"type":"array","items":{"type":"string"},"maxItems":500}},"required":["nodeIds"],"additionalProperties":false}
            """),
        new(
            "duplicate_nodes",
            AgentToolSite.Canvas,
            AgentApprovalPolicy.Never,
            "Duplicate existing nodes. Each copy is offset from its source and starts without stale execution state.",
            """
            {"type":"object","properties":{"nodeIds":{"type":"array","items":{"type":"string"},"minItems":1,"maxItems":100}},"required":["nodeIds"],"additionalProperties":false}
            """),
        new(
            "delete_nodes",
            AgentToolSite.Canvas,
            AgentApprovalPolicy.Always,
            "Delete nodes and every edge touching them. This destructive action requires explicit user approval.",
            """
            {"type":"object","properties":{"nodeIds":{"type":"array","items":{"type":"string"},"minItems":1,"maxItems":100}},"required":["nodeIds"],"additionalProperties":false}
            """),
        new(
            "disconnect_nodes",
            AgentToolSite.Canvas,
            AgentApprovalPolicy.Never,
            "Remove edges by edge id, or all edges from one source node to one target node.",
            """
            {"type":"object","properties":{"edgeIds":{"type":"array","items":{"type":"string"},"maxItems":100},"sourceId":{"type":"string"},"targetId":{"type":"string"}},"additionalProperties":false}
            """),
        new(
            "move_nodes",
            AgentToolSite.Canvas,
            AgentApprovalPolicy.Never,
            "Move nodes to absolute canvas coordinates. The operation is one undoable history step.",
            """
            {"type":"object","properties":{"moves":{"type":"array","minItems":1,"maxItems":100,"items":{"type":"object","properties":{"nodeId":{"type":"string"},"x":{"type":"number"},"y":{"type":"number"}},"required":["nodeId","x","y"],"additionalProperties":false}}},"required":["moves"],"additionalProperties":false}
            """),
        new(
            "resize_node",
            AgentToolSite.Canvas,
            AgentApprovalPolicy.Never,
            "Resize one node. Width and height are clamped to the canvas minimum and a safe maximum.",
            """
            {"type":"object","properties":{"nodeId":{"type":"string"},"width":{"type":"number","minimum":160,"maximum":4096},"height":{"type":"number","minimum":100,"maximum":4096}},"required":["nodeId","width","height"],"additionalProperties":false}
            """),
        new(
            "run_node",
            AgentToolSite.Canvas,
            AgentApprovalPolicy.Never,
            "Run an image-generation or video-generation node and wait until its browser orchestration reaches a terminal outcome.",
            """
            {"type":"object","properties":{"nodeId":{"type":"string"}},"required":["nodeId"],"additionalProperties":false}
            """,
            TimeoutMilliseconds: 600_000),
        new(
            "retry_node",
            AgentToolSite.Canvas,
            AgentApprovalPolicy.Never,
            "Retry a failed or interrupted image or video output through the generation node that feeds it.",
            """
            {"type":"object","properties":{"nodeId":{"type":"string"}},"required":["nodeId"],"additionalProperties":false}
            """,
            TimeoutMilliseconds: 600_000),
        new(
            "cancel_node",
            AgentToolSite.Canvas,
            AgentApprovalPolicy.Always,
            "Cancel the active generation run carried by an image or video output node. This requires explicit approval.",
            """
            {"type":"object","properties":{"nodeId":{"type":"string"}},"required":["nodeId"],"additionalProperties":false}
            """),
        new(
            "manage_canvas",
            AgentToolSite.Canvas,
            AgentApprovalPolicy.DestructiveAction,
            "Manage canvas history and viewport. Actions: undo, redo, fit_view, zoom_in, zoom_out, auto_layout, and clear. Clear is destructive and requires approval.",
            """
            {"type":"object","properties":{"action":{"type":"string","enum":["undo","redo","fit_view","zoom_in","zoom_out","auto_layout","clear"]}},"required":["action"],"additionalProperties":false}
            """),
        new(
            "load_skill",
            AgentToolSite.Server,
            AgentApprovalPolicy.Never,
            "Load an installed Skill's instructions or a named UTF-8 resource. Available skill slugs and descriptions are supplied in this tool's per-run schema.",
            """
            {"type":"object","properties":{"slug":{"type":"string"},"path":{"type":"string","default":"SKILL.md"}},"required":["slug"],"additionalProperties":false}
            """),
        new(
            "run_skill_script",
            AgentToolSite.Server,
            AgentApprovalPolicy.Always,
            "Run a .sh, .py, or .js script from an installed Skill in the isolated runner. Every execution requires explicit user approval.",
            """
            {"type":"object","properties":{"slug":{"type":"string"},"scriptPath":{"type":"string"},"arguments":{"type":"array","items":{"type":"string"},"maxItems":64},"stdin":{"type":"string","maxLength":262144},"timeoutSeconds":{"type":"integer","minimum":1,"maximum":120}},"required":["slug","scriptPath"],"additionalProperties":false}
            """,
            TimeoutMilliseconds: 130_000),
        new(
            "edit_image",
            AgentToolSite.Canvas,
            AgentApprovalPolicy.Never,
            "Create a new image from an existing image node. Crop, rotate, flip, and upscale are local pixel operations. Expand prepares transparent margins and mask creates an inpaint-mask node. Variation, outpaint, and inpaint compose real text/image/image-generation nodes and run the existing generation pipeline; they require providerId, modelId, and prompt.",
            """
            {"type":"object","properties":{"nodeId":{"type":"string"},"operation":{"type":"string","enum":["crop","rotate","flip","upscale","expand","mask","variation","outpaint","inpaint"]},"quarterTurns":{"type":"integer"},"axis":{"type":"string","enum":["horizontal","vertical"]},"factor":{"type":"number","minimum":1,"maximum":8},"providerId":{"type":"string"},"modelId":{"type":"string"},"prompt":{"type":"string","maxLength":100000},"crop":{"type":"object","properties":{"x":{"type":"number","minimum":0,"maximum":1},"y":{"type":"number","minimum":0,"maximum":1},"width":{"type":"number","exclusiveMinimum":0,"maximum":1},"height":{"type":"number","exclusiveMinimum":0,"maximum":1}},"required":["x","y","width","height"],"additionalProperties":false},"insets":{"type":"object","properties":{"top":{"type":"number","minimum":0,"maximum":2},"right":{"type":"number","minimum":0,"maximum":2},"bottom":{"type":"number","minimum":0,"maximum":2},"left":{"type":"number","minimum":0,"maximum":2}},"required":["top","right","bottom","left"],"additionalProperties":false},"strokes":{"type":"array","minItems":1,"maxItems":100,"items":{"type":"object","properties":{"radius":{"type":"number","exclusiveMinimum":0,"maximum":0.5},"points":{"type":"array","minItems":1,"maxItems":2000,"items":{"type":"object","properties":{"x":{"type":"number","minimum":0,"maximum":1},"y":{"type":"number","minimum":0,"maximum":1}},"required":["x","y"],"additionalProperties":false}}},"required":["radius","points"],"additionalProperties":false}}},"required":["nodeId","operation"],"additionalProperties":false}
            """,
            TimeoutMilliseconds: 120_000),
    ];

    /// <summary>按当前查找键读取已有配置或目录项。</summary>
    public static AgentToolDescriptor? Find(string name) =>
        All.FirstOrDefault(tool => string.Equals(tool.Name, name, StringComparison.Ordinal));
}
