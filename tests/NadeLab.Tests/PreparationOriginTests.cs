using System.Numerics;
using System.Text.Json;
using NadeLab.Core;

public static class PreparationOriginTests
{
    private static ThrowStart Fixed()=>JsonSerializer.Deserialize("""{"Feet":{"X":0,"Y":0,"Z":0},"EyeHeight":64,"Origin":1,"RunUpDistance":100}""",NadeLabJsonContext.Default.ThrowStart);
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        test("Fixed preparation origin launches after forward run-up instead of at the selected feet",()=>
        {
            var profile=new PhysicsProfile();var aim=new Aim(-30,0,ThrowMode.Left,250,ThrowAction.RunJump,JumpLatch.Height(profile),JumpLatch.VerticalSpeed(profile),25);
            var actual=new SmokeSimulation(new AnalyticWorld(0),profile,Fixed(),aim);
            var expected=new SmokeSimulation(new AnalyticWorld(0),profile,new ThrowStart(new(100,0,0)),aim);
            assert(Vector3.Distance(actual.Position,expected.Position)<.001f,$"new selected origin still releases 100u too early: {actual.Position} vs {expected.Position}");
            assert(actual.Velocity==expected.Velocity,"origin conversion changed launch velocity");
        });
        test("Fixed preparation ground run adds button delay once while legacy input remains unchanged",()=>
        {
            var profile=new PhysicsProfile {ReleaseDelaySeconds=.2f};var aim=new Aim(-20,90,ThrowMode.Left,250,ThrowAction.Run);
            var actual=new SmokeSimulation(new AnalyticWorld(0),profile,Fixed(),aim);
            var expected=new SmokeSimulation(new AnalyticWorld(0),profile,new ThrowStart(new(0,150,0)),aim);
            assert(Vector3.Distance(actual.Position,expected.Position)<.001f,"100u run-up plus 50u delay was not applied exactly once");
        });
        test("Preparation remains fixed for every heading and non-running actions retain their original trajectory",()=>
        {
            var p=new PhysicsProfile();var start=Fixed() with {Feet=new(30,40,0)};
            foreach(float yaw in new[]{0f,90f,180f,-90f})
            {
                var a=new Aim(-30,yaw,ThrowMode.Left,250,ThrowAction.RunJump,JumpLatch.Height(p),JumpLatch.VerticalSpeed(p),25);
                var plan=ThrowExecution.Resolve(start,a,p);
                assert(plan.PreparationFeet==start.Feet,"candidate changed the selected preparation point");
                assert(Vector3.Distance(plan.ButtonFeet,start.Feet+Geometry.Direction(0,yaw)*100)<.001f,"button line did not follow candidate heading");
                assert(Vector3.Distance(plan.ReleaseFeet,plan.ButtonFeet+Geometry.Direction(0,yaw)*25+Vector3.UnitZ*JumpLatch.Height(p))<.001f,"run-up or jump drift counted twice");
            }
            foreach(var a in ThrowCatalog.Families(p,new SearchOptions {IncludeActions=true,Exhaustive=true},0,0).Where(a=>!a.NeedsRunUp))
            {
                var current=new SmokeSimulation(new AnalyticWorld(0),p,start,a);
                var legacy=new SmokeSimulation(new AnalyticWorld(0),p,start with {Origin=ThrowOrigin.Release},a);
                assert(current.Position==legacy.Position && current.Velocity==legacy.Velocity,"stationary/step action silently moved");
            }
        });
        test("Timing samples retain the same preparation point and check earlier or later points on its forward path",()=>
        {
            var p=new PhysicsProfile {ReleaseDelaySeconds=.2f};var start=Fixed();
            foreach(var a in new[]{new Aim(-20,30,ThrowMode.Left,250,ThrowAction.Run),new Aim(-30,30,ThrowMode.Left,250,ThrowAction.RunJump,JumpLatch.Height(p),JumpLatch.VerticalSpeed(p),25)})
            {
                var trials=ManualExecution.Cases(start,a,p).ToArray();
                assert(trials.All(t=>t.Start.Feet==start.Feet),"timing moved the preparation point and skipped a path prefix");
                assert(trials.Any(t=>t.Start.RunUpDistance<100) && trials.Any(t=>t.Start.RunUpDistance>100),"no earlier/later button positions were sampled");
                assert(ReleaseWindow.Cases(start,a,p).All(t=>t.Start.Feet==start.Feet),"release-window trials moved preparation point");
                if(a.Airborne) assert(trials.All(t=>MathF.Abs(t.Aim.ForwardOffset-t.Aim.ForwardSpeed*JumpLatch.Seconds)<.001f),"jump latch used a ground-release delay");
            }
        });
        test("New and legacy origin records round trip without reinterpreting old release data",()=>
        {
            var old=JsonSerializer.Deserialize("""{"Feet":{"X":1,"Y":2,"Z":3},"EyeHeight":64}""",NadeLabJsonContext.Default.ThrowStart);
            assert(old.Origin==ThrowOrigin.Release,"missing origin migrated old data silently");
            var value=Fixed();var saved=JsonSerializer.Serialize(value,NadeLabJsonContext.Default.ThrowStart);
            assert(JsonSerializer.Deserialize(saved,NadeLabJsonContext.Default.ThrowStart)==value,"origin metadata was not persisted");
            var library=new RouteLibrary();var target=new Target(new(300,0,2));var aim=new Aim(-20,0,ThrowMode.Left,250,ThrowAction.Run);
            library.Remember(new(Vector3.Zero),target,aim,"world","physics",true);
            assert(library.Hints(value,target,null,"world","physics").Length==0,"old release-point hint was reused as a preparation-origin hint");
            assert(!ThrowExecution.Valid(value with {Origin=(ThrowOrigin)8}) && !ThrowExecution.Valid(value with {RunUpDistance=float.NaN}),"invalid origin metadata accepted");
            var nearby=NearbyStart.Find(value,new(new(2000,0,2),32,new(50,50,10)),p=>p,(_,_)=>true);
            assert(nearby?.Origin==ThrowOrigin.Preparation,"nearby search dropped origin semantics");
        });
        test("Verification and sampled button window agree with fixed start and nondefault delay",()=>
        {
            var p=new PhysicsProfile {ReleaseDelaySeconds=.2f};var start=Fixed();var aim=new Aim(-20,0,ThrowMode.Left,250,ThrowAction.Run);
            var plan=ThrowExecution.Resolve(start,aim,p);
            var observation=new ThrowObservation(plan.ReleaseFeet,64,aim,1,aim.PlayerVelocity,false,10,true,true);
            assert(Verification.Matches(start,aim,observation,10,p),"actual resolved release was rejected");
            assert(!Verification.Matches(start,aim,observation with {Feet=start.Feet},10,p),"throw from preparation feet was accepted as a completed run-up");
            var window=new ReleaseWindow(.1f,.1f,225,275,.05f);
            var edges=ThrowExecution.WindowEdges(start,aim,p,window);
            assert(MathF.Abs(edges.Early.X-77.5f)<.001f && MathF.Abs(edges.Late.X-122.5f)<.001f,"button window double-counted release delay");
            var trajectory=new Trajectory(new(300,0,2),SimulationStatus.Rested,2,0,[]);
            var candidate=new Candidate(aim,trajectory,0,ReleaseWindow:window);
            foreach(float speed in new[]{225f,250f,275f})
                foreach(float button in new[]{edges.Early.X,edges.Late.X})
                {
                    var o=observation with {Feet=new(button+speed*p.ReleaseDelaySeconds,0,0),Velocity=new(speed,0,0)};
                    assert(Verification.TryGuidedSetup(start,candidate,o,10,out var adjusted,out var adjustedAim,p),"visible boundary rejected by guided verification");
                    assert(adjusted.Feet==start.Feet,"guided verification replaced fixed preparation point");
                    assert(ReleaseGuidance.Stage(new(button,0,0),plan.ButtonFeet,new(speed,0,0),aim,true,window,0)==ReleaseStage.Window,"HUD disagrees with sampled button region");
                }
        });
        test("Search validates new forward execution geometry for nominal perturbations and final publication",()=>
        {
            var p=new PhysicsProfile();var start=Fixed();var a=new Aim(-30,0,ThrowMode.Left,250,ThrowAction.RunJump,JumpLatch.Height(p),JumpLatch.VerticalSpeed(p),25);
            var simulation=new SmokeSimulation(new AnalyticWorld(0),p,start,a);while(!simulation.Done)simulation.Step();
            var target=new Target(simulation.Position,128);var calls=new List<(ThrowStart Start,Aim Aim)>();
            IEnumerable<bool?> Check(ThrowStart s,Aim aim){calls.Add((s,aim));yield return null;yield return true;}
            var options=new SearchOptions {IncludeActions=true,ForwardSpeed=250,SeedAims=[a],MaxCandidates=1};
            using var search=new LineupSearch(new AnalyticWorld(0),p,start,target,options,executionSpace:Check);
            int limit=0;while(search.Advance() && limit++<100000){}
            assert(search.Done && search.Result!.Matches.Count>0,"fixed-start seed did not publish");
            assert(search.Result!.Matches[0].RunUpFeet==start.Feet,"published candidate moved preparation point");
            assert(calls.Any(t=>t.Aim.Yaw!=a.Yaw) && calls.Any(t=>t.Start.RunUpDistance!=100),"perturbed direction/timing bypassed execution-space checks");
            assert(calls.Count(t=>t.Start==start && t.Aim==a)>=3,"nominal, review and final publication did not independently check player path");
            using var blocked=new LineupSearch(new AnalyticWorld(0),p,start,target,options,executionSpace:(_,_)=>new bool?[]{false});
            while(blocked.Advance()){}
            assert(blocked.Result!.Matches.Count==0,"blocked player path published a grenade-only route");
            using var missing=new LineupSearch(new AnalyticWorld(0),p,start,target,options);
            while(missing.Advance()){}
            assert(missing.Result!.Matches.Count==0,"missing movement validator silently accepted fixed-start run-up");
        });
    }
}
