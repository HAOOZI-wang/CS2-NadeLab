using System.Numerics;
using NadeLab.Core;

internal static class FlashVerificationTests
{
    private static FlashObserver[] Expected(int count=2)=>Enumerable.Range(1,count).Select(i=>
        new FlashObserver((uint)i,$"假人{i}",new(i*100,0,64),new(0,180,ThrowMode.Left),i%2==1 ? FlashRole.MustFlash : FlashRole.Protect)).ToArray();
    private static FlashNativeObservation[] Native(FlashObserver[] expected)=>expected.Select(x=>
        new FlashNativeObservation(x.Id,x.Id+100,x.Eye,x.View,true,0,0,0)).ToArray();
    private static void Samples(FlashVerificationTrial trial,FlashNativeObservation[] states,float end)
    {
        while(trial.LastSampleAt+.1f<end && !trial.Complete) trial.Observe(states,trial.LastSampleAt+.1f);
        if(!trial.Complete) trial.Observe(states,end);
    }
    private static void Detonate(FlashVerificationTrial trial,FlashNativeObservation[] states)
    {trial.Release(10);Samples(trial,states,11.5f);trial.Detonate(11.5f);}
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        test("Flash trial keeps eight independent observers and uses only this throw's events",()=>
        {
            var expected=Expected(8);var native=Native(expected);var trial=new FlashVerificationTrial(expected,native,10);
            assert(trial.BaselineClean,"clean baseline refused");Detonate(trial,native);
            foreach(var target in expected.Where(x=>x.Role==FlashRole.MustFlash))
                trial.Blind(target.Id,target.Id+100,target.Eye,target.View,3+target.Id*.1f,11.5f,true);
            var after=native.Select(x=>x.Id%2==1 ? x with {BlindUntil=15,BlindStart=11.5f,FlashDuration=3.5f} : x).ToArray();
            Samples(trial,after,12.01f);var result=trial.Results();
            assert(trial.Complete && result.Length==8,"full roster/window missing");
            assert(result.Count(x=>x.State==FlashMeasurementState.ObservedBlind)==4 && result.Count(x=>x.State==FlashMeasurementState.NoBlindObserved)==4,"independent blind/no-observation results lost");
            assert(result.Where(x=>x.Role==FlashRole.Protect).All(x=>x.EventDuration is null),"missing event was fabricated as measured zero");
            assert(result.All(x=>x.SampleCount>=20) && result.Where(x=>x.Role==FlashRole.MustFlash).All(x=>x.EventAt==11.5f),"window evidence was not retained");
            var next=new FlashVerificationTrial(expected,native,20);
            assert(next.Results().All(x=>x.EventDuration is null && x.State==FlashMeasurementState.Pending),"previous throw's blind duration leaked into new trial");
        });
        test("Flash trial rejects active baselines and permits expired historical duration",()=>
        {
            var expected=Expected();var native=Native(expected);
            foreach(var dirty in new[]{native[0] with {BlindUntil=11},native[0] with {BlindStart=9,FlashDuration=2},native[0] with {FlashDuration=2},native[0] with {FlashDuration=float.NaN}})
            {var trial=new FlashVerificationTrial(expected,[dirty,native[1]],10);assert(!trial.BaselineClean && trial.Complete,"active or unknown native baseline accepted");}
            var expired=new FlashVerificationTrial(expected,[native[0] with {BlindUntil=8,BlindStart=5,FlashDuration=3},native[1]],10);
            assert(expired.BaselineClean,"historical nonzero duration permanently blocked another throw");
        });
        test("Flash trial missing blind event with increased native state is unknown",()=>
        {
            var expected=Expected();var native=Native(expected);var trial=new FlashVerificationTrial(expected,native,10);Detonate(trial,native);
            native[1]=native[1] with {BlindUntil=14,FlashDuration=2.5f,BlindStart=11.5f};Samples(trial,native,12.01f);
            assert(trial.Results()[1].State==FlashMeasurementState.Unknown && trial.Results()[1].EventDuration is null,"native blind without event reported as unflashed");
            assert(trial.Results()[0].State==FlashMeasurementState.NoBlindObserved,"unaffected observer was not reported independently");
        });
        test("Flash trial interrupted continuous observation cannot certify missing events",()=>
        {
            var expected=Expected();var native=Native(expected);var trial=new FlashVerificationTrial(expected,native,10);trial.Release(10);trial.Observe(native,10.3f);
            assert(trial.Complete && trial.Results().All(x=>x.State==FlashMeasurementState.Unknown),"sampling gap yielded no-blind claim");
        });
        test("Flash trial invalidates moved turned deleted dead or replaced dummy independently",()=>
        {
            var expected=Expected();var native=Native(expected);
            var changed=new[]{native[0] with {Eye=native[0].Eye+new Vector3(3,0,0)},native[0] with {View=native[0].View with {Yaw=182}},
                native[0] with {Live=false},native[0] with {Pawn=999},native[0] with {Eye=new(float.NaN,0,0)}};
            foreach(var change in changed)
            {
                var trial=new FlashVerificationTrial(expected,native,10);Detonate(trial,native);
                Samples(trial,[change,native[1]],12.01f);
                assert(trial.Results()[0].State==FlashMeasurementState.Unknown && trial.Results()[1].State==FlashMeasurementState.NoBlindObserved,"pose/identity change was hidden or contaminated independent observer");
            }
            var missing=new FlashVerificationTrial(expected,native,10);Detonate(missing,native);Samples(missing,[native[1]],12.01f);
            assert(missing.Results()[0].State==FlashMeasurementState.Unknown,"deleted dummy was treated as protected");
        });
        test("Flash trial concurrent second flash contaminates the entire observation",()=>
        {
            var expected=Expected();var native=Native(expected);var trial=new FlashVerificationTrial(expected,native,10);
            trial.Release(10);trial.Release(10.1f);
            assert(trial.Complete && trial.Results().All(x=>x.State==FlashMeasurementState.Unknown),"overlapping throws merged into one successful trial");
        });
        test("Flash trial refuses incorrect event attribution duplicate or remote timestamps",()=>
        {
            var expected=Expected();var native=Native(expected);
            foreach(int mode in new[]{0,1,2,3,4})
            {
                var trial=new FlashVerificationTrial(expected,native,10);Detonate(trial,native);var target=expected[0];
                trial.Blind(target.Id,mode==3 ? 999u : target.Id+100,target.Eye,target.View,mode==4 ? float.NaN : 3,mode==2 ? 12 : 11.5f,mode!=0);
                if(mode==1) trial.Blind(target.Id,target.Id+100,target.Eye,target.View,3,11.5f,true);
                Samples(trial,native,12.01f);
                assert(trial.Results()[0].State==FlashMeasurementState.Unknown,"ambiguous, duplicate, malformed or stale event accepted");
            }
        });
        test("Flash trial native increase before release stays unknown even if later event matches",()=>
        {
            var expected=Expected();var native=Native(expected);var trial=new FlashVerificationTrial(expected,native,10);
            native[0]=native[0] with {BlindUntil=12,BlindStart=10,FlashDuration=2};trial.Observe(native,10.1f);trial.Release(10.1f);
            Samples(trial,native,11.5f);trial.Detonate(11.5f);var target=expected[0];trial.Blind(target.Id,target.Id+100,target.Eye,target.View,3,11.5f,true);
            Samples(trial,native,12.01f);assert(trial.Results()[0].State==FlashMeasurementState.Unknown,"pre-existing blindness became this throw's clean result");
        });
        test("Flash trial incomplete detonation and observation stay pending then become unknown",()=>
        {
            var expected=Expected();var native=Native(expected);var trial=new FlashVerificationTrial(expected,native,10);
            trial.Release(10);Samples(trial,native,11.5f);
            assert(trial.Results().All(x=>x.State==FlashMeasurementState.Pending),"missing detonation immediately became no-blind");
            Samples(trial,native,18.1f);assert(trial.Complete && trial.Results().All(x=>x.State==FlashMeasurementState.Unknown),"missing detonation never expired");
            var shortTrial=new FlashVerificationTrial(expected,native,10);Detonate(shortTrial,native);Samples(shortTrial,native,11.8f);
            assert(!shortTrial.Complete && shortTrial.Results().All(x=>x.State==FlashMeasurementState.Pending),"post-event window ended too early");
        });
        test("Flash trial zero duration does not override contradictory native blind state",()=>
        {
            var expected=Expected();var native=Native(expected);var trial=new FlashVerificationTrial(expected,native,10);Detonate(trial,native);
            var target=expected[0];trial.Blind(target.Id,target.Id+100,target.Eye,target.View,0,11.5f,true);
            native[0]=native[0] with {BlindUntil=14,BlindStart=11.5f,FlashDuration=2.5f};Samples(trial,native,12.01f);
            assert(trial.Results()[0].State==FlashMeasurementState.Unknown,"contradictory zero event certified no blindness");
        });
        test("Flash requirements compare every target independently using event duration and clean no-observation",()=>
        {
            var target=new FlashMeasurement(1,"A红",FlashRole.MustFlash,FlashMeasurementState.ObservedBlind,1,null);
            var protect=new FlashMeasurement(2,"B蓝",FlashRole.Protect,FlashMeasurementState.NoBlindObserved,null,null);
            assert(FlashVerificationRequirements.Overall([target,protect])==FlashRequirementState.Satisfied,"one-second event and clean protected observation did not satisfy this trial");
            assert(FlashVerificationRequirements.Evaluate(target with {EventDuration=.99f})==FlashRequirementState.Failed,"short event treated as one second");
            assert(FlashVerificationRequirements.Evaluate(target with {State=FlashMeasurementState.NoBlindObserved,EventDuration=null})==FlashRequirementState.Failed,"missing intended blindness passed");
            assert(FlashVerificationRequirements.Overall([target with {EventDuration=6},protect with {State=FlashMeasurementState.ObservedBlind,EventDuration=.01f}])==FlashRequirementState.Failed,"strong enemy flash offset friendly exposure");
            assert(FlashVerificationRequirements.Evaluate(protect with {State=FlashMeasurementState.Pending})==FlashRequirementState.Unknown,"pending observations passed protection");
            assert(FlashVerificationRequirements.Evaluate(protect with {State=FlashMeasurementState.Unknown})==FlashRequirementState.Unknown,"unknown observations passed protection");
        });
        test("Flash requirement aggregate reports explicit failure before unknown and skips ignored dummies",()=>
        {
            var pass=new FlashMeasurement(1,"A红",FlashRole.MustFlash,FlashMeasurementState.ObservedBlind,2,null);
            var unknown=new FlashMeasurement(2,"B蓝",FlashRole.Protect,FlashMeasurementState.Unknown,null,"观测中断");
            var fail=pass with {Id=3,EventDuration=.5f};
            assert(FlashVerificationRequirements.Overall([pass,unknown])==FlashRequirementState.Unknown,"unknown target was hidden by passing enemy");
            assert(FlashVerificationRequirements.Overall([pass,unknown,fail])==FlashRequirementState.Failed,"explicit requirement failure was hidden by unknown");
            assert(FlashVerificationRequirements.Overall([pass,unknown with {Role=FlashRole.Ignore}])==FlashRequirementState.Satisfied,"ignored dummy blocked trial");
            assert(FlashVerificationRequirements.Overall([])==FlashRequirementState.Unknown,"empty requirements were called success");
            assert(FlashVerificationRequirements.Evaluate(pass with {EventDuration=float.NaN})==FlashRequirementState.Unknown,"invalid event duration passed");
        });
        test("Flash requirement rows keep every verdict visible in the eight-dummy panel byte budget",()=>
        {
            var examples=new[]
            {
                new FlashMeasurement(1,"A红",FlashRole.MustFlash,FlashMeasurementState.ObservedBlind,1.25f,null),
                new FlashMeasurement(2,"B蓝",FlashRole.Protect,FlashMeasurementState.NoBlindObserved,null,null),
                new FlashMeasurement(3,"C绿",FlashRole.Protect,FlashMeasurementState.ObservedBlind,.01f,null),
                new FlashMeasurement(4,"D黄",FlashRole.MustFlash,FlashMeasurementState.Unknown,null,new string('字',100)),
                new FlashMeasurement(5,"E紫",FlashRole.MustFlash,FlashMeasurementState.Pending,null,null)
            };
            foreach(var row in examples)
            {
                string text=FlashVerificationRequirements.Text(row);
                assert(System.Text.Encoding.UTF8.GetByteCount(text)<=90 && !text.Contains('\uFFFD'),"bounded verdict row corrupted or oversized");
                assert(text.StartsWith(FlashVerificationRequirements.Evaluate(row) switch {FlashRequirementState.Satisfied=>"本次达标",FlashRequirementState.Failed=>"未达标",_=>"未知"},StringComparison.Ordinal),"verdict truncated before observation");
            }
            assert(FlashVerificationRequirements.Text(examples[0]).Contains("事件1.25s"),"event duration was described as full-white duration");
            assert(FlashVerificationRequirements.Text(examples[1]).Contains("本次未观察致盲"),"clean no-observation was changed into measured zero");
        });
    }
}
