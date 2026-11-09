using System.Numerics;
using NadeLab.Core;

public static class LiveSearchTests
{
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        test("Reviewed routes remain actionable while the search continues",()=>
        {
            var view=WorkflowModel.Resolve(new(Searching:true,HasTarget:true,HasOrigin:true,HasCandidate:true,ReviewedCandidate:true));
            assert(view.Primary==WorkflowAction.Prepare,"background search still intercepts adoption");
            assert(view.Availability(WorkflowAction.NextCandidate)==WorkflowAvailability.Available,"background search hides route navigation");
            var ready=WorkflowModel.Resolve(new(Searching:true,HasCandidate:true,Ready:true));
            assert(ready.Step==WorkflowStep.Ready,"search progress overwrites prepared throw guidance");
        });
        test("Collision reviewed candidates are published before search completion",()=>
        {
            var world=new AnalyticWorld();var physics=new PhysicsProfile();var start=new ThrowStart(Vector3.Zero);
            var aim=new Aim(-25,0,ThrowMode.Left);var endpoint=SmokeSimulation.Run(world,physics,start,aim).Endpoint;
            using var search=new LineupSearch(world,physics,start,new(endpoint,32,new Vector3(256,256,64)),
                new(){MaxCandidates=150,Exhaustive=true,SeedAims=[aim]});
            IReadOnlyList<Candidate> Published()=>search.PublishedCandidates;
            int units=0;while(search.Advance() && Published().Count==0 && ++units<2000000) {}
            assert(Published().Count>0 && !search.Done,"no actionable snapshot until final search completion");
            var first=Published()[0];int evaluated=search.Evaluated;
            assert(first.RobustTrials>0 && first.Trajectory.Points.Count>0 && world is not null,"snapshot lacks complete trajectory review");
            while(search.Advance()) {}
            assert(search.Evaluated>evaluated,"publishing stopped further exploration");
            assert(Published().Any(c=>ReferenceEquals(first,c)),"later review replaced an already published object");
            search.ContinueExpanded(new AnalyticWorld(-10));
            assert(Published().Count==0,"continuation advertises geometry from its previous world as fresh");
        });
        test("Incremental result merge preserves route numbers and filters unfinished entries",()=>
        {
            var trajectory=new Trajectory(new(50,0,2),SimulationStatus.Rested,2,0,[]);
            var a=new Candidate(new(-25,0,ThrowMode.Left),trajectory,0,9,12);
            var b=new Candidate(new(-26,3,ThrowMode.Left),trajectory,0,12,12);
            var unfinished=b with {Aim=new(-27,6,ThrowMode.Left),RobustTrials=0};
            var old=new SearchResult("test",false,12,false,[a],[]);
            var update=old with {Evaluated=100,Matches=[b,a with {Error=1},unfinished]};
            var merged=SearchPresentation.MergePublished(old,update);
            assert(merged.Matches.Count==2 && ReferenceEquals(merged.Matches[0],a) && ReferenceEquals(merged.Matches[1],b),"merge replaced, reordered or exposed an unfinished route");
            assert(merged.Evaluated==100,"progress metadata stopped updating");
            var stopped=SearchPresentation.MergePublished(merged,update with {Matches=[]});
            assert(stopped.Matches.Count==2 && ReferenceEquals(stopped.Matches[0],a),"finishing/empty update erased a usable route");
        });
        test("A newly found easier route is listed first without changing selection IDs",()=>
        {
            var trajectory=new Trajectory(new(50,0,2),SimulationStatus.Rested,2,0,[]);
            var hard=new Candidate(new(-80,0,ThrowMode.Left),trajectory,0,8,19);
            var easy=hard with {Aim=new(-30,20,ThrowMode.Left),RobustHits=19};
            var result=new SearchResult("test",false,100,false,[hard,easy],[]);
            var page=SearchPresentation.Page(result,0);
            assert(page.Choices[0].Number==2 && ReferenceEquals(page.Choices[0].Candidate,easy),"later useful route is buried behind the early low-tolerance choice");
            assert(ReferenceEquals(result.Matches[0],hard),"presentation renumbered existing adoption");
        });
        test("Flash publishes a checked choice while other angles are still explored",()=>
        {
            var physics=Grenades.Bootstrap(GrenadeKind.Flash,new());
            var observer=new FlashObserver(1,"test",new(1500,0,64),new(0,180,ThrowMode.Left),FlashRole.MustFlash);
            using var search=new FlashEffectSearch(new AnalyticWorld(),physics,new(Vector3.Zero),new([observer],.1f),(_,_)=>new(3,3),new(){MaxCandidates=300});
            int units=0;while(search.Advance() && search.PublishedCandidates.Count==0 && ++units<2000000) {}
            assert(search.PublishedCandidates.Count>0 && !search.Done,"flash only publishes on final completion");
            var first=search.PublishedCandidates[0];int evaluated=search.Evaluated;
            assert(first.GeometryAccepted && first.Candidate.RobustTrials>0,"unreviewed flash is actionable");
            while(search.Advance()) {}
            assert(search.Evaluated>evaluated && search.PublishedCandidates.Any(c=>ReferenceEquals(first,c)),"flash publication stopped exploration or changed route identity");
        });
    }
}
