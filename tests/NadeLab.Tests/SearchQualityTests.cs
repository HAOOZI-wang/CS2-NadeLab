using System.Numerics;
using System.Reflection;
using NadeLab.Core;

public static class SearchQualityTests
{
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        Candidate C(float pitch,ThrowAction action=ThrowAction.Standing)=>new(new(pitch,0,ThrowMode.Left,Action:action),
            new(new(200,0,2),SimulationStatus.Rested,3,0,[]),0,19,19);
        bool Stops(params Candidate[] candidates)
        {
            using var search=new LineupSearch(new AnalyticWorld(),new(),new(Vector3.Zero),new(new(200,0,2),32,new Vector3(96,96,24)));
            var matches=(List<Candidate>)typeof(LineupSearch).GetField("matches",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(search)!;
            matches.AddRange(candidates);
            return (bool)typeof(LineupSearch).GetProperty("SimpleAreaHit",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(search)!;
        }
        test("A weak angle or speed dimension cannot be hidden by a passing aggregate score",()=>
        {
            var angle=C(-30) with {RobustHits=15,Checks=[new("angle",4,8,20,10),new("position",4,4,0,0)]};
            var moving=C(-30,ThrowAction.Walk) with {ExecutionHits=5,ExecutionTrials=6,Checks=[new("motion-speed",1,2,20,10),new("motion-timing",4,4,0,0)]};
            assert(angle.Reliability>.75f && moving.Reliability>.75f,"fixture must expose aggregate masking");
            assert(!SearchPresentation.IsRecommended(angle) && !SearchPresentation.IsRecommended(moving),"weak execution group still recommended");
            assert(SearchPresentation.ForPlayer(new("test",false,1,false,[],[],Experimental:[angle,moving])).Matches.Count==2,"low-tolerance valid landings were hidden");
            assert(!Stops(angle),"weak angle route ends exploration");
        });
        test("Quick completion requires useful variety rather than sky-only or duplicate choices",()=>
        {
            assert(!Stops(C(-80),C(-79,ThrowAction.Jump)),"sky-only routes stop better-route exploration");
            assert(!Stops(C(-30),C(-30.1f)),"nearly identical routes satisfy variety");
            assert(Stops(C(-30),C(-35,ThrowAction.Jump)),"two executable choices never finish bounded quick search");
            assert(Stops(C(-30,ThrowAction.Crouch),C(-35,ThrowAction.AirForwardJump)),"controlled crouch/air actions excluded from quality policy");
        });
        test("Balanced execution precedes weak groups while source uncertainty stays separate",()=>
        {
            var weak=C(-30) with {RobustHits=15,Checks=[new("angle",4,8,20,10)]};
            var balanced=C(-40,ThrowAction.Jump) with {RobustHits=15,Checks=[new("angle",6,8,4,1)]};
            assert(ReferenceEquals(SceneAdvisor.Order([weak,balanced],null,true)[0],balanced),"convenience outranks independent execution weakness");
            var uncertain=balanced with {SourceStressHits=1,SourceStressTrials=6,Checks=[new("source",1,6,50,20)]};
            assert(SearchPresentation.IsRecommended(uncertain),"source stress was misrepresented as hand execution probability");
            assert(SearchPresentation.Label(uncertain).Contains("模型待验证"),"source uncertainty missing from compact player label");
        });
    }
}
