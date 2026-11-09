using System.Text.Json;
using NadeLab.Core;
using Microsoft.Extensions.Logging;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private readonly RouteLibrary routeLibrary=new();
    private RouteLibrary benchmarkRouteLibrary=new();
    private string? benchmarkRouteRun;
    private string WorldKey=>WorldIdentity.Key(worldVersion?.ToJsonString(),CounterStrikeSharp.API.Server.MapName) ?? "unknown";
    private bool RouteWorldKnown=>worldVersion?["GameVersion"]?.GetValue<string>() is {Length:>0} game && game!="unknown" &&
        worldVersion["MapBytes"]?.GetValue<long>()>0 && worldVersion["MapLastWriteUtc"]?.GetValue<string>() is {Length:>0};
    private static string PhysicsKey(PhysicsProfile profile)=>JsonSerializer.Serialize(profile,NadeLabJsonContext.Default.PhysicsProfile);
    private RouteLibrary BenchmarkRoutes(string run)
    {
        if(benchmarkRouteRun!=run) {benchmarkRouteLibrary=new();benchmarkRouteRun=run;}
        return benchmarkRouteLibrary;
    }
    private void ReadRouteLibrary()
    {
        string path=Path.Combine(DataPath,"route-library.json");
        try
        {
            if(File.Exists(path) && new FileInfo(path).Length<8_000_000)
                routeLibrary.Import((JsonSerializer.Deserialize(File.ReadAllText(path),NadeLabJsonContext.Default.SavedRouteArray) ?? [])
                    .Where(route=>route is not null && WorldIdentity.RouteKey(route.World) is not null)
                    .Select(route=>route with {World=WorldIdentity.RouteKey(route.World)!}));
            Logger.LogInformation("Route library loaded: {Count} versioned hints; every reuse requires simulation",routeLibrary.Count);
        }
        catch(Exception ex) {Logger.LogWarning(ex,"Route library unavailable; starting with no imported hints");}
    }
    private void SaveRouteLibrary()
    {
        var snapshot=routeLibrary.Snapshot().ToArray();
        telemetry!.Write(Path.Combine(DataPath,"route-library.json"),()=>JsonSerializer.Serialize(snapshot,NadeLabJsonContext.Default.SavedRouteArray),false);
    }
    private void RememberRoutes(SearchJob current,Session session,SearchResult result)
    {
        if(!RouteWorldKnown) return;
        // Diagnostic replays have an isolated, run-scoped cache. They must not
        // populate the player's persistent library or borrow its prior angles.
        if(current.ConsoleSession is not null && current.BenchmarkRunId is null) return;
        var library=current.BenchmarkRunId is { } run ? BenchmarkRoutes(run) : routeLibrary;
        var start=current.SearchedStart ?? session.Start!.Value;var target=session.Target!.Value;
        var profile=current.Search.Profile;bool changed=false;
        string world=WorldKey,physicsKey=PhysicsKey(profile);
        foreach(var c in result.Matches.Concat(result.Experimental ?? []).Distinct().Take(20))
        {
            if(c.RobustTrials<=0 || !profile.Successful(c.Trajectory.Status) || !target.Contains(c.Trajectory.Endpoint)) continue;
            library.Remember(start,target,c.Aim,world,physicsKey,c.Reliability>=.75f,nominalValidated:true);
            changed=true;
        }
        if(changed && current.BenchmarkRunId is null) SaveRouteLibrary();
    }
    private void RememberActual(RecordedThrow record)
    {
        if(record.Arm.ObservationOnly || record.Arm.EffectObjective || record.Arm.Target is not { } target) return;
        string? world=WorldIdentity.Key(record.WorldVersion?.ToJsonString(),record.Map);if(world is null) return;
        routeLibrary.Remember(record.Arm.Start,target,record.Arm.Candidate.Aim,world,
            JsonSerializer.Serialize(record.Physics,NadeLabJsonContext.Default.PhysicsProfile),record.Arm.Candidate.Reliability>=.75f,
            new(record.Id,record.Matched,record.TargetHit,record.Feedback?.Verdict ?? "unknown",record.GuidedMatched,record.Feedback?.ObjectiveViewMatched));
        SaveRouteLibrary();
    }
    private void RememberGrenadeActual(GrenadeFlight flight,bool matched,bool hit)
    {
        // Fire burst alone does not confirm ignition; free throws have no
        // verified action identity to transfer to a saved lineup.
        if(flight.Arm.ObservationOnly || flight.Arm.EffectObjective || flight.Arm.Target is not { } target || flight.OwnerIsBot || flight.Profile.Kind.Fire() || flight.World is null) return;
        string? world=WorldIdentity.RouteKey(flight.World.ToJsonString());if(world is null) return;
        routeLibrary.Remember(flight.Arm.Start,target,flight.Arm.Candidate.Aim,world,
            PhysicsKey(flight.Profile),flight.Arm.Candidate.Reliability>=.75f,new(flight.Id,matched,hit));
        SaveRouteLibrary();
    }
    private Aim[] PriorAims(ThrowStart start,Target target,Aim? view,PhysicsProfile profile,string? benchmarkRun=null)=>!RouteWorldKnown ? [] :
        (benchmarkRun is null ? routeLibrary : BenchmarkRoutes(benchmarkRun)).Hints(start,target,view,WorldKey,PhysicsKey(profile));
    private string RouteEvidence(Session s,Candidate candidate)
    {
        var route=routeLibrary.Find(s.Start!.Value,s.Target!.Value,candidate.Aim,WorldKey,PhysicsKey(s.SearchPhysics ?? ProfileFor(s)));
        if(route is null || route.Trials.Count==0) return "证据：仅有模拟复核，尚无本路线实投记录。";
        int matched=route.Trials.Count(t=>t.Matched),landed=route.Trials.Count(t=>t.Matched && t.LandingHit);
        if(s.Kind!=GrenadeKind.Smoke) return $"近期记录：动作匹配的实投生效位置有效{landed}/{matched}；不代表伤害、致盲或覆盖效果达标。";
        int guided=route.Trials.Count(t=>t.GuidedMatched),guidedLanded=route.Trials.Count(t=>t.GuidedMatched && t.LandingHit);
        int sealedCount=route.Trials.Count(t=>t.CoverageVerdict=="sealed"),leaking=route.Trials.Count(t=>t.CoverageVerdict=="leaking");
        string scoped=s.Target?.Objective?.ObserverEye is null ? "" : $" 其中预设视角封满{route.Trials.Count(t=>t.ObjectiveViewMatched==true && t.CoverageVerdict=="sealed")}次，其余不代表预设视角。";
        return $"近期记录：动作匹配手投落点有效{landed}/{matched}；释放区手投{guidedLanded}/{guided}；人工各视角封满{sealedCount}次、漏缝{leaking}次。覆盖不是自动验证。{scoped}";
    }
}
