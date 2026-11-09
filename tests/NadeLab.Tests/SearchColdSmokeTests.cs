using System.Numerics;
using NadeLab.Core;

public static class SearchColdSmokeTests
{
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        foreach(var (mode,pitch,yaw) in new[]{(ThrowMode.Left,-11.721781f,88.269775f),(ThrowMode.Both,-11.384638f,52.379654f)})
            test($"Cold stationary {mode} jump recovers a two-wall smoke landing",()=>
            {
                var scene=new AnalyticWorld(0,new Box(new(220,-500,0),new(230,500,400)),new Box(new(-250,-500,0),new(-240,500,400)),
                    new Box(new(-250,250,0),new(230,260,400)),new Box(new(-250,-260,0),new(230,-250,400)),new Box(new(-250,-500,200),new(230,500,210)));
                var physics=new PhysicsProfile();var start=new ThrowStart(Vector3.Zero);
                var family=ThrowCatalog.Families(physics,new(){IncludeActions=true},0,0).First(a=>a.EffectiveAction==ThrowAction.Jump && a.Mode==mode);
                var reference=SmokeSimulation.Run(scene,physics,start,family with {Pitch=pitch,Yaw=yaw});
                assert(reference.Status==SimulationStatus.Rested && reference.WallBounces>=2,"cold reference lost its multiple wall contacts");
                var target=new Target(reference.Endpoint,8,new Vector3(8,8,4));
                // The witness defines only the target. It is never supplied as
                // an angle hint, personal-library lookup or origin adjustment.
                using var search=new LineupSearch(scene,physics,start,target,new(){IncludeActions=true,MaxCandidates=1600});
                int first=-1;while(search.Advance()) if(first<0 && search.PublishedCandidates.Count>0) first=search.Evaluated;
                var choices=SearchPresentation.ForPlayer(search.Result!).Matches;
                Console.WriteLine($"  cold two-wall {mode}: first={first}; total={search.Evaluated}; choices={choices.Count}");
                assert(search.InitialHintCount==0 && first is >0 and <900 && choices.Count>0,"independent search lost the reachable multi-contact smoke");
                foreach(var candidate in choices)
                {
                    var replay=SmokeSimulation.Run(scene,physics,start,candidate.Aim);
                    assert(replay.Status==SimulationStatus.Rested && target.Accepts(scene,replay.Endpoint),"cold result failed full collision replay");
                }
            });
    }
}
