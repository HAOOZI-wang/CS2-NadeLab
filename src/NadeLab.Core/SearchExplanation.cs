namespace NadeLab.Core;

public sealed record SearchExplanation(string Summary,string NextStep)
{
    public static SearchExplanation For(SearchResult result,Target target)
    {
        int recommended=SearchPresentation.RecommendedCount(result),fragile=SearchPresentation.LowToleranceCount(result);
        if(recommended>0) return new($"找到{recommended}个优先尝试方案"+(fragile>0 ? $"，另有{fragile}个低容错投法。" : "。"),
            "使用键就位手投，换弹键依次切换全部投法；模拟命中不代表实际效果已验证。");
        if(fragile>0)
        {
            var experiments=SearchPresentation.ForPlayer(result).Matches;
            var best=experiments.OrderByDescending(c=>c.Reliability).First();
            var weak=best.Checks?.Where(c=>c.Group!="source" && c.Hits<c.Trials).OrderBy(c=>(float)c.Hits/c.Trials).FirstOrDefault();
            string detail=weak is null ? "动作或模型容错不足" : $"{RobustChecks.Label(weak.Group)}扰动仅通过{weak.Hits}/{weak.Trials}";
            return new($"找到{fragile}个模拟可达的低容错投法；{detail}。",
                "已直接列入方案：使用键就位手投，换弹键切换；需要更好操作的投法可继续搜索。");
        }
        if(result.Obstacles is {RunUpRejected:>0} o && o.RunUpRejected>=Math.Max(1,result.Evaluated))
            return new($"本轮有{o.RunUpRejected}个移动候选未通过助跑空间检查；尚无稳妥解。","可保留目标换到后方有助跑空间的位置；这不证明其他投法不可达。");
        if(result.Nearest.FirstOrDefault() is { } nearest && target.AreaHalfSize is { } area && MathF.Abs(nearest.Trajectory.Endpoint.Z-target.Position.Z)>area.Z)
            return new("本轮最接近的已停留轨迹仍超出目标高度范围。","单独放宽水平范围不会修复高度偏差；可继续找路线或记录一颗已知可行的手投。");
        if(result.Obstacles is {ActionSpaceRejected:>0} a && result.Evaluated==0)
            return new($"本轮{a.ActionSpaceRejected}个动作未通过人物空间/支撑检查。","落地站稳后换一个无遮挡站位重搜；不能据此断言目标不可达。");
        return new(result.Truncated ? "本轮预算内尚未找到通过复核的方案。" : "已采样路线中尚未找到通过复核的方案。",
            "原因尚不能确定；可继续搜索、保留目标换站位，或自由实投记录已知可行的投法。");
    }
}
