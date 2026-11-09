using System.Diagnostics;
using CounterStrikeSharp.API.Core;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private bool ExpandJob(SearchJob current,Session s,CCSPlayerController? owner)
    {
        if(!current.Search.CanContinue || owner is not null && current.OwnerIdentity is { } identity && !identity.Matches(ActionIdentity(owner))) return false;
        var start=current.SearchedStart ?? s.Start!.Value;
        var pawn=owner?.PlayerPawn.Value;
        current.PriorTraceCount+=current.World.TraceCount;current.PriorHitCount+=current.World.HitCount;current.PriorSolidCount+=current.World.SolidStartCount;
        if(!current.Paused) current.CompletedSeconds+=Stopwatch.GetElapsedTime(current.StartedAt).TotalSeconds;
        current.Paused=false;
        current.World=new NativeWorld(pawn);
        current.Search.ContinueExpanded(current.World,ActionFilter(pawn,start),IncrementalRunUpFilter(pawn,start),s.Kind==GrenadeKind.Smoke ? FailureFilter(start) : null,
            ExecutionSpaceFilter(pawn,current.Search.Profile));
        current.StartedAt=Stopwatch.GetTimestamp();current.TimeLimit=20;current.AutomaticExpansion=false;current.Expanded=true;
        current.PublishedCount=0;
        current.LastReport=CounterStrikeSharp.API.Server.CurrentTime;
        Say(owner,"继续寻找更好投的路线，继承已计算角度。低容错投法也会直接列入结果并标明；追加搜索最多20秒，随后复核。");
        return true;
    }
    private void ContinueSearch(CCSPlayerController? p)
    {
        if(!Check(p,out _)) return;
        var s=Get(p!);
        if(s.FlashMode) {StartFlashSearch(p);return;}
        if(s.RegionDraft is not null) {OpenRegionEditor(p);return;}
        if(DummyLayoutLoadActive || job is not null || flashJob is not null) {Say(p,"正在搜索或加载假人，请等待完成或取消。");return;}
        if(s.Continuation is not { } continuation || continuation.Revision!=s.Revision)
        {
            if(s.Start is null) StartCommand(p,null);
            if(s.Start is not null && s.Target is not null)
            {
                // A stale continuation must not route Solve("wide") back here.
                s.Continuation?.Search.Dispose();s.Continuation=null;
                // A nearby result may have a newer origin than the initial request.
                s.RequestedStart=s.Start;
                Say(p,"没有可继承的搜索进度；保留目标和记录站位，重新进行扩展搜索，不保证找到新路线。");
                Solve(p,"wide",keepResults:true);
            }
            return;
        }
        if(!ExpandJob(continuation,s,p)) {Say(p,"本次累计候选预算已用完；可重新搜索或更换站位。");return;}
        s.Continuation=null;s.NextHudAt=0;
        job=continuation;
    }
    private void ExplainSearch(CCSPlayerController p)
    {
        var s=Get(p);
        if(s.Result is not { } result || s.Target is not { } target) return;
        var explanation=SearchExplanation.For(result,target);
        Say(p,explanation.Summary);Say(p,explanation.NextStep);
        foreach(var lane in result.Routes ?? [])
            Say(p,$"{PhaseLabel("route-"+lane.Lane)}：计算{lane.Evaluated}、理想落点命中{lane.Hits}、进入复核{lane.Reviewed}；按提议来源计数，实际撞墙看方案。");
    }
}
