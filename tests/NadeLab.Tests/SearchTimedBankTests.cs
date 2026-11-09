using System.Numerics;
using NadeLab.Core;

internal static class SearchTimedBankTests
{
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        test("Distant timed walls do not suppress locally open approach proposals",()=>
        {
            var room=new AnalyticWorld(0,new Box(new(400,-450,0),new(410,450,500)),new Box(new(-410,-450,0),new(-400,450,500)),
                new Box(new(-450,400,0),new(450,410,500)),new Box(new(-450,-410,0),new(450,-400,500)));
            var physics=Grenades.Bootstrap(GrenadeKind.Flash,new());
            using var search=new LineupSearch(room,physics,new(Vector3.Zero),new(new(0,0,100),8,new Vector3(8,8,4),Airspace:true),
                new(){IncludeActions=true,MaxCandidates=120});
            while(search.Advance()){}
            // Every long ray reaches a wall, but the original 256u local
            // neighbourhood is open. The local approach lane must survive.
            assert(search.Result!.Routes!.Any(r=>r.Lane=="opening" && r.Evaluated>0),"far walls erased locally open approach hints");
        });
        test("Timed HE search discovers a lossy bank beyond the old local wall survey",()=>
        {
            var scene=new AnalyticWorld(0,
                new Box(new(350,-400,0),new(360,-20,350)),new Box(new(350,20,0),new(360,400,350)),
                new Box(new(350,-20,0),new(360,20,100)),new Box(new(350,-20,160),new(360,20,350)));
            Verify(GrenadeKind.HighExplosive,scene,new(-26.94374f,29.635704f,ThrowMode.Left,250,ThrowAction.Run),assert);
        });
        test("Timed flash search accounts for time and velocity loss after a nearby wall bank",()=>
        {
            var scene=new AnalyticWorld(0,new Box(new(200,-300,0),new(210,100,350)),new Box(new(350,180,0),new(500,190,350)));
            var physics=Grenades.Bootstrap(GrenadeKind.Flash,new());
            var family=ThrowCatalog.Families(physics,new(){IncludeActions=true},1500,0)
                .First(a=>a.EffectiveAction==ThrowAction.RunJump && a.Mode==ThrowMode.Both && a.ForwardSpeed==130);
            Verify(GrenadeKind.Flash,scene,family with {Pitch=-26.194262f,Yaw=3.0947418f},assert);
        });
    }

    private static void Verify(GrenadeKind kind,ICollisionWorld scene,Aim knownAim,Action<bool,string> assert)
    {
        var physics=Grenades.Bootstrap(kind,new());var start=new ThrowStart(Vector3.Zero);
        var known=SmokeSimulation.Run(scene,physics,start,knownAim);
        assert(known.Status==SimulationStatus.Detonated && known.WallBounces==1,"reference did not produce one real simulated wall contact");
        var target=new Target(known.Endpoint,8,new Vector3(8,8,4),Airspace:true);
        var world=new CountedWorld(scene);
        // Deliberately do not supply the reference angle as a seed. Both cases
        // exhausted this same budget with no result before the proposal fix.
        // Exercise the player's "use the first reviewed option" path: full
        // research may spend extra traces on additional robustness reviews,
        // but the first usable result must cost less than the old failed scan.
        using var search=new LineupSearch(world,physics,start,target,new(){IncludeActions=true,MaxCandidates=1600});
        while(search.Advance()) if(search.ReviewedCandidateCount>0) search.RequestFinish();
        var result=search.Result!;
        var found=result.Matches.Concat(result.Experimental ?? []).ToArray();
        assert(found.Length>0,"known reachable timed bank was missed");
        assert(result.Routes!.Any(r=>r.Lane=="bank" && r.Hits>0),"bank proposals never approached the known detonation region");
        assert(result.Evaluated<500 && world.Traces<340000,"first reviewed result cost more than the old failed scan");
        assert(result.Matches.All(c=>c.Reliability>=.75f) && (result.Experimental??[]).All(c=>c.Reliability<.75f),"low tolerance was mislabelled as recommended");
        foreach(var candidate in found)
        {
            var replay=SmokeSimulation.Run(scene,physics,start,candidate.Aim);
            assert(replay.Status==SimulationStatus.Detonated && target.Accepts(scene,replay.Endpoint),"proposal published without full collision validation");
        }
    }

    private sealed class CountedWorld(ICollisionWorld inner):ICollisionWorld
    {
        public int Traces;
        public SweepHit Sweep(Vector3 a,Vector3 b,float size){Traces++;return inner.Sweep(a,b,size);}
    }
}
