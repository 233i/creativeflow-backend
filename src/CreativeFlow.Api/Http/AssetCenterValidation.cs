using CreativeFlow.Application.Contracts.Ai;

namespace CreativeFlow.Api.Http;

/// <summary>管理端与用户选择器共用参数边界，任何入口都不能传入任意排序或无限批量。</summary>
internal static class AssetCenterValidation
{
    internal static bool Query(AssetQuery q) => q.Offset is >= 0 and <= 10000000 && q.Limit is >= 1 and <= 100
        && q.Search?.Length is not > 200 && q.Tag?.Length is not > 40 && q.MinBytes is not < 0 && !(q.From > q.To)
        && Optional(q.Kind, "image", "video") && Optional(q.Source, "unknown", "upload", "generated", "derived", "reused")
        && q.State is "active" or "recycled" or "purged" or "all" && Optional(q.Usage, "referenced", "unreferenced", "purgeable")
        && q.Sort is "created_desc" or "created_asc" or "size_desc" or "name_asc";
    internal static bool Tags(string[]? tags) => tags is { Length: <= 20 } && tags.All(t => !string.IsNullOrWhiteSpace(t) && t.Length <= 40);
    internal static bool Reason(string? reason) => !string.IsNullOrWhiteSpace(reason) && reason.Length <= 300;
    internal static bool Revision(string? revision) => !string.IsNullOrEmpty(revision) && revision.Length <= 32;
    internal static bool Update(UpdateAssetRequest r) => !string.IsNullOrWhiteSpace(r.Name) && r.Name.Length <= 200 && Tags(r.Tags) && Reason(r.Reason) && Revision(r.Revision);
    internal static bool Action(AssetActionRequest r) => Reason(r.Reason) && Revision(r.Revision) && r.ConfirmName?.Length is not > 200;
    internal static bool Batch(AssetBatchRequest r) => r.Ids is { Length: > 0 and <= 50 } && r.Ids.All(x => Guid.TryParse(x, out var id) && id != Guid.Empty)
        && r.Action is "recycle" or "restore" or "purge" or "group" or "tags" && Reason(r.Reason) && r.GroupName?.Length is not > 64
        && (r.Action != "tags" || Tags(r.Tags)) && (r.Action is "tags" or "group" || r.Revisions is not null);
    private static bool Optional(string? value, params string[] values) => string.IsNullOrEmpty(value) || values.Contains(value, StringComparer.Ordinal);
}
