using System.Numerics;
using NadeLab.Core;

internal static class SearchInverseRefinementTests
{
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        foreach(bool walls in new[]{false,true})
            test($"Local endpoint refinement finds a tolerant smoke route ({(walls ? "walls" : "open")})",()=>
            {
                var scene=walls ? new AnalyticWorld(0,new Box(new(100,-300,0),new(105,300,200)),
                    new Box(new(-90,-300,0),new(-85,300,200))) : new AnalyticWorld();
                var start=new ThrowStart(Vector3.Zero);var physics=new PhysicsProfile();
                var family=ThrowCatalog.Families(physics,new(){IncludeActions=true},1500,0)
                    .First(a=>a.Mode==ThrowMode.Right && a.EffectiveAction==ThrowAction.RunJump);
                // Frozen synthetic examples from the 312-scene comparison.
                // The generating aim is never supplied to the search as a hint.
                var known=SmokeSimulation.Run(scene,physics,start,family with
                    {Pitch=walls ? -68.868225f : -41.339966f,Yaw=walls ? 75.07892f : 65.97215f});
                assert(known.Status==SimulationStatus.Rested,"reference did not settle");
                var target=new Target(known.Endpoint,8,new Vector3(32,32,8));
                var world=new Counted(scene);
                using var search=new LineupSearch(world,physics,start,target,new(){IncludeActions=true,MaxCandidates=1600});
                int first=-1;
                while(search.Advance()) if(first<0 && search.PublishedCandidates.Count>0) first=search.Evaluated;
                var result=search.Result!;
                Console.WriteLine($"  inverse {(walls ? "walls" : "open")}: first={first}; total={search.Evaluated}; recommended={result.Matches.Count}; low={result.Experimental?.Count ?? 0}; traces={world.Traces}");
                // Adoption is now independent of completion. A later quality
                // scan must not delay the first actionable landing.
                assert(SearchPresentation.HasUsable(result) && first is >0 and <900,"no early actionable route");
                assert(world.Traces<1500000,"endpoint refinement exceeded the collision-work envelope");
                foreach(var candidate in result.Matches.Concat(result.Experimental??[]))
                {
                    var replay=SmokeSimulation.Run(scene,physics,start,candidate.Aim);
                    assert(candidate.RobustTrials>0 && replay.Status==SimulationStatus.Rested && target.Accepts(scene,replay.Endpoint),
                        "an inverse proposal bypassed complete simulation or review");
                }
                assert(result.Matches.All(SearchQuality.Recommended),"fragile route promoted without tolerance evidence");
            });
    }
    private sealed class Counted(ICollisionWorld inner):ICollisionWorld
    {
        public int Traces;
        public SweepHit Sweep(Vector3 a,Vector3 b,float halfSize) {Traces++;return inner.Sweep(a,b,halfSize);}
    }
}
