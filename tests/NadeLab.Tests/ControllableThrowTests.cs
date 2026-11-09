using System.Numerics;
using NadeLab.Core;

public static class ControllableThrowTests
{
    private static readonly ThrowAction Air=(ThrowAction)8;
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        test("Quick search represents the observed 30u air-forward jump without a ground prestep",()=>
        {
            var p=new PhysicsProfile();
            var families=ThrowCatalog.Families(p,new(){IncludeActions=true,ForwardSpeed=245},2500,0);
            var air=families.Where(a=>a.EffectiveAction==Air).ToArray();
            assert(air.Length==3,"observed action is absent from quick search");
            assert(air.All(a=>a.Airborne && !a.NeedsRunUp && a.ForwardSpeed==30 && MathF.Abs(a.ForwardOffset-3)<.001f),"air-forward action inherited run-up or prestep geometry");
            assert(families.Any(a=>a.EffectiveAction==ThrowAction.Walk && MathF.Abs(a.ForwardSpeed-127.4f)<.01f),"walk plateau ignored grenade maximum speed");
            var old=ThrowCatalog.Families(p,new(){IncludeActions=true,Exhaustive=true},2500,0).Where(a=>a.EffectiveAction==ThrowAction.StepJump);
            assert(old.All(a=>a.ForwardSpeed>=60 && a.ForwardOffset==5+a.ForwardSpeed*JumpLatch.Seconds),"old step-jump identity changed");
        });
        test("Controlled jump ordering prefers reproducible actions without discarding reliability",()=>
        {
            var t=new Trajectory(new(100,0,2),SimulationStatus.Rested,4,1,[]);
            Candidate C(ThrowAction action,float speed=0)=>new(new(-30,0,ThrowMode.Left,speed,action),t,1,20,20);
            var jump=C(ThrowAction.Jump);var air=C(Air,30);var walk=C(ThrowAction.Walk,127.4f) with {ReleaseWindow=new(.1f,.1f,115,140,.05f)};var transient=C(ThrowAction.StepJump,60);
            assert(ReferenceEquals(SceneAdvisor.Order([walk,jump],null,true)[0],jump),"ordinary jump is considered harder than continuous walking");
            assert(ReferenceEquals(SceneAdvisor.Order([transient,air],null,true)[0],air),"unknown instantaneous speed outranks controlled air motion");
            assert(ReferenceEquals(SceneAdvisor.Order([air with {RobustHits=1},transient],null,true)[0],transient),"convenience suppressed real execution failures");
        });
        test("Memorable aim proxy avoids sky-only aim among comparably robust simple actions",()=>
        {
            var t=new Trajectory(new(100,0,2),SimulationStatus.Rested,4,0,[]);
            var sky=new Candidate(new(-80,0,ThrowMode.Left),t,1,20,20);
            var ordinary=sky with {Aim=new(-30,0,ThrowMode.Left,Action:ThrowAction.Jump)};
            assert(ReferenceEquals(SceneAdvisor.Order([sky,ordinary],new(-80,0,ThrowMode.Left),true)[0],ordinary),"tiny view turn keeps a featureless sky aim first");
        });
        test("Air-forward collision branches survive independently from ordinary jump branches",()=>
        {
            var t=new Trajectory(new(100,0,2),SimulationStatus.Rested,4,3,[],WallBounces:2,CollisionRoute:8);
            var branch=new CollisionBranches();
            branch.Observe(new(new(-30,0,ThrowMode.Left,30,Air),t,5),c=>c.Error);
            branch.Observe(new(new(-30,0,ThrowMode.Left,Action:ThrowAction.Jump),t,1),c=>c.Error);
            assert(branch.Seeds(c=>c.Error).Length==2,"air-forward window route branch was discarded");
        });
        test("Controlled air jump preserves takeoff geometry during joint execution perturbations",()=>
        {
            var p=new PhysicsProfile();var start=new ThrowStart(new(1216,-115,-163.96875f),62.296875f,ThrowOrigin.Preparation);
            var a=ThrowCatalog.Families(p,new(){IncludeActions=true},2500,-167.57825f).First(a=>a.EffectiveAction==Air && a.Mode==ThrowMode.Left) with {Pitch=-45.343353f};
            var plan=ThrowExecution.Resolve(start,a,p);
            assert(plan.PreparationFeet==start.Feet && plan.ButtonFeet==start.Feet,"controlled jump introduced a ground run");
            assert(Vector3.Distance(plan.ReleaseFeet,start.Feet+Geometry.Direction(0,a.Yaw)*3+Vector3.UnitZ*JumpLatch.Height(p))<.001f,"source has a 5u prestep or 100u run-up");
            var cases=ManualExecution.Cases(start,a,p).ToArray();
            assert(cases.Length==6 && cases.All(c=>c.Start==start && c.Aim.ForwardSpeed is 28 or 32 && c.Aim.ForwardOffset is >=.79f and <=5.21f),"new joint bounds or takeoff identity changed");
            assert(cases.Any(c=>c.Aim.ForwardOffset<1) && cases.Any(c=>c.Aim.ForwardOffset>5),"speed and displacement are not varied independently");
            var launch=new SmokeSimulation(new AnalyticWorld(-1000),p,start,a);
            assert(Vector3.Distance(launch.Velocity,new(-457.8497f,-100.95581f,792.8053f))<.5f,"launch no longer agrees with the recorded successful 30u release");
            assert(ThrowInstructions.Operation(a).Contains("勿先走一步") && a.Instruction.Contains("轻点W"),"controlled recipe is displayed as arbitrary speed matching");
            assert(!ThrowCatalog.Families(p,new(){IncludeActions=true,AirForwardSpeed=0},2500,0).Any(a=>a.EffectiveAction==Air),"unsupported server air settings do not disable proposal");
        });
        test("Controlled jump seed retains identity and validates body space through final publication",()=>
        {
            var p=new PhysicsProfile();var start=new ThrowStart(new(0,0,0),64,ThrowOrigin.Preparation);
            var a=ThrowCatalog.Families(p,new(){IncludeActions=true},0,0).First(a=>a.EffectiveAction==Air && a.Mode==ThrowMode.Left) with {Pitch=-30};
            var trajectory=SmokeSimulation.Run(new AnalyticWorld(),p,start,a);
            var target=new Target(trajectory.Endpoint,128);
            var calls=new List<(ThrowStart Start,Aim Aim)>();
            IEnumerable<bool?> Check(ThrowStart s,Aim aim){calls.Add((s,aim));yield return null;yield return true;}
            var options=new SearchOptions {IncludeActions=true,MaxCandidates=1,SeedAims=[a with {ForwardSpeed=31,ForwardOffset=8}]};
            using var search=new LineupSearch(new AnalyticWorld(),p,start,target,options,executionSpace:Check);
            while(search.Advance()){}
            assert(search.Result!.Matches.Any(c=>c.Aim==a),"quick seed snapped to 60 or preserved obsolete source parameters");
            assert(calls.Any(c=>c.Aim.ForwardSpeed==28) && calls.Any(c=>c.Aim.ForwardOffset>5),"joint execution trials bypassed body geometry");
            assert(calls.Count(c=>c.Start==start && c.Aim==a)>=3,"nominal and final source did not recheck space");
            using var blocked=new LineupSearch(new AnalyticWorld(),p,start,target,options,executionSpace:(_,_)=>new bool?[]{false});
            while(blocked.Advance()){}
            assert(blocked.Result!.Matches.Count==0 && (blocked.Result.Experimental?.Count ?? 0)==0,"blocked takeoff published a grenade-only route");
            using var missing=new LineupSearch(new AnalyticWorld(),p,start,target,options);
            while(missing.Advance()){}
            assert(missing.Result!.Matches.Count==0,"missing body validator accepted controlled jump");
        });
    }
}
