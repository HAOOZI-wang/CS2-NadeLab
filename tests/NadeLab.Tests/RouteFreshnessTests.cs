using System.Numerics;
using NadeLab.Core;

public static class RouteFreshnessTests
{
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        test("Adoption checks complete current flight even if old endpoint still lies inside target",()=>
        {
            var physics=new PhysicsProfile();var start=new ThrowStart(Vector3.Zero);
            var aim=new Aim(-25,0,ThrowMode.Left);var trajectory=SmokeSimulation.Run(new AnalyticWorld(),physics,start,aim);
            var candidate=new Candidate(aim,trajectory,0,19,19);
            var target=new Target(trajectory.Endpoint,64,new Vector3(128,128,64));
            var type=typeof(Aim).Assembly.GetType("NadeLab.Core.RouteFreshnessCheck");
            assert(type is not null,"incremental adoption flight gate missing");
            foreach(var (world,expected) in new[]{(new AnalyticWorld(),true),(new AnalyticWorld(-10),false)})
            {
                var check=Activator.CreateInstance(type!,world,physics,start,candidate,target)!;
                assert(!(bool)type!.GetProperty("Done")!.GetValue(check)!,"flight is declared checked before advancement");
                int steps=0;while((bool)type.GetMethod("Advance")!.Invoke(check,null)!) assert(++steps<10000,"unbounded fresh flight");
                assert((bool)type.GetProperty("Accepted")!.GetValue(check)! == expected,"stale landing/robustness evidence accepted after geometry changed");
            }
        });
        test("Fresh reviewed geometry gets a new stable ID without replacing a practiced aim",()=>
        {
            var old=new Candidate(new(-25,0,ThrowMode.Left),new(new(100,0,2),SimulationStatus.Rested,3,1,[],WallBounces:1,CollisionRoute:5),0,19,19);
            var fresh=old with {Trajectory=old.Trajectory with {Endpoint=new(101,0,2),Bounces=2,CollisionRoute=6}};
            var result=SearchPresentation.MergePublished(new("test",false,1,false,[old],[]),new("test",false,2,false,[fresh],[]));
            assert(result.Matches.Count==2 && ReferenceEquals(result.Matches[0],old) && ReferenceEquals(result.Matches[1],fresh),"changed same-angle route overwrote evidence or became inaccessible");
        });
    }
}
