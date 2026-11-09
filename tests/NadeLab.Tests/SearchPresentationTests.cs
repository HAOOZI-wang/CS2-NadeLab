using System.Numerics;
using NadeLab.Core;

public static class SearchPresentationTests
{
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        test("Steep upward aim is visible without claiming a detected landmark",()=>
        {
            var t=new Trajectory(new(50,20,2),SimulationStatus.Rested,3,0,[]);
            var steep=new Candidate(new(-67.5f,0,ThrowMode.Left),t,0,19,19);
            assert(SearchPresentation.Label(steep).Contains("大仰角"),"steep result has no visible aiming cue");
            foreach(float pitch in new[]{-59f,0f,67.5f})
                assert(!SearchPresentation.Label(steep with {Aim=steep.Aim with {Pitch=pitch}}).Contains("大仰角"),"level or downward view mislabeled as sky-facing");
            var start=new ThrowStart(Vector3.Zero);var target=new Target(t.Endpoint);
            foreach(var candidate in new[]{steep,steep with {RobustHits=1},steep with {Aim=steep.Aim with {Action=ThrowAction.Jump}}})
            {
                string advice=SceneAdvisor.Explain(start,target,candidate,[candidate]);
                assert(advice.Contains("大仰角") && advice.Contains("未识别") && advice.Contains("继续寻找"),"action or tolerance advice hid the aiming limitation and alternative");
            }
        });
        test("Player gets fragile collision-checked routes without replacing evidence or candidate identity",()=>
        {
            var trajectory=new Trajectory(new(50,20,2),SimulationStatus.Rested,3,0,[]);
            var normal=new Candidate(new(-20,0,ThrowMode.Left),trajectory,0,19,19);
            var fragile=new Candidate(new(-25,5,ThrowMode.Left),trajectory,0,5,19);
            var raw=new SearchResult("test",false,2,false,[normal],[fragile],Experimental:[fragile]);
            var view=SearchPresentation.ForPlayer(raw);
            assert(view.Matches.Count==2 && ReferenceEquals(view.Matches[0],normal) && ReferenceEquals(view.Matches[1],fragile),"omitted fragile choice or changed candidate identity");
            assert(raw.Matches.Count==1 && ReferenceEquals(raw.Experimental,view.Experimental),"mutated raw recommendation/evidence collections");
            assert(SearchPresentation.RecommendedCount(view)==1 && SearchPresentation.LowToleranceCount(view)==1,"label counts misreport fragile routes as recommendations");
            assert(SearchPresentation.ForPlayer(view).Matches.Count==2,"reopening results duplicates experimental choices");
            assert(SearchPresentation.Label(fragile).Contains("低容错") && SearchPresentation.Label(normal).Contains("待实投"),"tolerance or actual-validation warning missing");
        });
        test("Player view never promotes nearest misses unfinished physics or unreviewed hits",()=>
        {
            var t=new Trajectory(new(50,20,2),SimulationStatus.Rested,3,0,[]);
            var unreviewed=new Candidate(new(-20,0,ThrowMode.Left),t,0);
            var blocked=unreviewed with {RobustHits=5,RobustTrials=19,Trajectory=t with {Status=SimulationStatus.Blocked}};
            var timedOut=blocked with {Trajectory=t with {Status=SimulationStatus.TimedOut}};
            var miss=blocked with {Trajectory=t};
            var raw=new SearchResult("test",false,1,true,[],[miss],Experimental:[unreviewed,blocked,timedOut]);
            assert(!SearchPresentation.HasUsable(raw) && SearchPresentation.ForPlayer(raw).Matches.Count==0,"miss/unreviewed/failed simulation exposed as selectable result");
            assert(SearchPresentation.Label(unreviewed).Contains("尚未"),"unreviewed evidence falsely labelled low tolerance");
        });
        test("A real narrow target is immediately selectable with a low-tolerance label",()=>
        {
            var world=new AnalyticWorld();var profile=new PhysicsProfile();var start=new ThrowStart(Vector3.Zero);
            var aim=new Aim(-25,0,ThrowMode.Left);var endpoint=SmokeSimulation.Run(world,profile,start,aim).Endpoint;
            var target=new Target(endpoint,3,new Vector3(3,3,12));
            using var search=new LineupSearch(world,profile,start,target,new SearchOptions {MaxCandidates=1,SeedAims=[aim]});
            bool seenReviewed=false;
            while(search.Advance()) seenReviewed|=search.ReviewedCandidateCount>0;
            var raw=search.Result!;var view=SearchPresentation.ForPlayer(raw);
            assert(seenReviewed && raw.Experimental is {Count:>0},"fixture did not expose reviewed low-tolerance progress");
            assert(view.Matches.Count>0 && view.Matches.All(c=>target.Accepts(world,c.Trajectory.Endpoint) && profile.Successful(c.Trajectory.Status)),"actionable list lost true hit or accepted a failed endpoint");
            assert(SearchExplanation.For(raw,target).NextStep.Contains("使用键就位"),"player still redirected to a hidden research menu");
        });
    }
}
