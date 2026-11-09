using System.Numerics;
using NadeLab.Core;

internal static class FlashCandidateTrialsTests
{
    private static readonly FlashObserver[] Observers=[new(1,"A",Vector3.Zero,new(0,0,ThrowMode.Left),FlashRole.MustFlash),
        new(2,"B",Vector3.One,new(0,0,ThrowMode.Left),FlashRole.Protect)];
    private static FlashMeasurement[] Rows(bool failed)=>[
        new(1,"A",FlashRole.MustFlash,FlashMeasurementState.ObservedBlind,2,null,true,100,12),
        failed ? new(2,"B",FlashRole.Protect,FlashMeasurementState.ObservedBlind,.59f,null,true,100,12)
               : new(2,"B",FlashRole.Protect,FlashMeasurementState.NoBlindObserved,null,null,false,100)];
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        test("Appending flash candidates preserves prior measurements and failed-route skipping",()=>
        {
            var h=new FlashCandidateTrials(2,Observers);h.Record(2,true,true,null,Rows(true));
            var old=h.Measurements(2);h.Grow(4);
            assert(ReferenceEquals(old,h.Measurements(2)) && h.Next(1)==3,"appending candidates erased trial evidence or changed IDs");
            assert(h.Record(4,true,true,null,Rows(false)),"new candidate has no trial slot");
            h.Grow(3);assert(h.Outcome(4)==FlashRequirementState.Satisfied,"smaller snapshot shrank history");
        });
        test("Flash trial history skips a previously failed candidate and wraps without losing retry",()=>
        {
            var h=new FlashCandidateTrials(3,Observers);
            assert(h.Record(2,true,true,null,Rows(true)),"qualified failed trial not retained");
            assert(h.Outcome(2)==FlashRequirementState.Failed && h.Next(1)==3 && h.Next(3)==1,"failed candidate selected again");
            assert(h.Measurements(2)?.Count==2 && h.Summary(2)!.Contains("上次") && h.Summary(2)!.Contains("未达标"),"past result not distinguishable");
        });
        test("Flash trial history keeps current selection when all alternatives failed",()=>
        {
            var h=new FlashCandidateTrials(2,Observers);h.Record(1,true,true,null,Rows(true));h.Record(2,true,true,null,Rows(true));
            assert(h.Next(1) is null && h.Next(2) is null,"all-failed loop returned another failed candidate");
            assert(new FlashCandidateTrials(1,Observers).Next(1) is null,"one candidate pretended to be another");
        });
        test("Flash trial history allows explicit retry to replace a failed result",()=>
        {
            var h=new FlashCandidateTrials(2,Observers);h.Record(2,true,true,null,Rows(true));
            assert(h.Record(2,true,true,null,Rows(false)) && h.Next(1)==2 && h.Outcome(2)==FlashRequirementState.Satisfied,"successful retry could not recover");
            assert(h.Summary(2)!.Contains("不保证"),"single success advertised a guarantee");
        });
        test("Flash trial history ignores partial unmatched invalid or unknown attempts",()=>
        {
            var h=new FlashCandidateTrials(2,Observers);var rows=Rows(true);
            assert(!h.Record(1,false,true,null,rows) && !h.Record(1,true,false,null,rows) && !h.Record(1,true,true,"another flash",rows),"unqualified attempt retained");
            assert(!h.Record(1,true,true,null,[rows[0]]) && !h.Record(1,true,true,null,[rows[0],rows[0]]),"partial or duplicate roster retained");
            assert(!h.Record(1,true,true,null,[rows[0],rows[1] with {State=FlashMeasurementState.Unknown}]),"unknown treated as failed");
            assert(!h.Record(1,true,true,null,[rows[0],rows[1] with {Role=FlashRole.MustFlash}]),"changed requirement accepted");
            assert(!h.Record(1,true,true,null,[rows[0],rows[1] with {EventDuration=float.NaN}]),"nonfinite event accepted");
            assert(!h.Record(1,true,true,null,[rows[0],rows[1] with {NativeIncreased=false}]),"uncorroborated event accepted");
            assert(h.Outcome(1) is null && h.Next(2)==1,"rejected attempt tainted selection");
        });
        test("Flash trial history snapshots measurements and cannot leak into a new plan",()=>
        {
            var h=new FlashCandidateTrials(2,Observers);var rows=Rows(true);h.Record(1,true,true,null,rows);
            rows[1]=Rows(false)[1];assert(h.Measurements(1)![1].EventDuration==.59f,"mutable rows changed history");
            assert(new FlashCandidateTrials(2,Observers).Outcome(1) is null,"new plan inherited old outcome");
            assert(!h.Record(1,true,false,null,Rows(false)) && h.Outcome(1)==FlashRequirementState.Failed,"uncertain retry erased valid past evidence");
        });
        test("Flash trial history rejects invalid candidate indices without growing state",()=>
        {
            var h=new FlashCandidateTrials(2,Observers);
            assert(!h.Record(0,true,true,null,Rows(true)) && !h.Record(3,true,true,null,Rows(true)),"out-of-range candidate accepted");
            assert(h.Outcome(0) is null && h.Next(0)==1 && h.Next(99)==1,"invalid index did not select first available candidate");
        });
    }
}
