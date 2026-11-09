using System.Numerics;
using Stopwatch=System.Diagnostics.Stopwatch;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using NadeLab.Core;
using CsTrace=CounterStrikeSharp.API.Modules.Utils.Trace;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private static ThrowStart? NearbyAlternative(CCSPlayerPawn? pawn,ThrowStart start,Target target)
    {
        if(!target.IsArea || Vector2.Distance(new(start.Feet.X,start.Feet.Y),new(target.Position.X,target.Position.Y))<1500) return null;
        float yaw=MathF.Atan2(target.Position.Y-start.Feet.Y,target.Position.X-start.Feet.X)*180/MathF.PI;
        if(FindPreparation(pawn,start,new(0,yaw,ThrowMode.Left,250,ThrowAction.Run),new()) is not null) return null;
        var mins=pawn?.Collision?.Mins ?? new CounterStrikeSharp.API.Modules.Utils.Vector(-16,-16,0);
        var maxs=pawn?.Collision?.Maxs ?? new CounterStrikeSharp.API.Modules.Utils.Vector(16,16,72);
        var options=new TraceOptions {InteractsAs=Contents.Player,InteractsWith=Masks.PlayerSolid|Contents.PhysicsProp};
        return NearbyStart.Find(start,target,
            feet=>
            {
                var floor=CsTrace.TraceHullShape(NativeWorld.ToNative(feet+new Vector3(0,0,18)),NativeWorld.ToNative(feet-new Vector3(0,0,18)),mins,maxs,pawn,options);
                if(!floor.DidHit() || floor.IsAllSolid || floor.Normal.Z<.7f) return null;
                var grounded=NativeWorld.FromNative(floor.EndPos);
                return TryStandingPosition(pawn,grounded,out var safe) && safe.Z-grounded.Z<=3 ? grounded : null;
            },
            (from,to)=>
            {
                var lift=new Vector3(0,0,18);
                var trace=CsTrace.TraceHullShape(NativeWorld.ToNative(from+lift),NativeWorld.ToNative(to+lift),mins,maxs,pawn,options);
                return !trace.IsAllSolid && !trace.DidHit();
            });
    }

    private bool ContinueNearby(SearchJob current,Session session,CCSPlayerController? owner)
    {
        if(current.Search.Result is not { } result || SearchPresentation.HasUsable(result) || SearchPresentation.HasUsable(session.Result) || current.AlternativeStart is not { } next) return false;
        current.AlternativeStart=null;
        current.Search.Dispose();
        var pawn=owner?.PlayerPawn.Value;
        current.World=new NativeWorld(pawn);
        var searchOptions=new SearchOptions {IncludeActions=true,MaxCandidates=4000,PreferredView=session.ReferenceView,ForwardSpeed=SmokeRunSpeed(pawn,session.Kind),AirForwardSpeed=AirForwardSpeed()};
        searchOptions=searchOptions with {SeedAims=SearchSeedAims(owner,next,session.Target!.Value,session.ReferenceView,session.SearchPhysics ?? ProfileFor(session),searchOptions)};
        current.Search=new(current.World,session.SearchPhysics ?? ProfileFor(session),next,session.Target!.Value,
            searchOptions,
            canRunUp:ActionFilter(pawn,next),runUpSteps:IncrementalRunUpFilter(pawn,next),rejectedByEvidence:session.Kind==GrenadeKind.Smoke ? FailureFilter(next) : null,
            executionSpace:ExecutionSpaceFilter(pawn,session.SearchPhysics ?? ProfileFor(session)));
        current.SearchedStart=next;
        current.StartedAt=Stopwatch.GetTimestamp(); current.TimeLimit=20;
        current.TotalWorkMs=0; current.PeakWorkMs=0;
        Say(owner,"原站位暂未找到稳妥落点，补搜前方约96u的安全站位；目标保留，找到后按使用键就位。");
        return true;
    }
}
