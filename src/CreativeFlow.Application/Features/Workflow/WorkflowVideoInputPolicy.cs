namespace CreativeFlow.Application.Features.Workflow;

/// <summary>解释视频节点的图片顺序，保留旧节点的参考图与连线顺序语义。</summary>
public static class WorkflowVideoInputPolicy
{
    /// <summary>在读取素材前检查首尾帧数量，并仅按显式交换配置调整两张图片的顺序。</summary>
    public static WorkflowVideoInputs Prepare(
        string inputMode,
        bool reverseFrames,
        IReadOnlyList<string> images)
    {
        // 参考图模式沿用原输入；不能因用户曾交换过首尾帧而改变参考图模式的生成结果。
        if (inputMode != "first-last") return new(images, null);
        if (images.Count > 2)
            return new([], "First-last mode accepts at most two images. Reduce the connected image outputs.");
        return new(reverseFrames && images.Count == 2 ? [images[1], images[0]] : images, null);
    }
}

/// <summary>视频节点准备读取的素材引用及可定位的输入错误。</summary>
/// <param name="Images">按当前模式排列的素材路径，不修改原节点或上游输出。</param>
/// <param name="Error">输入不满足首尾帧约束时的错误；为空表示可以继续读取素材。</param>
public sealed record WorkflowVideoInputs(IReadOnlyList<string> Images, string? Error);
