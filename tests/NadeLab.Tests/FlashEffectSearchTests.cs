using System.Numerics;
using NadeLab.Core;

public static class FlashEffectSearchTests
{
    private static readonly PhysicsProfile Physics=Grenades.Bootstrap(GrenadeKind.Flash,new());
    private static readonly ThrowStart Start=new(Vector3.Zero);
    private static FlashObserver Observer(uint id,Vector3 eye,FlashRole role=FlashRole.MustFlash,float yaw=180)=>new(id,$"假人{id}",eye,new(0,yaw,ThrowMode.Left),role);
    private static void Complete(FlashEffectSearch search)
    {
        int guard=0;
        while(search.Advance())if(++guard>3000000)throw new Exception("Incremental search did not terminate.");
    }
    private static void Reject(Action action,Action<bool,string> assert,string message)
    {
        bool rejected=false;try{action();}catch(ArgumentException){rejected=true;}assert(rejected,message);
    }
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        test("Flash objective freezes 1–8 unique poses and requires at least one intended victim",()=>
        {
            var original=Observer(1,new(500,0,64));var mutable=new List<FlashObserver>{original};
            var objective=new FlashObjective(mutable);mutable[0]=original with {Eye=new(900,0,64),Role=FlashRole.Protect};
            assert(objective.Observers[0]==original,"search retained a mutable roster");
            Reject(()=>new FlashObjective([]),assert,"empty objective accepted");
            Reject(()=>new FlashObjective([original,original]),assert,"duplicate id overwrites a constraint");
            Reject(()=>new FlashObjective([original with {Role=FlashRole.Protect}]),assert,"all-protect objective has vacuous success");
            Reject(()=>new FlashObjective(Enumerable.Range(1,9).Select(i=>original with {Id=(uint)i})),assert,"more than eight observers accepted");
            Reject(()=>new FlashObjective([original with {Eye=new(float.NaN,0,0)}]),assert,"nonfinite observer accepted");
        });
        test("Flash constraints are individual: another enemy's effect cannot offset one victim or protector failure",()=>
        {
            var source=new Vector3(100,0,64);
            var near=Observer(1,new(200,0,64));var distant=Observer(2,new(2500,0,64));
            var protectedPlayer=Observer(3,new(200,30,64),FlashRole.Protect);
            var objective=new FlashObjective([near,distant,protectedPlayer]);
            var good=objective.Evaluate(source,near,new(3,3));var bad=objective.Evaluate(source,distant,new(3,3));
            var exposed=objective.Evaluate(source,protectedPlayer,new(3,3));
            var trajectory=new Trajectory(source,SimulationStatus.Detonated,1.5f,0,[]);
            var partlyGood=new FlashSearchCandidate(new(new(0,0,ThrowMode.Left),trajectory,0),[good,bad,exposed]);
            assert(good.GeometryAccepted && !bad.GeometryAccepted && !exposed.GeometryAccepted,"per-observer screening missing");
            assert(!partlyGood.GeometryAccepted && partlyGood.CandidateOnly && !partlyGood.StrictProtectionVerified,"partial result masquerades as an achieved objective");
        });
        test("Flash protection requires multiple valid blocked samples and never treats rear-facing as safe",()=>
        {
            var enemy=Observer(1,new(200,0,64));var ally=Observer(2,new(0,0,64),FlashRole.Protect,180);
            var objective=new FlashObjective([enemy,ally]);var burst=new Vector3(100,0,64);
            assert(!objective.Evaluate(burst,ally,new(3,3)).GeometryAccepted,"back-facing protection accepted");
            assert(!objective.Evaluate(burst,ally,new(0,1)).GeometryAccepted,"single blocked ray accepted");
            assert(!objective.Evaluate(burst,ally,new(1,3)).GeometryAccepted,"partially exposed protector accepted");
            assert(!objective.Evaluate(burst,ally,new(0,3,false)).GeometryAccepted,"invalid visibility counted as occlusion");
            assert(!objective.Evaluate(new(float.NaN,0,0),ally,new(0,3)).GeometryAccepted,"NaN endpoint counted as safe");
            var occluded=objective.Evaluate(burst,ally,new(0,3));
            assert(occluded.GeometryAccepted && !occluded.StrictProtectionVerified && occluded.Estimate.Maximum>0 && occluded.Description.Contains("待实投"),"geometric shadow falsely guarantees zero flash");
        });
        test("Flash search returns only whole-objective candidates and preserves the fixed origin",()=>
        {
            var world=new AnalyticWorld();var aim=new Aim(-15,0,ThrowMode.Left);
            var point=SmokeSimulation.Run(world,Physics,Start,aim).Endpoint;
            var objective=new FlashObjective([Observer(1,point+new Vector3(100,0,0)),Observer(2,point+new Vector3(150,0,0)),Observer(3,point+new Vector3(300,0,0),FlashRole.Protect)]);
            var observations=new List<Aim>();
            using var search=new FlashEffectSearch(world,Physics,Start,objective,(_,o)=>o.Role==FlashRole.Protect ? new(0,3) : new(3,3),
                new(){MaxCandidates=1,SeedAims=[aim]},a=>{observations.Add(a);return true;});
            Complete(search);
            assert(search.Result is {Matches.Count:>0} && search.Result.CandidateOnly,"known effect candidate absent");
            assert(search.Result!.Matches.All(c=>c.GeometryAccepted && c.Effects.Count==3 && c.Candidate.RobustTrials==5 && c.Candidate.Trajectory.Points.Count>0),"unreviewed/incomplete effect result published");
            assert(search.Start==Start && observations.All(a=>a.ForwardSpeed==0 && a.ForwardOffset==0 && a.EffectiveAction is ThrowAction.Standing or ThrowAction.Jump),"search moved the selected origin");
            Reject(()=>new FlashSearchOptions {SeedAims=[aim with {ForwardSpeed=100}]}.Validate(),assert,"moving seed bypassed fixed-origin constraint");
        });
        test("Unknown flash visibility excludes candidates and a failed finite search does not claim theoretical impossibility",()=>
        {
            var objective=new FlashObjective([Observer(1,new(500,0,64)),Observer(2,new(500,50,64),FlashRole.Protect)]);
            using var search=new FlashEffectSearch(new AnalyticWorld(),Physics,Start,objective,(_,_)=>new(0,3,false),new(){MaxCandidates=3});
            Complete(search);
            assert(search.Result is {Matches.Count:0,Truncated:true} && search.Result.Nearest.Count>0,"unknown exposure became a candidate");
            assert(search.Result!.Conclusion.Contains("不代表理论无解"),"bounded failure presented as proof of impossibility");
        });
        test("Flash Advance bounds native collision and visibility work independently of roster size",()=>
        {
            var world=new CountingWorld(new AnalyticWorld());int visibilityCalls=0;
            var objective=new FlashObjective(Enumerable.Range(1,8).Select(i=>Observer((uint)i,new(700+i*10,0,64))));
            using var search=new FlashEffectSearch(world,Physics,Start,objective,(_,_)=>{visibilityCalls++;return new(3,3);},new(){MaxCandidates=3});
            int guard=0;
            while(true)
            {
                int traces=world.Traces,vis=visibilityCalls;bool more=search.Advance();
                assert(world.Traces-traces<=4 && visibilityCalls-vis<=1,"one Advance ran an entire trajectory or roster");
                assert(world.Traces==traces || visibilityCalls==vis,"physics and visibility were combined in a slice");
                if(!more)break;if(++guard>100000)throw new Exception("search did not finish");
            }
        });
        test("Flash global search includes opposite headings and keeps reflected collision routes",()=>
        {
            var world=new AnalyticWorld(0,new Box(new(-350,-400,0),new(-300,400,800)));
            var bankAim=new Aim(-25,180,ThrowMode.Left);
            var known=SmokeSimulation.Run(world,Physics,Start,bankAim);
            assert(known.WallBounces>0,"bank fixture does not reflect");
            var observer=Observer(1,known.Endpoint+new Vector3(100,0,0));
            var objective=new FlashObjective([observer]);var attempted=new List<Aim>();
            using var search=new FlashEffectSearch(world,Physics,Start,objective,(p,_)=>Vector3.Distance(p,known.Endpoint)<90 ? new(3,3) : new(0,3),
                new(){MaxCandidates=1500,PreferredView=new(0,0,ThrowMode.Left)},a=>{attempted.Add(a);return true;});
            Complete(search);
            assert(attempted.Any(a=>MathF.Abs(Geometry.NormalizeYaw(a.Yaw-180))<.01f),"search stayed in a target-facing cone");
            assert(attempted.Any(a=>a.Airborne) && attempted.Select(a=>a.Mode).Distinct().Count()==3,"jump/strength families omitted");
            assert(search.Result!.Matches.Any(c=>c.Candidate.Trajectory.WallBounces>0),"known reflected flash discarded");
        });
        test("Flash search finish and disposal cancel unfinished work without publishing partial checks",()=>
        {
            var objective=new FlashObjective([Observer(1,new(500,0,64))]);var world=new CountingWorld(new AnalyticWorld());
            using(var early=new FlashEffectSearch(world,Physics,Start,objective,(_,_)=>new(3,3)))
            {
                early.RequestFinish();Complete(early);
                assert(early.Result is {Evaluated:0,Truncated:true,Matches.Count:0} && world.Traces==0,"finish started a new physical scan");
            }
            var disposed=new FlashEffectSearch(world,Physics,Start,objective,(_,_)=>new(3,3));
            disposed.Advance();disposed.Advance();int before=world.Traces;disposed.Dispose();
            assert(!disposed.Advance() && disposed.Done && disposed.Result is null && world.Traces==before,"disposed search continued or manufactured a result");
        });
        test("Flash search covers a clear window between coarse angles while keeping a second observer protected",()=>
        {
            // A 40u window in a wall at X=1000. A standing left-click at
            // (-32.5,15) bursts at (836.7,224.2,340.2); all five rays to
            // (1200,0,64) cross this opening, while the ally's rays hit the wall.
            var world=new AnalyticWorld(0,
                new Box(new(1000,-5000,0),new(1010,103.4f,3000)),
                new Box(new(1000,143.4f,0),new(1010,5000,3000)),
                new Box(new(1000,103.4f,0),new(1010,143.4f,196)),
                new Box(new(1000,103.4f,236),new(1010,143.4f,3000)));
            var objective=new FlashObjective([Observer(1,new(1200,0,64)),Observer(2,new(1200,400,64),FlashRole.Protect)]);
            PredictionVisibility Sight(Vector3 point,FlashObserver observer)
            {
                int clear=0;
                foreach(var offset in new[]{Vector3.Zero,new Vector3(3,0,0),new Vector3(-3,0,0),new Vector3(0,3,0),new Vector3(0,-3,0)})
                    if(world.Sweep(point,observer.Eye+offset,0).Fraction>=1)clear++;
                return new(clear,5);
            }
            var known=SmokeSimulation.Run(world,Physics,Start,new(-32.5f,15,ThrowMode.Left));
            assert(objective.Observers.All(o=>objective.Evaluate(known.Endpoint,o,Sight(known.Endpoint,o)).GeometryAccepted),"window fixture has no feasible throw");
            using var search=new FlashEffectSearch(world,Physics,Start,objective,Sight,
                new(){MaxCandidates=4000,PreferredView=new(0,0,ThrowMode.Left)});
            Complete(search);
            assert(search.Result is {Matches.Count:>0},$"feasible window missed after only {search.Evaluated}/4000 candidates");
            assert(search.Evaluated<=4000 && search.Start==Start && search.Result!.Matches.All(c=>
                c.GeometryAccepted && c.Effects[0].Estimate.Visibility.Open && c.Effects[1].Estimate.Visibility.Clear==0 &&
                c.Candidate.Aim.ForwardOffset==0 && c.Candidate.Aim.ForwardSpeed==0 && c.Candidate.RobustTrials>=5 &&
                c.CandidateOnly && !c.StrictProtectionVerified),"window recovery weakened an observer constraint or skipped review");
        });
        test("Flash jump hints use current release physics and sample early and late fixed-origin releases",()=>
        {
            var physics=Physics with {JumpImpulse=370};
            var jump=new Aim(-15,0,ThrowMode.Left,Action:ThrowAction.Jump,ReleaseHeight:JumpLatch.Height(physics),VerticalSpeed:JumpLatch.VerticalSpeed(physics));
            var point=SmokeSimulation.Run(new AnalyticWorld(),physics,Start,jump).Endpoint;
            var objective=new FlashObjective([Observer(1,point+new Vector3(200,0,0))]);
            var seen=new List<Aim>();
            using var search=new FlashEffectSearch(new AnalyticWorld(),physics,Start,objective,(_,_)=>new(3,3),
                new(){MaxCandidates=1,SeedAims=[jump with {ReleaseHeight=99,VerticalSpeed=0}]},a=>{seen.Add(a);return true;});
            Complete(search);
            assert(search.Result is {Matches.Count:>0},"current jump candidate missing");
            var result=search.Result!.Matches[0];
            assert(result.Candidate.Aim==jump && result.Candidate.RobustTrials==7,"hint copied stale jump physics or omitted timing probes");
            assert(seen.Any(a=>a.ReleaseHeight<jump.ReleaseHeight && a.VerticalSpeed>jump.VerticalSpeed) &&
                seen.Any(a=>a.ReleaseHeight>jump.ReleaseHeight && a.VerticalSpeed<jump.VerticalSpeed),"timing perturbations are not physically coupled");
            assert(seen.All(a=>a.ForwardOffset==0 && a.ForwardSpeed==0) && result.ReviewScope.Contains("待实投"),"timing probes moved origin or claimed actual effect");
        });
        test("Flash publication discards earlier reviewed candidates after collision or visibility changes",()=>
        {
            foreach(bool changeCollision in new[]{true,false})
            {
                var world=new MutableWorld();var aim=new Aim(-15,0,ThrowMode.Left);
                var point=SmokeSimulation.Run(world,Physics,Start,aim).Endpoint;
                var objective=new FlashObjective([Observer(1,point+new Vector3(100,0,0))]);
                bool unknown=false,changed=false,sawReviewed=false;
                using var search=new FlashEffectSearch(world,Physics,Start,objective,(_,_)=>unknown ? new(0,3,false) : new(3,3),
                    new(){MaxCandidates=1,SeedAims=[aim]});
                int guard=0;
                while(search.Advance())
                {
                    sawReviewed|=search.ReviewedCandidateCount>0;
                    if(search.Phase=="flash-publish" && !changed)
                    {changed=true;if(changeCollision)world.Blocked=true;else unknown=true;}
                    if(++guard>100000)throw new Exception("final geometry recheck did not finish");
                }
                assert(sawReviewed && changed && search.Result is {Matches.Count:0},"stale reviewed flash survived changed final geometry");
            }
        });
    }
    private sealed class CountingWorld(ICollisionWorld inner):ICollisionWorld
    {
        public int Traces { get; private set; }
        public SweepHit Sweep(Vector3 start,Vector3 end,float halfSize){Traces++;return inner.Sweep(start,end,halfSize);}
    }
    private sealed class MutableWorld:ICollisionWorld
    {
        public bool Blocked { get; set; }
        private readonly AnalyticWorld inner=new();
        public SweepHit Sweep(Vector3 start,Vector3 end,float halfSize)=>Blocked ? new(0,start,Vector3.Zero,true) : inner.Sweep(start,end,halfSize);
    }
}
