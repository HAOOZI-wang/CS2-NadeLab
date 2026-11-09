using System.Numerics;
using NadeLab.Core;

public static class SearchResultCapacityTests
{
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        test("Every reviewed seeded route remains selectable beyond six results",()=>
        {
            var world=new AnalyticWorld();var profile=new PhysicsProfile();var start=new ThrowStart(Vector3.Zero);
            var center=SmokeSimulation.Run(world,profile,start,new(-25,0,ThrowMode.Left)).Endpoint;
            var target=new Target(center,32,new Vector3(256,256,64));
            var seeds=Enumerable.Range(0,12).Select(i=>new Aim(-25,i-5.5f,ThrowMode.Left)).ToArray();
            using var search=new LineupSearch(world,profile,start,target,new(){MaxCandidates=12,SeedAims=seeds});
            while(search.Advance()) {}
            var raw=search.Result!;var view=SearchPresentation.ForPlayer(raw);
            assert(search.ReviewedCandidateCount==12,"fixture did not complete all twelve independent route reviews");
            assert(view.Matches.Count==12,$"reviewed routes disappeared before selection: {view.Matches.Count}/12");
            assert(seeds.All(a=>view.Matches.Any(c=>c.Aim==a)),"a reviewed angle cannot be selected");
            assert(view.Matches.All(c=>c.RobustTrials>0 && profile.Successful(c.Trajectory.Status) && target.Accepts(world,c.Trajectory.Endpoint)),
                "larger result capacity promoted an unfinished or invalid route");
            var reopened=SearchPresentation.ForPlayer(view);
            assert(view.Matches.Zip(reopened.Matches).All(pair=>ReferenceEquals(pair.First,pair.Second)),"reopening changed candidate identities or order");
        });
        test("More result storage does not enlarge the final physics review batch",()=>
        {
            var world=new AnalyticWorld();var profile=new PhysicsProfile();var start=new ThrowStart(Vector3.Zero);
            var endpoint=SmokeSimulation.Run(world,profile,start,new(-25,0,ThrowMode.Left)).Endpoint;
            using var search=new LineupSearch(world,profile,start,new(endpoint,256),new(){MaxCandidates=80,Exhaustive=true});
            while(search.Advance()) {}
            assert(search.ReviewedCandidateCount>0 && search.ReviewedCandidateCount<=5,
                $"expanded storage increased the final review batch: {search.ReviewedCandidateCount}");
        });
        test("Continued search publishes all newly reviewed alternatives using current geometry",()=>
        {
            var world=new AnalyticWorld();var profile=new PhysicsProfile();var start=new ThrowStart(Vector3.Zero);
            var aim=new Aim(-25,0,ThrowMode.Left);var endpoint=SmokeSimulation.Run(world,profile,start,aim).Endpoint;
            var target=new Target(endpoint,32,new Vector3(256,256,64));
            using var search=new LineupSearch(world,profile,start,target,new(){MaxCandidates=1,SeedAims=[aim]});
            while(search.Advance()) {}
            assert(search.Result!.Matches.Count==1,"fixture needs one original reviewed route");
            var currentWorld=new AnalyticWorld(-10);
            search.ContinueExpanded(currentWorld);
            int units=0;
            while(search.Advance())
            {
                if(search.ReviewedCandidateCount>=9) search.RequestFinish();
                if(++units>3000000) throw new Exception($"continued search did not finish within bounded work: evaluated={search.Evaluated}, reviewed={search.ReviewedCandidateCount}, phase={search.Phase}");
            }
            var view=SearchPresentation.ForPlayer(search.Result!);
            assert(search.ReviewedCandidateCount>=7 && search.ReviewedCandidateCount<=24,"fixture did not find a bounded set beyond six");
            assert(view.Matches.Count==search.ReviewedCandidateCount,
                $"continued pool hid reviewed alternatives: {view.Matches.Count}/{search.ReviewedCandidateCount}");
            assert(view.Matches.All(c=>c.RobustTrials>0 && c.Trajectory.Endpoint.Z<0 && target.Accepts(currentWorld,c.Trajectory.Endpoint)),
                "continuation exposed old geometry or unfinished evidence");
        });
        test("Candidate pages expose every numbered result without changing references or selecting one",()=>
        {
            var trajectory=new Trajectory(new(50,20,2),SimulationStatus.Rested,3,0,[]);
            var candidates=Enumerable.Range(0,12).Select(i=>new Candidate(new(-25,i,ThrowMode.Left),trajectory,0,19,19)).ToArray();
            var result=new SearchResult("test",false,12,false,candidates,[]);
            var first=SearchPresentation.Page(result,0);var second=SearchPresentation.Page(result,1);var last=SearchPresentation.Page(result,99);
            assert(first.PageIndex==0 && first.PageCount==3 && first.TotalCount==12,"wrong first-page bounds");
            assert(first.Choices.Select(c=>c.Number).SequenceEqual(new[]{1,2,3,4,5}),"first page indices changed");
            assert(second.Choices.Select(c=>c.Number).SequenceEqual(new[]{6,7,8,9,10}),"routes beyond six are missing");
            assert(last.PageIndex==2 && last.Choices.Select(c=>c.Number).SequenceEqual(new[]{11,12}),"last page dropped or renumbered candidates");
            foreach(var choice in first.Choices.Concat(second.Choices).Concat(last.Choices))
                assert(ReferenceEquals(choice.Candidate,result.Matches[choice.Number-1]),"page copied candidate or changed its selection identity");
            assert(ReferenceEquals(result.Matches,candidates),"page navigation changed the source results");
            assert(SearchPresentation.Page(result,-3).PageIndex==0,"negative page not clamped");
            var empty=SearchPresentation.Page(result with {Matches=[]},3);
            assert(empty.PageIndex==0 && empty.PageCount==1 && empty.Choices.Count==0,"empty result has an invalid page");
        });
        test("Candidate pages do not expose unfinished evidence or renumber later choices",()=>
        {
            var t=new Trajectory(new(50,20,2),SimulationStatus.Rested,3,0,[]);
            var unreviewed=new Candidate(new(-25,0,ThrowMode.Left),t,0);
            var reviewed=unreviewed with {RobustHits=19,RobustTrials=19};
            var blocked=reviewed with {Trajectory=t with {Status=SimulationStatus.Blocked}};
            var result=new SearchResult("test",false,3,false,[unreviewed,blocked,reviewed],[reviewed]);
            var page=SearchPresentation.Page(result,0);
            assert(page.Choices.Count==1 && page.Choices[0].Number==3 && ReferenceEquals(page.Choices[0].Candidate,reviewed),
                "page promoted unfinished evidence or shifted the selection id");
        });
        test("Flash keeps reviewed choices beyond six without expanding its review allowance",()=>
        {
            var physics=Grenades.Bootstrap(GrenadeKind.Flash,new());var start=new ThrowStart(Vector3.Zero);
            var seeds=Enumerable.Range(0,24).Select(i=>new Aim(-15,(i-11.5f)*3,ThrowMode.Left)).ToArray();
            var observer=new FlashObserver(1,"test",new(1500,0,64),new(0,180,ThrowMode.Left),FlashRole.MustFlash);
            var objective=new FlashObjective([observer],.1f);
            using var search=new FlashEffectSearch(new AnalyticWorld(),physics,start,objective,(_,_)=>new(3,3),new(){MaxCandidates=24,SeedAims=seeds});
            while(search.Advance()) {}
            var result=search.Result!;
            // Two early reviews plus the existing eighteen final reviews. The
            // other four nominal hits must remain unpublished and unvalidated.
            assert(result.Matches.Count==20,$"flash retained {result.Matches.Count}/20 reviewed choices");
            // Each completed candidate gets one additional fresh nominal check
            // before live publication. The twenty-candidate allowance is fixed.
            assert(search.StabilityProbes==140,$"flash changed its bounded review cost: {search.StabilityProbes}");
            assert(result.Matches.All(c=>c.GeometryAccepted && c.Candidate.RobustTrials==5 && c.CandidateOnly && !c.StrictProtectionVerified),
                "flash result capacity promoted nominal-only hits or claimed actual validation");
        });
    }
}
