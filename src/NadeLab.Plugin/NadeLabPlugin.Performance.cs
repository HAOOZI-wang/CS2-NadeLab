using System.Diagnostics;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private readonly SearchFrameBudget searchFrameBudget=new();
    private double searchBudgetMs=SearchFrameBudget.ConservativeMs;
    private const int SearchUnitLimit = 4096;
    private readonly Queue<double> tickWork = new();
    private readonly Queue<double> tickIntervals = new();
    private readonly Queue<double> actionWork = new();
    private double peakActionMs;
    private double peakPublishMs;
    private double peakTickMs;
    private long lastTickAt;
    private SearchDiagnostics? lastSearchDiagnostics;
    private sealed record SearchDiagnostics(long NativeTraces,long CollisionHits,long SolidStarts,double WorkMs,double PeakTickMs);
    private static System.Text.Json.Nodes.JsonObject DiagnosticsJson(SearchDiagnostics d) => new()
    { ["NativeTraces"]=d.NativeTraces,["CollisionHits"]=d.CollisionHits,["SolidStarts"]=d.SolidStarts,["WorkMs"]=d.WorkMs,["PeakTickMs"]=d.PeakTickMs };

    private static void AddTimingSample(Queue<double> window,double milliseconds)
    {
        window.Enqueue(milliseconds);
        if (window.Count>512) window.Dequeue();
    }
    private void Tick()
    {
        long began=Stopwatch.GetTimestamp();
        double interval=lastTickAt==0 ? 0 : Stopwatch.GetElapsedTime(lastTickAt,began).TotalMilliseconds;
        if (lastTickAt!=0) AddTimingSample(tickIntervals,interval);
        double now=(double)began/Stopwatch.Frequency;
        searchFrameBudget.Observe(now,interval,0);
        searchBudgetMs=searchFrameBudget.Milliseconds(now);
        lastTickAt=began;
        try { TickCore(); }
        finally
        {
            double elapsed=Stopwatch.GetElapsedTime(began).TotalMilliseconds;
            searchFrameBudget.Observe(now,interval,elapsed);
            AddTimingSample(tickWork,elapsed); peakTickMs=Math.Max(peakTickMs,elapsed);
        }
    }
    private string PerformanceReport()
    {
        string timing=tickWork.Count==0 ? "暂无帧样本" :
            $"最近 {tickWork.Count} 帧：插件平均 {tickWork.Average():F3}ms / P95 {Percentile(tickWork,.95):F3} / P99 {Percentile(tickWork,.99):F3} / 最大 {tickWork.Max():F3}ms；帧间隔P99 {Percentile(tickIntervals,.99):F1}ms；搜索预算 {searchBudgetMs:F1}ms";
        var counters=job is { } current
            ? new SearchDiagnostics(current.World.TraceCount,current.World.HitCount,current.World.SolidStartCount,current.TotalWorkMs,current.PeakWorkMs)
            : lastSearchDiagnostics;
        timing+=$"；插件累计峰值 {peakTickMs:F2}ms；准备/菜单峰值 {peakActionMs:F2}ms；结果发布峰值 {peakPublishMs:F2}ms；预览 {previewScenes.Values.Sum(s=>s.Beams.Count)} 实体 / {previewScenes.Values.Sum(s=>s.Pending.Count)} 待画。";
        return timing+(counters is null ? "；无搜索碰撞统计。" :
            $"；{(job is null ? "上次搜索" : "当前搜索")}原生检测 {counters.NativeTraces} / 碰撞 {counters.CollisionHits} / 实体内起步 {counters.SolidStarts}；搜索单帧峰值 {counters.PeakTickMs:F3}ms。");
    }
    private static double Percentile(IEnumerable<double> samples,double fraction)
    {
        var sorted=samples.Order().ToArray();
        return sorted.Length==0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(sorted.Length*fraction)-1,0,sorted.Length-1)];
    }
}
