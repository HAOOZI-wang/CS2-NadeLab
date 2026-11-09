using System.Numerics;
using NadeLab.Core;

internal static class SearchDownwardBankTests
{
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        foreach(var kind in new[]{GrenadeKind.Flash,GrenadeKind.HighExplosive})
            test($"{kind} finds a downward jumping wall/ground bank during the early route scan",()=>Verify(kind,assert));
    }

    private static void Verify(GrenadeKind kind,Action<bool,string> assert)
    {
        var physics=Grenades.Bootstrap(kind,new());var start=new ThrowStart(Vector3.Zero);
        var jump=ThrowCatalog.Families(physics,new(){IncludeActions=true},500,0)
            .First(a=>a.Mode==ThrowMode.Left && a.EffectiveAction==ThrowAction.Jump);
        var scene=new AnalyticWorld(-40,new Box(new(-700,90,-150),new(700,100,300)));
        // This angle is neither a proposal-grid point nor a supplied hint. Its
        // positive pitch creates both wall and ground contacts before the fuse.
        var known=SmokeSimulation.Run(scene,physics,start,jump with {Pitch=43.2f,Yaw=135.6f});
        assert(known.Status==SimulationStatus.Detonated && known.WallBounces==1 && known.Bounces>=2,"invalid multiple-contact reference");
        var target=new Target(known.Endpoint,8,new Vector3(8,8,4),Airspace:true);
        var world=new CountedWorld(scene);
        // Isolate the requested stationary jump family, without an aim seed.
        // The old lane eventually found it only after more than 500 simulations.
        using var search=new LineupSearch(world,physics,start,target,new(){IncludeActions=true,MaxCandidates=1600},
            a=>a.Mode==ThrowMode.Left && a.EffectiveAction==ThrowAction.Jump);
        int first=-1,firstTraces=-1;
        while(search.Advance()) if(first<0 && search.ReviewedCandidateCount>0){first=search.Evaluated;firstTraces=world.Traces;}
        var result=search.Result!;var found=result.Matches.Concat(result.Experimental??[]).ToArray();
        Console.WriteLine($"  downward {kind}: first={first}, firstTraces={firstTraces}, total={search.Evaluated}, traces={world.Traces}, recommended={result.Matches.Count}, low={result.Experimental?.Count??0}");
        assert(found.Length>0 && first>0 && first<250,"known downward jump waited for the late broad scan");
        assert(result.Routes!.Any(r=>r.Lane=="jump-bank" && r.Hits>0),"jump-bank proposals never entered the reachable region");
        assert(world.Traces<650000,"full search exceeded the bounded collision-work regression budget");
        assert(result.Matches.All(c=>c.Reliability>=.75f) && (result.Experimental??[]).All(c=>c.Reliability<.75f),"low tolerance was mislabelled as recommended");
        foreach(var candidate in found)
        {
            var replay=SmokeSimulation.Run(scene,physics,start,candidate.Aim);
            assert(candidate.Aim.EffectiveAction==ThrowAction.Jump && candidate.Aim.Mode==ThrowMode.Left,"wrong action family published");
            assert(replay.Status==SimulationStatus.Detonated && target.Accepts(scene,replay.Endpoint),"published route failed complete collision replay");
        }
    }

    private sealed class CountedWorld(ICollisionWorld inner):ICollisionWorld
    {
        public int Traces;
        public SweepHit Sweep(Vector3 a,Vector3 b,float size){Traces++;return inner.Sweep(a,b,size);}
    }
}
