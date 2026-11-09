using System.Text.Json;
using System.Numerics;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private Task? managedWarmup;
    private void WarmManagedPaths()
    {
        // Pure managed fixtures only: native map queries and game entities
        // must never cross to a worker thread. Prime iterator/JIT and generated
        // JSON accessors before the first human search completes.
        managedWarmup=Task.Run(()=>
        {
            // Warm code paths with a bounded fixture, not user calibration:
            // valid custom profiles may not support every seeded jump action.
            var profile=new PhysicsProfile();
            var world=new AnalyticWorld();
            var start=new ThrowStart(System.Numerics.Vector3.Zero);
            using var search=new LineupSearch(world,profile,start,new(new(350,0,2),120),new SearchOptions {IncludeActions=true,MaxCandidates=80});
            while(search.Advance()) { }
            _=JsonSerializer.SerializeToNode(search.Result,NadeLabJsonContext.Default.SearchResult);
            // The old point/Release fixture never visited area route lanes or
            // fixed-origin body iterators. Their first use happened during the
            // player's first real search. Use managed geometry only here.
            var fixedStart=new ThrowStart(Vector3.Zero,64,ThrowOrigin.Preparation);
            SweepHit Body(Vector3 from,Vector3 to)=>from.Z>0 && to.Z<=0 ?
                new(from.Z/(from.Z-to.Z),Vector3.Lerp(from,to,from.Z/(from.Z-to.Z)),Vector3.UnitZ) : SweepHit.Miss(to);
            IEnumerable<bool?> Space(ThrowStart source,Aim action)=>CheckPreparationPathSteps(source,action,profile,Body);
            var controlled=ThrowCatalog.Families(profile,new(){IncludeActions=true},0,0)
                .Where(a=>a.Mode==ThrowMode.Left && a.EffectiveAction is ThrowAction.AirForwardJump or ThrowAction.Run).ToArray();
            foreach(var action in controlled)
            {
                var known=action with {Pitch=-30};
                var endpoint=SmokeSimulation.Run(world,profile,fixedStart,known).Endpoint;
                using var area=new LineupSearch(world,profile,fixedStart,new(endpoint,96,new Vector3(96,96,24)),
                    new(){IncludeActions=true,Exhaustive=true,MaxCandidates=80,SeedAims=[known]},executionSpace:Space);
                while(area.Advance()){}
                _=SearchPresentation.ForPlayer(area.Result!);
                _=SceneAdvisor.Explain(fixedStart,new(endpoint,96,new Vector3(96,96,24)),new(known,SmokeSimulation.Run(world,profile,fixedStart,known),0),[]);
            }
            var aim=new Aim(-25,0,ThrowMode.Left);
            var trajectory=SmokeSimulation.Run(world,profile,start,aim);
            var candidate=new Candidate(aim,trajectory,1,9,9);
            var warmedResult=new SearchResult(profile.Id,false,1,false,[candidate],[candidate],[new(ThrowAction.Standing,ThrowMode.Left,1,1,1,1)],1,[candidate]);
            var warmNode=JsonSerializer.SerializeToNode(warmedResult,NadeLabJsonContext.Default.SearchResult);
            _=warmNode!.ToJsonString(Json);
            _=JsonSerializer.SerializeToNode(profile,NadeLabJsonContext.Default.PhysicsProfile);
            _=JsonSerializer.Serialize(new[]{new SavedRoute(start,SmokeTargets.Around(trajectory.Endpoint),aim,"warmup","warmup",true,[new("warmup",true,true)])},NadeLabJsonContext.Default.SavedRouteArray);
            var library=new RouteLibrary();
            library.Remember(fixedStart,SmokeTargets.Around(trajectory.Endpoint),aim,"warmup","warmup",true,nominalValidated:true);
            _=library.Hints(fixedStart,SmokeTargets.Around(trajectory.Endpoint),aim,"warmup","warmup");
            _=JsonSerializer.SerializeToNode(SmokeTargets.MirageVip,NadeLabJsonContext.Default.Target);
            _=JsonSerializer.SerializeToNode(new ThrowObservation(start.Feet,64,new(0,0,ThrowMode.Left),1,System.Numerics.Vector3.Zero,true,0),NadeLabJsonContext.Default.ThrowObservation);
        });
    }
}
