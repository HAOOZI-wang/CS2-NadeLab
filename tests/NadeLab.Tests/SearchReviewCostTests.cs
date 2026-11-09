using System.Numerics;
using NadeLab.Core;

internal static class SearchReviewCostTests
{
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        test("Moving review reuses exact release-window launches without dropping evidence",()=>
        {
            var scene=new AnalyticWorld();var physics=new PhysicsProfile();var start=new ThrowStart(Vector3.Zero);
            var aim=new Aim(-25,0,ThrowMode.Left,250,ThrowAction.Run);
            var known=SmokeSimulation.Run(scene,physics,start,aim);
            var target=new Target(known.Endpoint,8,new Vector3(256,256,64));
            var world=new CountedWorld(scene,physics.HullHalfSize);
            using var search=new LineupSearch(world,physics,start,target,
                new(){IncludeForward=true,SeedAims=[aim],MaxCandidates=1,ResultCount=1});
            while(search.Advance()) {}
            var result=search.Result!;
            assert(result.Matches.Count==1,"the known tolerant moving route was lost");
            var candidate=result.Matches[0];
            assert(candidate.Aim==aim && candidate.Trajectory.Endpoint==known.Endpoint,"review changed the nominal route");
            assert(candidate.RobustHits==25 && candidate.RobustTrials==25 && candidate.ExecutionHits==6 && candidate.ExecutionTrials==6,
                "the complete execution envelope or its evidence counts changed");
            assert(candidate.ModelHits==4 && candidate.ModelTrials==4 && candidate.SourceStressHits==6 && candidate.SourceStressTrials==6,
                "surface or source stress evidence was dropped");
            assert(candidate.ReleaseWindow==new ReleaseWindow(.1f,.1f,225,275,.05f),"release timing/speed coverage changed");
            var replay=SmokeSimulation.Run(scene,physics,start,candidate.Aim);
            assert(target.Accepts(scene,replay.Endpoint) && replay.Status==SimulationStatus.Rested,"published candidate failed full replay");
            Console.WriteLine($"  review cost: evaluated={result.Evaluated}, launches={world.Launches}, traces={world.Traces}, robust={candidate.RobustHits}/{candidate.RobustTrials}, execution={candidate.ExecutionHits}/{candidate.ExecutionTrials}");
            // One proposal, nominal review, 18 envelope cases, six manual cases,
            // eight distinct release-window cases, live publication replay and final publication replay.
            // The other seven window cases exactly repeat nominal/manual input.
            assert(result.Evaluated==1 && world.Launches==36,"release-window review repeated already checked nominal/manual launches");
        });
        test("Release-window reuse preserves a failed manual timing sample",()=>
        {
            var scene=new AnalyticWorld(-10000);var physics=Grenades.Bootstrap(GrenadeKind.Flash,new());
            var start=new ThrowStart(new(0,0,500));var aim=new Aim(-25,0,ThrowMode.Left,250,ThrowAction.Run);
            var known=SmokeSimulation.Run(scene,physics,start,aim);
            // The slow early throw misses the left edge; the other five manual
            // speed/timing samples pass. This remains a recommended route with
            // a shorter early window, not a falsely symmetric release window.
            var target=new Target(known.Endpoint+new Vector3(20,0,0),8,new Vector3(84,256,64),Airspace:true);
            var world=new CountedWorld(scene,physics.HullHalfSize);
            using var search=new LineupSearch(world,physics,start,target,
                new(){IncludeForward=true,SeedAims=[aim],MaxCandidates=1,ResultCount=1});
            while(search.Advance()) {}
            var result=search.Result!;
            assert(result.Matches.Count==1,"the asymmetric but tolerant route was lost");
            var candidate=result.Matches[0];
            assert(candidate.RobustHits==24 && candidate.RobustTrials==25 && candidate.ExecutionHits==5 && candidate.ExecutionTrials==6,
                "a failed manual sample was lost or promoted to a hit");
            assert(candidate.ReleaseWindow==new ReleaseWindow(.05f,.1f,225,275,.05f),$"failed early release was reused as a success: {candidate.ReleaseWindow}");
            Console.WriteLine($"  asymmetric review cost: evaluated={result.Evaluated}, launches={world.Launches}, traces={world.Traces}, robust={candidate.RobustHits}/{candidate.RobustTrials}, execution={candidate.ExecutionHits}/{candidate.ExecutionTrials}");
            // The first failed slow/early trial already short-circuited its
            // other two window inputs. Six repeated launches remain to reuse.
            assert(result.Evaluated==1 && world.Launches==35,"failed release-window samples still incurred duplicate simulations");
        });
    }

    private sealed class CountedWorld(ICollisionWorld inner,float hull):ICollisionWorld
    {
        public int Traces,Launches;
        public SweepHit Sweep(Vector3 a,Vector3 b,float halfSize)
        {
            Traces++;
            // This fixture's simulation steps are shorter than 22u. Only the
            // throw constructor uses this 22u hull sweep; connectivity uses .1u.
            if(halfSize==hull && MathF.Abs(Vector3.Distance(a,b)-22)<.001f) Launches++;
            return inner.Sweep(a,b,halfSize);
        }
    }
}
