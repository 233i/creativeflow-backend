using CreativeFlow.Domain.Ai;
using CreativeFlow.Domain.Identity;

namespace CreativeFlow.Application.Abstractions.Persistence;

public interface IMcpServerRepository
{
    /// <summary>列出MCP 服务配置，保留现有筛选、排序与访问范围。</summary>
    Task<IReadOnlyList<McpServer>> ListAsync(DeviceId deviceId, CancellationToken ct);

    /// <summary>按当前查找键读取已有配置或目录项。</summary>
    Task<McpServer?> FindAsync(DeviceId deviceId, McpServerId serverId, CancellationToken ct);

    /// <summary>按原有存储规则写入MCP 服务配置。</summary>
    Task InsertAsync(McpServer server, CancellationToken ct);

    /// <summary>更新MCP 服务配置，保留原有访问范围和并发结果。</summary>
    Task UpdateAsync(McpServer server, CancellationToken ct);

    /// <summary>删除MCP 服务配置，保留不存在或不可访问时的返回语义。</summary>
    Task<bool> DeleteAsync(DeviceId deviceId, McpServerId serverId, CancellationToken ct);

    /// <summary>
    /// 插入该设备尚未注册同名项的服务器，并返回新增数量。用于 JSON 批量导入，
    /// 用户可以对编辑后的文件重复执行导入。
    /// </summary>
    Task<int> InsertIfAbsentAsync(IReadOnlyList<McpServer> servers, CancellationToken ct);
}
