using System.Numerics;
using System.Text.Json;
using NadeLab.Core;

int passed=0,failed=0;
void Test(string name,Action test)
{
    try { test(); Console.WriteLine("PASS " + name); passed++; }
    catch(Exception ex) { Console.WriteLine("FAIL " + name + ": " + ex.Message); failed++; }
}
void Assert(bool value,string reason) { if(!value) throw new Exception(reason); }
void Near(float a,float b,float eps=.001f) => Assert(MathF.Abs(a-b)<eps,$"{a} != {b}");
EffectPlacementTests.Run(Test,Assert);
TargetConnectivityTests.Run(Test,Assert);
GrenadeTargetTransferTests.Run(Test,Assert);
EffectDrawingTests.Run(Test,Assert);
SearchPresentationTests.Run(Test,Assert);
LiveSearchTests.Run(Test,Assert);
SearchQualityTests.Run(Test,Assert);
SearchColdSmokeTests.Run(Test,Assert);
ReleaseCueTests.Run(Test,Assert);
RouteFreshnessTests.Run(Test,Assert);
SearchResultCapacityTests.Run(Test,Assert);
PreparationOriginTests.Run(Test,Assert);
ControllableThrowTests.Run(Test,Assert);
PersonalSmokeHintTests.Run(Test,Assert);
TargetIdentityTests.Run(Test,Assert);
EffectPredictionTests.Run(Test,Assert);
FlashEffectSearchTests.Run(Test,Assert);
FlashVerificationTests.Run(Test,Assert);
FlashCandidateTrialsTests.Run(Test,Assert);
DummyLayoutTests.Run(Test,Assert);
RecentImpactTests.Run(Test,Assert);
PersonalCaptureTests.Run(Test,Assert);
MenuHoldTests.Run(Test,Assert);
CompanionInputTests.Run(Test,Assert);
MenuEquipmentTests.Run(Test,Assert);
DummyBatchTests.Run(Test,Assert);
PersonalLineupTests.Run(Test,Assert);
PersonalFlashHintTests.Run(Test,Assert);
GrenadeInventoryTests.Run(Test,Assert);
LaunchObservationTests.Run(Test,Assert);
SearchTimedBankTests.Run(Test,Assert);
SearchDownwardBankTests.Run(Test,Assert);
SearchInverseRefinementTests.Run(Test,Assert);
SearchReviewCostTests.Run(Test,Assert);
RouteLibraryHintTests.Run(Test,Assert);
MapCatalogTests.Run(Test,Assert);
FlightPreviewTests.Run(Test,Assert);
SpawnTravelTests.Run(Test,Assert);
WorldIdentityTests.Run(Test,Assert);
DummyRosterTests.Run(Test,Assert);
PanelTextTests.Run(Test,Assert);
MenuNavigationTests.Run(Test,Assert);
GrenadeBodyCameraTests.Run(Test,Assert);
RouteDiagnosisTests.Run(Test,Assert);
WorkflowModelTests.Run(Test,Assert);
ThrowInstructionTests.Run(Test,Assert);
Test("Search slice backs off promptly during server stalls and expensive plugin work",()=>
{
    var budget=new SearchFrameBudget();
    budget.Observe(1,15.625,1);Near((float)budget.Milliseconds(1),1);
    budget.Observe(2,30,1);Near((float)budget.Milliseconds(2),.5f);
    budget.Observe(2.9,15.625,1);Near((float)budget.Milliseconds(2.9),.5f);
    Near((float)budget.Milliseconds(3.1),1);
    budget.Observe(4,15.625,4);Near((float)budget.Milliseconds(4),.5f);
    budget.Observe(4.9,50,1);Near((float)budget.Milliseconds(5.8),.5f);
    Near((float)budget.Milliseconds(6),1);
});
Test("Timed grenades burst in air at the fuse rather than waiting for rest",()=>
{
    foreach(var kind in new[]{GrenadeKind.Flash,GrenadeKind.HighExplosive})
    {
        var profile=Grenades.Bootstrap(kind,new());
        var simulation=new SmokeSimulation(new EmptyWorld(),profile,new Vector3(0,0,1000),new Vector3(100,0,100));
        while(!simulation.Done) simulation.Step();
        var r=simulation.Result();Near(r.Time,profile.FuseSeconds);Assert(r.Status==SimulationStatus.Detonated,"not a timed air burst");
        Near(r.Endpoint.X,100*profile.FuseSeconds,.01f);Assert(profile.Successful(r.Status),"valid effect rejected");
        Assert(!new PhysicsProfile().Successful(r.Status),"smoke accepted flash termination");
    }
});
Test("Ground contact does not prematurely detonate HE and fire needs suitable ground",()=>
{
    var he=Grenades.Bootstrap(GrenadeKind.HighExplosive,new());
    var ground=new AnalyticWorld(0);
    var sim=new SmokeSimulation(ground,he,new Vector3(0,0,4),Vector3.Zero);
    while(!sim.Done) sim.Step();
    Near(sim.Result().Time,he.FuseSeconds);Assert(sim.Status==SimulationStatus.Detonated,"HE stopped on contact");
    foreach(var kind in new[]{GrenadeKind.Molotov,GrenadeKind.Incendiary})
    {
        var profile=Grenades.Bootstrap(kind,new());
        var fire=new SmokeSimulation(ground,profile,new Vector3(0,0,40),new Vector3(20,0,-80));
        while(!fire.Done) fire.Step();
        Assert(fire.Status==SimulationStatus.Ignited && fire.Result().Time<profile.FuseSeconds,"fire failed valid floor");
        var air=new SmokeSimulation(new EmptyWorld(),profile,new Vector3(0,0,1000),Vector3.UnitX*100);
        while(!air.Done) air.Step();
        Assert(air.Status==SimulationStatus.Extinguished && !profile.Successful(air.Status),"air timeout falsely counted as fire");
    }
});
Test("Grenade profile and target identities cannot inherit smoke calibration",()=>
{
    var smoke=new PhysicsProfile {Calibrated=true};
    var he=Grenades.Bootstrap(GrenadeKind.HighExplosive,smoke);
    Assert(!he.Calibrated && he.Kind==GrenadeKind.HighExplosive && smoke.Kind==GrenadeKind.Smoke,"profile leak");
    var legacy=JsonSerializer.Deserialize<PhysicsProfile>("{}");Assert(legacy!.Kind==GrenadeKind.Smoke,"legacy changed kind");
    var target=new Target(Vector3.Zero,48,new Vector3(48));Assert(!target.SameDefinition(target with {Airspace=true}),"target semantics ignored");
    Assert(GrenadeKind.Molotov.Weapon()!=GrenadeKind.Incendiary.Weapon(),"fire inventory mixed");
});
Test("HE search publishes timed endpoints with full area checks",()=>
{
    var profile=Grenades.Bootstrap(GrenadeKind.HighExplosive,new());var start=new ThrowStart(new(0,0,100));
    var world=new EmptyWorld();var known=SmokeSimulation.Run(world,profile,start,new(-20,0,ThrowMode.Left));
    var target=new Target(known.Endpoint,64,new Vector3(64),Airspace:true);
    using var search=new LineupSearch(world,profile,start,target,new SearchOptions {MaxCandidates=1200,SeedAims=[new(-20,0,ThrowMode.Left)]});
    while(search.Advance()) {}
    Assert(search.Result!.Matches.Count>0,"known timed solution missing");
    Assert(search.Result.Matches.All(c=>c.Trajectory.Status==SimulationStatus.Detonated && c.Reliability>=.75f),"bad timed publication");
});
var physics = new PhysicsProfile();
foreach(var kind in new[]{GrenadeKind.Flash,GrenadeKind.HighExplosive,GrenadeKind.Molotov,GrenadeKind.Incendiary,GrenadeKind.Decoy})
Test($"{kind} finds an unseeded reachable area with its own termination rule",()=>
{
    var profile=Grenades.Bootstrap(kind,new());var origin=new ThrowStart(Vector3.Zero);
    var world=new AnalyticWorld(0);var known=SmokeSimulation.Run(world,profile,origin,new(-15,23,ThrowMode.Left));
    Assert(profile.Successful(known.Status),"fixture must produce an effect");
    var target=new Target(known.Endpoint,64,new Vector3(64,64,kind.Timed() ? 48 : 12),Airspace:kind.Timed());
    using var search=new LineupSearch(world,profile,origin,target,new SearchOptions {MaxCandidates=1600});
    while(search.Advance()) {}
    Assert(search.Result!.Matches.Count>0,"unseeded solution missing");
    Assert(search.Result.Matches.All(c=>profile.Successful(c.Trajectory.Status) && target.Accepts(world,c.Trajectory.Endpoint)),"wrong endpoint published");
});
var start = new ThrowStart(Vector3.Zero);
Test("Free flight agrees with analytic ballistic solution",() =>
{
    var s=new SmokeSimulation(new EmptyWorld(),physics,start,new(-30,25,ThrowMode.Left));
    var p=s.Position; var v=s.Velocity;
    for(int i=0;i<20;i++) s.Step();
    float t=20*physics.StepSeconds;
    Assert(Vector3.Distance(s.Position,p+v*t-new Vector3(0,0,.5f*physics.Gravity*t*t))<.005f,"ballistic integration drift");
});
Test("Hull hits a wall before its center crosses the wall",() =>
{
    var world=new AnalyticWorld(0,new Box(new(100,-100,0),new(102,100,200)));
    var h=world.Sweep(new(50,0,64),new(150,0,64),2);
    Near(h.Fraction,.48f); Near(h.Position.X,98); Assert(h.Normal==-Vector3.UnitX,"wrong wall normal");
});
Test("Wall reflection reverses horizontal velocity",() =>
{
    var world=new AnalyticWorld(0,new Box(new(100,-100,0),new(102,100,200)));
    var s=new SmokeSimulation(world,physics,start,new(0,0,ThrowMode.Left));
    for(int i=0;i<30 && s.Velocity.X>=0;i++) s.Step();
    Assert(s.Velocity.X<0,"wall did not reflect velocity");
});
Test("Stationary smoke settles above the hull floor",() =>
{
    var r=SmokeSimulation.Run(new AnalyticWorld(),physics,start,new(-25,0,ThrowMode.Left));
    Assert(r.Status==SimulationStatus.Rested,"smoke did not settle");
    Assert(r.Endpoint.Z>=2 && r.Endpoint.Z<2.1f,"hull penetrated floor");
    Assert(r.Time>=physics.MinDetonateSeconds,"premature smoke detonation");
    Assert(r.Bounces>0,"no ground contact");
});
Test("Unbounded flight times out instead of being called a hit",() =>
{
    var r=SmokeSimulation.Run(new EmptyWorld(),physics,start,new(0,0,ThrowMode.Left));
    Assert(r.Status==SimulationStatus.TimedOut,"missing flight timeout");
});
Test("Release inside a solid volume is rejected",() =>
{
    var world=new AnalyticWorld(0,new Box(new(-30,-30,0),new(30,30,100)));
    var r=SmokeSimulation.Run(world,physics,start,new(0,0,ThrowMode.Left));
    Assert(r.Status==SimulationStatus.Blocked,"inside solid wasn't rejected");
});
Test("Inverse search finds a known reachable target and tests perturbations",() =>
{
    var world=new AnalyticWorld();
    var known=SmokeSimulation.Run(world,physics,start,new(-25,15,ThrowMode.Left));
    var target=new Target(known.Endpoint,16);
    using var search=new LineupSearch(world,physics,start,target,new SearchOptions{YawSpan=20,SeedCount=4,MaxCandidates=2500});
    int advances=0;
    while(search.Advance()) { if(++advances>3000000) throw new Exception("search unbounded"); }
    var result=search.Result!;
    Assert(result.Matches.Count>0,"did not recover known throw");
    Assert(result.Matches.All(c=>target.Contains(c.Trajectory.Endpoint) && c.Trajectory.Status==SimulationStatus.Rested),"invalid hit");
    Assert(result.Matches.All(c=>c.RobustTrials==1+ExecutionEnvelope.Perturbations(start,c.Aim).Count() && c.RobustHits>=1 && c.RobustHits<=c.RobustTrials),"missing perturbation tests");
    Assert(!result.Calibrated,"demo was falsely marked calibrated");
    var roundtrip=JsonSerializer.Deserialize<SearchResult>(JsonSerializer.Serialize(result,new JsonSerializerOptions{IncludeFields=true}),new JsonSerializerOptions{IncludeFields=true})!;
    Assert(Vector3.Distance(roundtrip.Matches[0].Trajectory.Endpoint,result.Matches[0].Trajectory.Endpoint)<.01,"JSON vector lost");
});
Test("Candidate budget is bounded and explicitly reported",() =>
{
    using var s=new LineupSearch(new AnalyticWorld(),physics,start,new(new(100000,0,0)),new SearchOptions{MaxCandidates=5});
    while(s.Advance()){}
    Assert(s.Result!.Evaluated==5 && s.Result.Truncated,"budget not respected");
    Assert(s.Result.Matches.Count==0,"unreachable target claimed hit");
});
Test("Direct throws survive ranking and finish without a dense grid",() =>
{
    var world=new CountingWorld(new AnalyticWorld());
    var known=SmokeSimulation.Run(world,physics,start,new(-25,15,ThrowMode.Left));
    world.Traces=0;
    using var search=new LineupSearch(world,physics,start,new(known.Endpoint,16));
    var phases=new HashSet<string>();
    while(search.Advance()) phases.Add(search.Phase);
    var result=search.Result!;
    Assert(result.Matches.Count>0 && result.Matches.All(c=>c.Trajectory.WallBounces==0),"lost clear direct throws");
    Assert(result.Matches.All(c=>c.Trajectory.Bounces>0),"ground contacts were mistaken for walls");
    // The accepted angle/position boundary is now checked with coupled errors,
    // roughly doubling the old nine-trajectory finalist check. Exploration must
    // still avoid a grid, with an explicit bound on total collision work.
    Assert(!phases.Contains("coarse") && result.Evaluated<250 && world.Traces<125000,$"easy direct target used {result.Evaluated} candidates / {world.Traces} queries / coarse={phases.Contains("coarse")}");
    Console.WriteLine($"  direct search: {result.Evaluated} candidates, {world.Traces} collision queries");
});
Test("Endpoint-only simulation preserves collisions without allocating a polyline",() =>
{
    var world=new AnalyticWorld(0,new Box(new(100,-100,0),new(102,100,200)));
    var aim=new Aim(-15,0,ThrowMode.Left);
    var full=SmokeSimulation.Run(world,physics,start,aim);
    var light=new SmokeSimulation(world,physics,start,aim,recordPoints:false);
    while(!light.Done) light.Step();
    var endpoint=light.Result();
    Assert(endpoint.Endpoint==full.Endpoint && endpoint.Time==full.Time && endpoint.WallBounces==full.WallBounces,"endpoint mode changed physics");
    Assert(endpoint.Points.Count==0 && full.Points.Count>0 && full.WallBounces>0,"collision classification/polyline retention failed");
});
Test("Expanded searches retain a bank alternative while preferring a direct route",() =>
{
    var world=new AnalyticWorld(0,new Box(new(-150,-1000,0),new(-148,1000,2000)));
    var bank=SmokeSimulation.Run(world,physics,start,new(-25,170,ThrowMode.Left));
    Assert(bank.Status==SimulationStatus.Rested && bank.WallBounces>0,"invalid mixed-route fixture");
    using var search=new LineupSearch(world,physics,start,new(bank.Endpoint,24),
        new SearchOptions{YawSpan=180,Exhaustive=true,MaxCandidates=5000});
    while(search.Advance()){}
    Assert(search.Result!.Matches.FirstOrDefault()?.Trajectory.WallBounces==0,"bank route displaced the direct choice");
    Assert(search.Result.Matches.Any(c=>c.Trajectory.WallBounces>0),"expanded search lost its bank alternative");
});
Test("Time-budget finish returns valid completed candidates",() =>
{
    var world=new AnalyticWorld();
    var known=SmokeSimulation.Run(world,physics,start,new(-25,15,ThrowMode.Left));
    using var search=new LineupSearch(world,physics,start,new(known.Endpoint,32));
    while(search.Evaluated<30) search.Advance();
    search.RequestFinish();
    int remaining=0;
    while(search.Advance()) Assert(++remaining<100000,"finish did not bound remaining work");
    Assert(search.Result!.Truncated && search.Evaluated<=31,"time limit not reported");
    Assert(search.Result.Matches.All(c=>c.Trajectory.Status==SimulationStatus.Rested && new Target(known.Endpoint,32).Contains(c.Trajectory.Endpoint)),"published an unfinished candidate");
});
Test("Generated JSON accessors preserve enum, vectors and collision summaries",() =>
{
    var full=SmokeSimulation.Run(new AnalyticWorld(),physics,start,new(-25,15,ThrowMode.Left));
    var record=new SearchResult(physics.Id,false,1,false,[new(new(-25,15,ThrowMode.Left),full,0)],[]);
    string json=JsonSerializer.Serialize(record,NadeLabJsonContext.Default.SearchResult);
    var copy=JsonSerializer.Deserialize(json,NadeLabJsonContext.Default.SearchResult)!;
    Assert(copy.Matches[0].Trajectory.Status==SimulationStatus.Rested && copy.Matches[0].Trajectory.Endpoint==full.Endpoint,"generated record lost enum/vector data");
    Assert(copy.Matches[0].Trajectory.WallBounces==full.WallBounces && copy.Matches[0].Trajectory.Points.Count==full.Points.Count,"generated record lost collision/polyline data");
});
Test("Failed focused search expands to recover a shot off a wall behind the player",() =>
{
    var world=new AnalyticWorld(0,
        new Box(new(-150,-1000,0),new(-148,1000,2000)),
        new Box(new(20,-1000,0),new(22,40,2000)));
    var known=SmokeSimulation.Run(world,physics,start,new(-25,170,ThrowMode.Left));
    Assert(known.Status==SimulationStatus.Rested && known.Endpoint.X>22,$"invalid bank-shot fixture {known.Endpoint}");
    var target=new Target(known.Endpoint,12);
    var opts=new SearchOptions{SeedCount=8,MaxCandidates=6000};
    using var focused=new LineupSearch(world,physics,start,target,opts with {GlobalFallback=false});
    while(focused.Advance()){}
    Assert(focused.Result!.Matches.Count==0,"fixture does not need a heading outside the focused cone");
    using var expanded=new LineupSearch(world,physics,start,target,opts);
    var phases=new HashSet<string>();
    while(expanded.Advance()) phases.Add(expanded.Phase);
    Assert(phases.Contains("global"),"no all-heading fallback");
    Assert(expanded.Result!.Matches.Any(c=>c.Trajectory.Points.Any(p=>p.X< -100)),
        $"did not recover a bank shot behind the player: known {known.Endpoint}; evaluated {expanded.Evaluated}; nearest "+
        string.Join("; ",expanded.Result.Nearest.Take(3).Select(c=>$"{c.Aim} error {c.Error:F1} end {c.Trajectory.Endpoint}")));
    Assert(expanded.Result.Evaluated<=opts.MaxCandidates,"fallback exceeded candidate budget");
});
Test("Canceled search cannot continue evaluating",() =>
{
    var s=new LineupSearch(new AnalyticWorld(),physics,start,new(new(800,0,2)));
    s.Advance(); s.Dispose(); Assert(!s.Advance() && s.Result==null,"canceled result published");
});
Test("Player placement never lowers feet and refuses insufficient standing clearance",() =>
{
    var feet=new Vector3(10,20,-.5f);
    bool Room(Vector3 p)=>p.Z>.25f && p.Z+72<74 && p.X==10 && p.Y==20;
    Assert(SafePlacement.TryRaise(feet,Room,out var safe) && safe.Z>feet.Z && Room(safe),"did not resolve a shallow floor overlap safely");
    Assert(!SafePlacement.TryRaise(feet,p=>p.Z>=0 && p.Z+72<=60,out _),"placed a standing player under a low ceiling");
    Assert(!SafePlacement.TryRaise(new(float.NaN,0,0),p=>true,out _),"accepted invalid recorded feet");
});
Test("Nonfinite target and invalid profiles fail before simulation",() =>
{
    bool target=false,profile=false;
    try { using var s=new LineupSearch(new AnalyticWorld(),physics,start,new(new(float.NaN,0,0))); } catch(ArgumentException) { target=true; }
    try { (physics with {StepSeconds=0}).Validate(); } catch(ArgumentException) { profile=true; }
    Assert(target&&profile,"invalid input accepted");
});
Test("Yaw wraps continuously at -180/180",() => { Near(Geometry.NormalizeYaw(181),-179); Near(Geometry.NormalizeYaw(-181),179); });
Test("Verification accepts a wrapped matching aim but rejects stale/moving/wrong-strength throws",() =>
{
    var aim=new Aim(-25,179.9f,ThrowMode.Both);
    var observed=new ThrowObservation(Vector3.Zero,64,new(-25,-179.9f,ThrowMode.Both),.5f,Vector3.Zero,true,10);
    Assert(Verification.Matches(start,aim,observed,10.02f),"wrapped aim rejected");
    Assert(!Verification.Matches(start,aim,observed,10.2f),"stale throw accepted");
    Assert(!Verification.Matches(start,aim,observed with {Velocity=new(20,0,0)},10.02f),"moving throw accepted");
    Assert(!Verification.Matches(start,aim,observed with {Strength=1},10.02f),"wrong strength accepted");
    Assert(!Verification.Matches(start,aim,observed with {Feet=new(10,0,0)},10.02f),"wrong position accepted");
    Assert(!Verification.Matches(start,aim,observed with {EyeHeight=46},10.02f),"crouch accepted");
    Assert(!Verification.Matches(start,aim,null,10.02f),"missing observation accepted");
});
Test("Captured long Mirage flight loses extra energy on its first heavy landing",() =>
{
    // Recorded 2026-10-06: first contact is the flat -264u floor. This fixture
    // checks the measured landing response without requiring the game runtime.
    var world=new AnalyticWorld(-264);
    var release=new ThrowStart(new(417.77893f,-667.6905f,-166.17625f),63.59375f);
    var aim=new Aim(-57,179.38774f,ThrowMode.Left);
    var measured=new Vector3(-1024.9935f,-651.8052f,-261.96875f);
    var corrected=SmokeSimulation.Run(world,physics,release,aim);
    var old=SmokeSimulation.Run(world,physics with {HeavyImpactSpeed=1000},release,aim);
    Assert(Vector3.Distance(corrected.Endpoint,measured)<8,"heavy landing no longer reproduces the captured endpoint");
    Assert(Vector3.Distance(old.Endpoint,measured)>80,"fixture does not expose the former landing error");
});
Test("Moving left throw inherits horizontal player velocity without changing release pitch",() =>
{
    var aim=new Aim(-30,90,ThrowMode.Left);
    var standing=new SmokeSimulation(new EmptyWorld(),physics,start,aim);
    var moving=new SmokeSimulation(new EmptyWorld(),physics,start,aim with {ForwardSpeed=250});
    Assert(Vector3.Distance(moving.Velocity-standing.Velocity,new(0,312.5f,0))<.01f,"missing/incorrect inherited velocity");
    Assert(moving.Position==standing.Position,"movement changed the nominal release position");
    var json=JsonSerializer.Serialize(aim with {ForwardSpeed=250},NadeLabJsonContext.Default.Aim);
    Assert(JsonSerializer.Deserialize(json,NadeLabJsonContext.Default.Aim).ForwardSpeed==250,"moving action lost in JSON");
});
Test("Long-range inverse search finds moving throws and refuses a blocked run-up",() =>
{
    var world=new AnalyticWorld();
    var known=SmokeSimulation.Run(world,physics,start,new(-25,15,ThrowMode.Left,250));
    var target=new Target(known.Endpoint,24);
    var options=new SearchOptions {IncludeForward=true,YawSpan=0,GlobalFallback=false,MaxCandidates=600};
    using var s=new LineupSearch(world,physics,start,target,options,aim=>true);
    while(s.Advance()){}
    Assert(s.Result!.Matches.Any(c=>c.Aim.ForwardSpeed==250),"did not recover the reachable W+left action");
    Assert(s.Result.Matches.Where(c=>c.Aim.ForwardSpeed>0).All(c=>c.ExecutionTrials==6 && c.RobustTrials==7+ExecutionEnvelope.Perturbations(start,c.Aim).Count()),"did not test coupled release speed and timing");
    using var blocked=new LineupSearch(world,physics,start,target,options,aim=>false);
    while(blocked.Advance()){}
    Assert(blocked.Result!.Matches.All(c=>c.Aim.ForwardSpeed==0),"recommended a blocked run-up");
    Console.WriteLine($"  moving search: {s.Evaluated} candidates; {s.Result.Matches.Count} matches");
});
Test("Moving verification checks projected release position, speed, direction and grounded posture",() =>
{
    var aim=new Aim(-25,0,ThrowMode.Left,250);
    var obs=new ThrowObservation(new(-3.90625f,0,0),64,new(-25,0,ThrowMode.Left),1,new(250,0,0),false,10,true);
    Assert(Verification.Matches(start,aim,obs,10.015625f),"valid W release rejected");
    Assert(!Verification.Matches(start,aim,obs with {Velocity=new(0,250,0)},10.015625f),"sideways action accepted as W");
    Assert(!Verification.Matches(start,aim,obs with {Velocity=new(220,0,0)},10.015625f),"wrong release speed accepted");
    Assert(!Verification.Matches(start,aim,obs with {GroundedUpright=false},10.015625f),"jump accepted as grounded motion");
    Assert(!Verification.Matches(start,aim,obs with {Feet=new(40,0,0)},10.015625f),"wrong release point accepted");
});
Test("Advice prefers simple standing shots but recognizes a materially shorter moving arc",() =>
{
    Candidate C(float pitch,float speed,float time)=>new(new(pitch,0,ThrowMode.Left,speed),new(new(800,0,2),SimulationStatus.Rested,time,2,[]),2,9,9);
    var stationary=C(-25,0,2); var moving=C(-15,250,2);
    Assert(SceneAdvisor.Order([moving,stationary])[0]==stationary,"unnecessary movement preferred");
    stationary=C(-60,0,5); moving=C(-20,250,2);
    Assert(SceneAdvisor.Order([stationary,moving])[0]==moving,"missed the flatter shorter moving route");
});
Test("Step support resolves a stair edge without lowering feet or crossing a ceiling",()=>
{
    var feet=new Vector3(-220,-332,-163.96875f);
    var floor=new SweepHit(.68f,new(-220,-332,-160.914f),Vector3.UnitZ);
    Assert(SafePlacement.TryStepSupport(feet,floor,p=>p.Z>=-160.9f,out var corrected),"valid 3.2u stair correction rejected");
    Assert(corrected.Z>feet.Z && corrected.Z-feet.Z<4,"incorrect stair height");
    Assert(!SafePlacement.TryStepSupport(feet,floor with {Position=feet-new Vector3(0,0,10)},p=>true,out _),"lowered player into a gap");
    Assert(!SafePlacement.TryStepSupport(feet,floor with {Position=feet+new Vector3(0,0,30)},p=>true,out _),"crossed an excessive step");
    Assert(!SafePlacement.TryStepSupport(feet,floor,p=>false,out _),"ignored standing clearance");
    Assert(!SafePlacement.TryStepSupport(feet,floor with {StartSolid=true},p=>true,out _),"accepted a solid support trace");
});
Test("Smoke landing regions accept broad floors but reject walls, wrong levels and ellipse corners",()=>
{
    var region=new Target(Vector3.Zero,120,new(72,120,20),"window");
    region.Validate();
    Assert(region.Contains(new(60,0,2)) && region.Contains(new(0,100,2)),"broader valid landing rejected");
    Assert(!region.Contains(new(65,110,2)),"ellipse corner accepted");
    Assert(!region.Contains(new(0,0,-100)),"different floor accepted");
    Assert(region.Accepts(new AnalyticWorld(),new(60,0,2)),"clear region rejected");
    Assert(!region.Accepts(new AnalyticWorld(0,new Box(new(40,-200,0),new(44,200,180))),new(60,0,2)),"landing behind a wall accepted");
    Near(region.MissDistance(new(60,0,2)),0);
    Assert(region.MissDistance(new(0,0,-100))>0,"wrong floor has no search penalty");
    var roundtrip=JsonSerializer.Deserialize(JsonSerializer.Serialize(region,NadeLabJsonContext.Default.Target),NadeLabJsonContext.Default.Target);
    Assert(roundtrip.IsArea && roundtrip.AreaHalfSize==region.AreaHalfSize && roundtrip.Label=="window","region lost in generated JSON");
    Assert(!new Target(Vector3.Zero,32).Contains(new(60,0,2)),"legacy precise target was silently widened");
});
Test("Area advice prefers a stable simple throw over exact-center movement",()=>
{
    Candidate C(float error,float speed,int robust=9)=>new(new(-25,0,ThrowMode.Left,speed),new(new(800,0,2),SimulationStatus.Rested,3,2,[]),error,robust,9);
    var standing=C(90,0); var moving=C(1,250);
    Assert(SceneAdvisor.Order([moving,standing],null,true)[0]==standing,"center accuracy displaced simple valid area shot");
    Assert(SceneAdvisor.Order([moving,standing with {RobustHits=1}],null,true)[0]==moving,"ignored poor region robustness");
    var bank=standing with {Trajectory=standing.Trajectory with {WallBounces=2},RobustHits=7};
    var fragile=standing with {Trajectory=standing.Trajectory with {WallBounces=1},RobustHits=3};
    Assert(SceneAdvisor.Order([fragile,bank],null,true)[0]==bank,"fewer contacts displaced much better region robustness");
});
Test("Area search finds valid broad landings and avoids unnecessary point precision",()=>
{
    var world=new AnalyticWorld();
    var known=SmokeSimulation.Run(world,physics,start,new(-25,15,ThrowMode.Left));
    var area=SmokeTargets.Around(known.Endpoint);
    using var search=new LineupSearch(world,physics,start,area);
    var phases=new HashSet<string>();
    while(search.Advance()) phases.Add(search.Phase);
    Assert(search.Result!.Matches.Count>0 && search.Result.Matches.All(c=>area.Accepts(world,c.Trajectory.Endpoint)),"invalid area candidates");
    Assert(!phases.Contains("precision") && search.Evaluated<150,"wasted precision work inside broad region");
    var offsetArea=SmokeTargets.Around(known.Endpoint+new Vector3(60,0,0));
    using var offsetSearch=new LineupSearch(world,physics,start,offsetArea,new SearchOptions{SeedAims=[new(-25,15,ThrowMode.Left)],MaxCandidates=150});
    while(offsetSearch.Advance()){}
    Assert(offsetSearch.Result!.Matches.Any(c=>c.Error>32 && c.Reliability>=.75f),"valid off-center seed rejected");
    Console.WriteLine($"  area search: {search.Evaluated} candidates");
});
Test("View preference handles yaw wrap and preserves collision and reliability priorities",()=>
{
    Candidate C(float pitch,float yaw,int walls=0,int hits=9,float error=2)=>new(new(pitch,yaw,ThrowMode.Left),new(new(800,0,2),SimulationStatus.Rested,3,2,[],walls),error,hits,9);
    var reference=new Aim(-20,179,ThrowMode.Left);
    var close=C(-20,-179); var far=C(-60,10);
    Near(SceneAdvisor.ViewTurn(close.Aim,reference),2);
    Assert(SceneAdvisor.Order([far,close],reference)[0]==close,"large turn preferred for equivalent shots");
    Assert(SceneAdvisor.Order([close with {RobustHits=1},far],reference)[0]==far,"view convenience displaced a much more reliable shot");
    Assert(SceneAdvisor.Order([close with {Trajectory=close.Trajectory with {WallBounces=1}},far],reference)[0]==far,"view preference displaced a clean direct shot");
    var steep=C(-80,179); var moderate=C(-25,179);
    Assert(SceneAdvisor.Order([steep,moderate],reference)[0]==moderate,"pitch turn ignored");
});
Test("Telemetry JSON envelopes use generated accessors with no reflection fallback",() =>
{
    var options=new JsonSerializerOptions {IncludeFields=true,TypeInfoResolver=NadeLabJsonContext.Default};
    var result=new SearchResult(physics.Id,false,1,false,[new(new(-25,15,ThrowMode.Left,250),
        SmokeSimulation.Run(new AnalyticWorld(),physics,start,new(-25,15,ThrowMode.Left,250)),2,9,11,new(-80,0,0))],[]);
    var envelope=new System.Text.Json.Nodes.JsonObject
    {
        ["Utc"]=DateTimeOffset.UtcNow.ToString("O"),
        ["Result"]=JsonSerializer.SerializeToNode(result,NadeLabJsonContext.Default.SearchResult),
        ["Samples"]=new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
        { ["Time"]=.015625f,["Position"]=JsonSerializer.SerializeToNode(new Vector3(1,2,3),NadeLabJsonContext.Default.Vector3) })
    };
    using var doc=JsonDocument.Parse(envelope.ToJsonString(options));
    Assert(doc.RootElement.GetProperty("Result").GetProperty("Matches")[0].GetProperty("RunUpFeet").GetProperty("X").GetSingle()==-80,"lost safe run-up point");
    Assert(doc.RootElement.GetProperty("Samples")[0].GetProperty("Position").GetProperty("Z").GetSingle()==3,"lost measured flight sample");
});
Test("Community action catalog includes right-click steps and all jump strengths",()=>
{
    var families=ThrowCatalog.Families(physics,new SearchOptions {IncludeActions=true,Exhaustive=true},800,0);
    Assert(families.Any(a=>a.Mode==ThrowMode.Right && a.EffectiveAction==ThrowAction.Step && a.ForwardOffset>0),"right-click one-step action missing");
    Assert(Enum.GetValues<ThrowMode>().All(mode=>families.Any(a=>a.Mode==mode && a.Airborne)),"jump throw strengths missing");
    Assert(families.Any(a=>a.EffectiveAction==ThrowAction.Walk) && families.Any(a=>a.EffectiveAction==ThrowAction.Crouch),"stance families missing");
});
Test("Jump and step simulation apply release offset and inherited vertical velocity",()=>
{
    var jump=ThrowCatalog.Families(physics,new SearchOptions {IncludeActions=true},800,0).First(a=>a.EffectiveAction==ThrowAction.Jump && a.Mode==ThrowMode.Left);
    var standing=new SmokeSimulation(new EmptyWorld(),physics,start,new(0,0,ThrowMode.Left));
    var airborne=new SmokeSimulation(new EmptyWorld(),physics,start,jump);
    Near(airborne.Position.Z-standing.Position.Z,jump.ReleaseHeight);
    Near(airborne.Velocity.Z-standing.Velocity.Z,jump.VerticalSpeed*physics.PlayerVelocityScale,.01f);
    var step=new Aim(0,0,ThrowMode.Right,100,ThrowAction.Step,ForwardOffset:5);
    var stepped=new SmokeSimulation(new EmptyWorld(),physics,start,step);
    var underhand=new SmokeSimulation(new EmptyWorld(),physics,start,new(0,0,ThrowMode.Right));
    Near(stepped.Position.X-underhand.Position.X,5);
    Near(stepped.Velocity.X-underhand.Velocity.X,125);
});
Test("Jump verification checks the latch instead of the live release phase",()=>
{
    var aim=new Aim(-25,0,ThrowMode.Left,Action:ThrowAction.Jump,ReleaseHeight:26.2f,VerticalSpeed:222);
    var obs=new ThrowObservation(new(0,0,26.2f),64,new(-25,0,ThrowMode.Left),1,new(0,0,222),false,10,OnGround:false,JumpThrow:true);
    Assert(Verification.Matches(start,aim,obs,10),"matching jump rejected");
    Assert(!Verification.Matches(start,aim,obs with {OnGround=true},10),"grounded throw verified as jump");
    Assert(Verification.Matches(start,aim,obs with {Velocity=new(0,0,50)},10),"cached jump rejected due to live vertical speed");
    Assert(!Verification.Matches(start,aim,obs with {JumpThrow=false},10),"unlatched jump verified");
    Assert(!Verification.Matches(start,new(-25,0,ThrowMode.Left),obs,10),"jump verified as stationary");
});
Test("An inverse search can recover a jump-only high platform target",()=>
{
    var world=new AnalyticWorld(0,new Box(new(250,-500,0),new(1500,500,160)));
    var families=ThrowCatalog.Families(physics,new SearchOptions {IncludeActions=true},800,0);
    var aim=families.First(a=>a.EffectiveAction==ThrowAction.Jump && a.Mode==ThrowMode.Both) with {Pitch=-25};
    var known=SmokeSimulation.Run(world,physics,start,aim);
    Assert(known.Status==SimulationStatus.Rested && known.Endpoint.Z>150,"invalid elevated fixture");
    using var s=new LineupSearch(world,physics,start,new(known.Endpoint,16),new SearchOptions {IncludeActions=true,YawSpan=0,MaxCandidates=2400,GlobalFallback=false});
    while(s.Advance()){}
    Assert(s.Result!.Matches.Any(c=>c.Aim.Airborne && c.Trajectory.Endpoint.Z>150),"jump family lost during inverse search/ranking");
});
Test("Recorded right jump launches match the latched initial velocity",()=>
{
    var aim=ThrowCatalog.Families(physics,new SearchOptions {IncludeActions=true},400,-124.34498f)
        .Single(a=>a.EffectiveAction==ThrowAction.Jump && a.Mode==ThrowMode.Right) with {Pitch=-33.99994f};
    var sim=new SmokeSimulation(new EmptyWorld(),physics,new(new(-971.336f,-376.77603f,-347.57706f),58.1875f),aim);
    Assert(Vector3.Distance(sim.Velocity,new(-87.231575f,-127.66133f,404.3444f))<.05f,"measured jump impulse mismatch");
    Assert(Verification.MatchesJumpLaunch(sim.Position,sim.Velocity,new(-87.231575f,-127.66133f,404.3444f),new(-979.59143f,-388.85754f,-253.07663f),1f/64),"recorded latch position mismatch");
    Assert(!Verification.MatchesJumpLaunch(sim.Position,sim.Velocity,new(-87.231575f,-127.66133f,53.911034f),new(-979.59143f,-388.85754f,-230.97404f),physics.StepSeconds),"late unlatched throw accepted");
});
Test("A run jump advances from takeoff to the latched release",()=>
{
    var aim=ThrowCatalog.Families(physics,new SearchOptions {IncludeActions=true},2650,0).First(a=>a.EffectiveAction==ThrowAction.RunJump);
    Near(aim.ForwardOffset,aim.ForwardSpeed*.1f); Near(aim.EyeHeight(new(Vector3.Zero,58)),64);
    Assert(ThrowCatalog.Families(physics,new SearchOptions {IncludeActions=true},500,0).Where(a=>a.Airborne).All(a=>MathF.Abs(a.VerticalSpeed-218.86838f)<.01f),"impossible late jump family remains");
});
Test("VIP excludes recorded leaking ledge even when trajectory prediction was accurate",()=>
{
    var target=SmokeTargets.MirageVip;
    Assert(!target.Contains(new(-1083.3132f,-672.5906f,-189.96875f)),"recorded leaking ledge still counts as a hit");
    Assert(!target.Contains(new(-1216,-636,-190)),"lower ledge allowed inside XY bounds");
    Assert(target.Contains(new(-1188,-641,-165.9375f)),"interior floor rejected");
    Assert(target.InteriorMargin(new(-1188,-641,-165.9375f))>.3f,"safe interior not distinguished");
});
Test("Quick search omits precise acceleration actions but expanded search retains them",()=>
{
    var quick=ThrowCatalog.Families(physics,new SearchOptions {IncludeActions=true},2700,0);
    Assert(quick.All(a=>a.EffectiveAction is not (ThrowAction.Step or ThrowAction.StepJump)),"fragile acceleration throw in quick search");
    Assert(quick.Any(a=>a.EffectiveAction==ThrowAction.Jump) && quick.Any(a=>a.EffectiveAction==ThrowAction.RunJump),"repeatable jump families removed");
    Assert(ThrowCatalog.Families(physics,new SearchOptions {IncludeActions=true,Exhaustive=true},2700,0).Any(a=>a.EffectiveAction==ThrowAction.StepJump),"experimental action lost");
});
Test("Manual perturbations include measured slow step jumps and coupled displacement",()=>
{
    var aim=new Aim(-23,0,ThrowMode.Left,60,ThrowAction.StepJump,26,219,11);
    var trials=ManualExecution.Perturbations(start,aim,physics).ToArray();
    Assert(trials.Any(t=>t.Aim.ForwardSpeed==30),"recorded 30u/s execution was not tested");
    Assert(trials.Any(t=>t.Aim.ForwardSpeed==30 && t.Start.Feet!=start.Feet && t.Aim.ForwardOffset<11),"velocity and release displacement tested independently only");
    var c=new Candidate(aim,new(Vector3.Zero,SimulationStatus.Rested,4,0,[]),0,12,15,ExecutionHits:1,ExecutionTrials:6);
    Near(c.Reliability,1f/6);
    var steady=c with {Aim=new(-23,0,ThrowMode.Left),RobustHits=8,RobustTrials=9,ExecutionHits=0,ExecutionTrials=0};
    Assert(SceneAdvisor.Order([c,steady],null,true)[0]==steady,"aim trials hid fragile execution");
});
Test("Area ranking preserves interior clearance before view convenience",()=>
{
    var t=SmokeTargets.MirageVip;
    var aim=new Aim(-30,0,ThrowMode.Left);
    var edge=new Candidate(aim,new(new(-1122,-636,-166),SimulationStatus.Rested,4,0,[]),94,9,9);
    var inside=edge with {Aim=aim with {Yaw=20},Trajectory=edge.Trajectory with {Endpoint=new(-1200,-636,-166)},Error=16};
    Assert(SceneAdvisor.Order([edge,inside],aim,true,t)[0]==inside,"view turn displaced safer interior landing");
});
Test("Nearby recovery requires a continuously supported clear route",()=>
{
    var origin=new ThrowStart(Vector3.Zero);
    var target=new Target(new(2700,0,0),96,new(96,80,12));
    var safe=NearbyStart.Find(origin,target,p=>p,(a,b)=>true);
    Assert(safe is { } s && s.Feet==new Vector3(96,0,0),"wrong bounded recovery distance");
    Assert(NearbyStart.Find(origin,target,p=>p.X>40 && p.X<60 ? null : p,(a,b)=>true) is null,"crossed a floor gap");
    Assert(NearbyStart.Find(origin,target,p=>p,(a,b)=>b.X<48) is null,"crossed blocked player hull");
    Assert(NearbyStart.Find(origin,target,p=>p.X>=48 ? p+new Vector3(0,0,30) : p,(a,b)=>true) is null,"jumped to another level");
    Assert(NearbyStart.Find(origin,new(new(2700,0,0),32),p=>p,(a,b)=>true) is null,"silently moved an exact-point setup");
});
Test("Stable standing bank outranks a moving direct route without overriding reliability",()=>
{
    var bank=new Candidate(new(-20,0,ThrowMode.Left),new(new(100,0,2),SimulationStatus.Rested,4,2,[],1),2,9,9,ModelHits:8,ModelTrials:8);
    var moving=bank with {Aim=new(-10,0,ThrowMode.Left,250),Trajectory=bank.Trajectory with {WallBounces=0},ExecutionHits=6,ExecutionTrials=6};
    Assert(SceneAdvisor.Order([bank,moving],null,true)[0]==bank,"cleaner W route displaced easier stable standing bank");
    Assert(SceneAdvisor.Order([bank,moving with {ExecutionHits=1}],null,true)[0]==bank,"unreliable W throw preferred blindly");
    Assert(SceneAdvisor.Order([bank with {ModelHits=2},moving],null,true)[0]==moving,"ease displaced substantially better reliability");
    Near((bank with {ModelHits=2}).Reliability,.25f);
});
Test("Area searches compare W routes and stress source reconstruction at the same origin",()=>
{
    var world=new AnalyticWorld();
    var known=SmokeSimulation.Run(world,physics,start,new(-25,15,ThrowMode.Left));
    var target=SmokeTargets.Around(known.Endpoint);
    var origins=new List<Vector3>();
    using var search=new LineupSearch(world,physics,start,target,new SearchOptions {IncludeActions=true,MaxCandidates=1600},
        runUpPosition:a=>{origins.Add(start.Feet);return start.Feet-Geometry.Direction(0,a.Yaw)*100;});
    var phases=new HashSet<string>();while(search.Advance())phases.Add(search.Phase);
    Assert(phases.Contains("route-direct") && origins.Count>0,"stationary hit skipped W comparison");
    Assert(search.Result!.Matches.Count>0 && search.Result.Matches.All(c=>c.ModelTrials>=4 && c.SourceStressTrials==6 && c.Reliability>=.75f),"model uncertainty was not checked");
    Assert(TrajectorySensitivity.Cases(Vector3.Zero,new(0,0,ThrowMode.Left),physics,1).Any(c=>c.Source.X==16),"observed source displacement absent");
});
Test("Execution envelope covers accepted angle boundaries together with position error",()=>
{
    var aim=new Aim(-25,179.8f,ThrowMode.Left);
    var trials=ExecutionEnvelope.Perturbations(start,aim).ToArray();
    foreach(float dp in new[]{-.5f,.5f}) foreach(float dy in new[]{-.5f,.5f})
    {
        var trial=trials.First(t=>MathF.Abs(t.Aim.Pitch-aim.Pitch-dp)<.001f &&
            MathF.Abs(Geometry.NormalizeYaw(t.Aim.Yaw-aim.Yaw)-dy)<.001f && t.Start.Feet!=start.Feet);
        var observation=new ThrowObservation(trial.Start.Feet,64,trial.Aim,1,Vector3.Zero,true,10);
        Assert(Verification.Matches(start,aim,observation,10),"boundary tested by search is rejected by verification");
    }
    Assert(!Verification.Matches(start,aim,new(start.Feet,64,aim with {Pitch=aim.Pitch+.51f},1,Vector3.Zero,true,10),10),"angle outside envelope accepted");
});
Test("Fragile nominal hits cannot terminate an area search",()=>
{
    var world=new AnalyticWorld();
    var endpoint=SmokeSimulation.Run(world,physics,start,new(-25,0,ThrowMode.Left)).Endpoint;
    using var search=new LineupSearch(world,physics,start,new(endpoint,3,new Vector3(3,3,12)),new SearchOptions{MaxCandidates=1200});
    int firstReview=-1; bool continued=false;
    while(search.Advance())
    {
        if(search.Phase=="robustness" && firstReview<0) firstReview=search.Evaluated;
        if(firstReview>=0 && search.Evaluated>firstReview+20) continued=true;
    }
    Assert(firstReview>=0 && continued,"unreliable first hit prevented further exploration");
    Assert(search.Result!.Matches.All(c=>c.Reliability>=.75f),"unreliable result published");
    Assert(search.Result.Experimental is {Count:>0} && search.Result.Experimental.All(c=>c.Reliability<.75f && targetValid(c)),"fragile hits disappeared instead of remaining research evidence");
    bool targetValid(Candidate c)=>Vector3.Distance(c.Trajectory.Endpoint,endpoint)<15;
});
Test("All moving strengths receive opportunities during route round-robin",()=>
{
    using var search=new LineupSearch(new AnalyticWorld(),physics,start,new(new(2700,0,2),96,new Vector3(96,80,12)),
        new SearchOptions{IncludeActions=true,MaxCandidates=400},runUpPosition:a=>new Vector3(-100,0,0));
    while(search.Advance()){}
    var rows=search.Result!.Coverage!;
    foreach(var action in new[]{ThrowAction.Run,ThrowAction.Walk}) foreach(var mode in Enum.GetValues<ThrowMode>())
        Assert(rows.Any(r=>r.Action==action && r.Mode==mode && r.Attempted>=1 && r.Evaluated>0),"an action/strength was starved by jump search");
});
Test("Preparation waits for a stable view and restarts after a stance change",()=>
{
    var gate=new PreparationState();
    Assert(!gate.Observe(0,Vector3.Zero,59,true),"first frame was called ready");
    Assert(!gate.Observe(.2f,Vector3.Zero,64,true),"view-height transition was ignored");
    Assert(!gate.Observe(.4f,Vector3.Zero,64,true),"stability period too short");
    Assert(gate.Observe(.5f,Vector3.Zero,64,true),"stable player never became ready");
    Assert(!gate.Observe(.6f,new(1,0,0),64,true),"movement did not reset stability");
    Assert(!gate.Observe(1,new(1,0,0),64,false),"wrong posture reported ready");
    var drifting=new PreparationState();
    drifting.Observe(0,Vector3.Zero,59,true);
    drifting.Observe(.1f,Vector3.Zero,59.09f,true);
    drifting.Observe(.2f,Vector3.Zero,59.18f,true);
    Assert(!drifting.Observe(.3f,Vector3.Zero,59.27f,true),"cumulative eye drift was mistaken for stability");
});
Test("Moving cues anticipate the line and detect a skipped crossing",()=>
{
    var aim=new Aim(0,0,ThrowMode.Left,250);
    Assert(ReleaseGuidance.Stage(new(-100,0,0),Vector3.Zero,new(250,0,0),aim,true)==ReleaseStage.Approach,"missing advance cue");
    Assert(ReleaseGuidance.Stage(new(-30,0,0),Vector3.Zero,new(250,0,0),aim,true)==ReleaseStage.Soon,"missing near cue");
    Assert(ReleaseGuidance.Stage(new(15,0,0),Vector3.Zero,new(250,0,0),aim,true)==ReleaseStage.Passed,"crossing between updates lost");
    Assert(ReleaseGuidance.Stage(new(-30,20,0),Vector3.Zero,new(250,0,0),aim,true)==ReleaseStage.Correct,"off-axis run got a release cue");
});
Test("Source stress is reported separately and surface uncertainty includes floor bounces",()=>
{
    var c=new Candidate(new(0,0,ThrowMode.Left),new(Vector3.Zero,SimulationStatus.Rested,2,1,[],0),0,19,19,
        ModelHits:4,ModelTrials:4,SourceStressHits:1,SourceStressTrials:6);
    Near(c.Reliability,1);
    Assert(TrajectorySensitivity.SurfaceCases(Vector3.Zero,physics,c.Trajectory.Bounces).Count()==4,"floor contact escaped surface checks");
    Assert(SceneAdvisor.Explain(start,new(new(100,0,2)),c,[c]).Contains("出手模型敏感"),"source risk disappeared from advice");
});
Test("Publishing rechecks geometry after cached candidate validation",()=>
{
    var world=new ClosingWorld();
    var known=SmokeSimulation.Run(world,physics,start,new(-25,0,ThrowMode.Left));
    using var search=new LineupSearch(world,physics,start,SmokeTargets.Around(known.Endpoint));
    bool closed=false;
    while(search.Advance()) if(search.Phase=="publish-review") {world.Closed=true; closed=true;}
    Assert(closed && search.Result!.Matches.Count==0,"published a candidate through changed geometry");
});
Test("Incremental run-up filtering can be interrupted between hull queries",()=>
{
    int units=0;
    IEnumerable<Vector3?> Runup(Aim aim)
    {
        for(int i=0;i<20;i++) {units++;yield return null;}
        yield return new Vector3(-100,0,0);
    }
    using var search=new LineupSearch(new AnalyticWorld(),physics,start,SmokeTargets.Around(new(1000,0,2)),
        new SearchOptions{IncludeActions=true,MaxCandidates=60},runUpSteps:Runup);
    while(search.Advance())
    {
        int before=units;
        if(!search.Advance()) break;
        Assert(units-before<=1,"multiple hull query steps consumed in one advance");
    }
    Assert(units>=20 && search.Result!.Coverage!.Any(c=>c.Action==ThrowAction.Run && c.Evaluated>0),"incremental filter never allowed a route");
});
Test("Candidate diagnostics reconcile all execution, surface and source trials",()=>
{
    var world=new AnalyticWorld();
    var known=SmokeSimulation.Run(world,physics,start,new(-25,15,ThrowMode.Left));
    using var search=new LineupSearch(world,physics,start,SmokeTargets.Around(known.Endpoint),new SearchOptions{IncludeActions=true,MaxCandidates=900});
    while(search.Advance()){}
    var candidates=search.Result!.Matches.Concat(search.Result.Experimental ?? []).ToArray();
    Assert(candidates.Length>0,"no reviewed candidates");
    foreach(var candidate in candidates)
    {
        var checks=candidate.Checks!;
        var execution=checks.Where(c=>c.Group is not ("surface" or "source")).ToArray();
        Assert(execution.Sum(c=>c.Trials)==candidate.RobustTrials-1 && execution.Sum(c=>c.Hits)==candidate.RobustHits-1,"execution accounting lost/duplicated trials");
        Assert(checks.Single(c=>c.Group=="surface").Trials==candidate.ModelTrials && checks.Single(c=>c.Group=="source").Trials==candidate.SourceStressTrials,"model and source checks conflated");
        Assert(checks.Single(c=>c.Group=="angle").Trials==8 && checks.Single(c=>c.Group=="coupled").Trials==4,"angle and coupled cases were not separated");
        var decoded=JsonSerializer.Deserialize(JsonSerializer.Serialize(candidate,NadeLabJsonContext.Default.Candidate),NadeLabJsonContext.Default.Candidate)!;
        Assert(decoded.Checks!.SequenceEqual(checks),"diagnostics lost in generated serialization");
    }
});
Test("Weak-category advice identifies motion and surface failures independently",()=>
{
    var checks=new RobustChecks(); checks.Observe("angle",true,0);checks.Observe("motion-timing",false,80);checks.Observe("surface",false,120);
    var candidate=new Candidate(new(0,0,ThrowMode.Left),new(Vector3.Zero,SimulationStatus.Rested,1,1,[]),0,1,3,Checks:checks.Results());
    string advice=RobustChecks.Explain(candidate);
    Assert(advice.Contains("速度与松键") && advice.Contains("反弹参数") && !advice.Contains("角度"),"generic advice blamed a passing category");
});
Test("Actual throw feedback distinguishes eye and velocity errors without changing matching",()=>
{
    var aim=new Aim(-25,0,ThrowMode.Left,130,ThrowAction.Walk);
    var expected=new ThrowStart(Vector3.Zero,60);
    var observed=new ThrowObservation(Vector3.Zero,64,aim,1,new(110,0,0),false,10,true,true);
    var issues=Verification.ExplainMismatch(expected,aim,observed,10);
    Assert(!Verification.Matches(expected,aim,observed,10) && issues.Any(i=>i.Contains("眼高")) && issues.Any(i=>i.Contains("速度")),"actual eye/speed deviation was hidden");
    Assert(!issues.Any(i=>i.Contains("角度")),"correct angle blamed");
    var corrected=observed with {EyeHeight=60,Velocity=new(130,0,0)};
    Assert(Verification.Matches(expected,aim,corrected,10) && Verification.ExplainMismatch(expected,aim,corrected,10).Count==0,"explanation disagrees with verification");
});
Test("Later-stage hits are reviewed before global exploration",()=>
{
    var world=new AnalyticWorld();
    var known=SmokeSimulation.Run(world,physics,start,new(-25,0,ThrowMode.Left));
    bool allow=false,coarse=false,globalBeforeReview=false,reviewed=false;
    using var search=new LineupSearch(world,physics,start,SmokeTargets.Around(known.Endpoint),
        new SearchOptions {IncludeActions=true,MaxCandidates=4000},rejectedByEvidence:_=>!allow);
    while(search.Advance())
    {
        if(search.Phase=="coarse") {allow=true;coarse=true;}
        if(search.ReviewedRecommendedCount>0) reviewed=true;
        if(search.Phase=="global" && !reviewed) globalBeforeReview=true;
    }
    Assert(coarse && reviewed && search.Result!.Matches.Count>0,"late-stage scenario never found a usable route");
    Assert(!globalBeforeReview,"continued broad exploration before reviewing a later-stage hit");
});
Test("Early result adoption still completes publication checks",()=>
{
    var world=new ClosingWorld();
    var known=SmokeSimulation.Run(world,physics,start,new(-25,0,ThrowMode.Left));
    using var search=new LineupSearch(world,physics,start,SmokeTargets.Around(known.Endpoint),new SearchOptions{IncludeActions=true,MaxCandidates=4000});
    bool requested=false,published=false;
    while(search.Advance())
    {
        if(!requested && search.ReviewedRecommendedCount>0) {search.RequestFinish();requested=true;}
        if(search.Phase=="publish-review") {world.Closed=true;published=true;}
    }
    Assert(requested && published,"early adoption did not reach final verification");
    Assert(search.Result!.Matches.Count==0,"early adoption bypassed changed geometry");
});
Test("Seeded repeat search rechecks hints and avoids the full scan",()=>
{
    var world=new AnalyticWorld();
    var aim=new Aim(-25,0,ThrowMode.Left);
    var known=SmokeSimulation.Run(world,physics,start,aim);
    using var search=new LineupSearch(world,physics,start,SmokeTargets.Around(known.Endpoint),
        new SearchOptions{IncludeActions=true,ResultCount=1,SeedAims=[aim],MaxCandidates=4000});
    while(search.Advance()){}
    var result=search.Result!;
    Assert(result.Evaluated==1 && result.Matches.Count==1,"seed was not reused efficiently");
    Assert(result.Matches[0].RobustTrials==19 && result.Matches[0].ModelTrials==4 && result.Matches[0].Reliability>=.75f,"hint skipped full checks");
    using var blocked=new LineupSearch(world,physics,start,SmokeTargets.Around(known.Endpoint),
        new SearchOptions{ResultCount=1,SeedAims=[aim],MaxCandidates=1},rejectedByEvidence:_=>true);
    while(blocked.Advance()){}
    Assert(blocked.Result!.Matches.Count==0,"historical hint bypassed failure evidence");
});
Test("Seeded search keeps the new origin and validates hint input",()=>
{
    var world=new AnalyticWorld();
    var aim=new Aim(-25,0,ThrowMode.Left);
    var relocated=new ThrowStart(new Vector3(40,20,0));
    var known=SmokeSimulation.Run(world,physics,relocated,aim);
    using var search=new LineupSearch(world,physics,relocated,SmokeTargets.Around(known.Endpoint),
        new SearchOptions{ResultCount=1,SeedAims=[aim],MaxCandidates=20});
    while(search.Advance()){}
    Assert(search.Result!.Matches.Count==1 && Vector3.Distance(search.Result.Matches[0].Trajectory.Endpoint,known.Endpoint)<.01f,"seed reused an old endpoint/origin");
    bool rejected=false;
    try {new SearchOptions{SeedAims=[aim with {ForwardOffset=float.NaN}]}.Validate();}
    catch(ArgumentException) {rejected=true;}
    Assert(rejected,"nonfinite hint accepted");
});
Test("Prior hints cannot bypass quick-search action restrictions",()=>
{
    var world=new AnalyticWorld();var target=SmokeTargets.Around(new(1000,0,2));
    var hints=new[]{new Aim(-25,0,ThrowMode.Left,100,ThrowAction.Step,ForwardOffset:10),
        new Aim(-25,0,ThrowMode.Left,100,ThrowAction.StepJump,25,220,10),
        new Aim(-25,0,ThrowMode.Left,0,ThrowAction.Jump,25,220)};
    foreach(bool actions in new[]{false,true})
    {
        using var search=new LineupSearch(world,physics,start,target,new SearchOptions{IncludeActions=actions,MaxCandidates=1,SeedAims=hints});
        while(search.Advance()){}
        Assert(search.Result!.Coverage!.All(c=>c.Action is not (ThrowAction.Step or ThrowAction.StepJump)),"wide-only action entered quick search");
        if(!actions) Assert(search.Result.Coverage!.All(c=>c.Action==ThrowAction.Standing),"disabled action entered via seed");
    }
});
Test("Prior jump angles use current jump physics even when profile ID is unchanged",()=>
{
    var updated=physics with {JumpImpulse=350,PlayerGravity=700};
    var old=new Aim(-25,0,ThrowMode.Left,0,ThrowAction.Jump,JumpLatch.Height(physics),JumpLatch.VerticalSpeed(physics));
    var expected=old with {ReleaseHeight=JumpLatch.Height(updated),VerticalSpeed=JumpLatch.VerticalSpeed(updated)};
    var world=new AnalyticWorld();var endpoint=SmokeSimulation.Run(world,updated,start,expected).Endpoint;
    using var search=new LineupSearch(world,updated,start,SmokeTargets.Around(endpoint),new SearchOptions{IncludeActions=true,MaxCandidates=1,SeedAims=[old]});
    while(search.Advance()){}
    var actual=search.Result!.Nearest.Single().Aim;
    Near(actual.ReleaseHeight,expected.ReleaseHeight);Near(actual.VerticalSpeed,expected.VerticalSpeed);
    Assert(actual.ReleaseHeight!=old.ReleaseHeight,"old latch survived a changed player gravity");
});
Test("Editable landing area widens horizontally without accepting the lower floor",()=>
{
    var target=new Target(new(10,20,100),96,new Vector3(96,80,12));
    var wider=TargetRegionEditor.Resize(target,1.25f);
    Assert(wider.Position==target.Position,"editing moved target center");
    Assert(!target.Contains(new(120,20,100)) && wider.Contains(new(120,20,100)),"width did not change acceptance");
    Assert(!wider.Contains(new(10,20,0)),"lower floor accepted");
    Near(wider.AreaHalfSize!.Value.Z,12);Near(wider.Radius,120);
    var maximum=TargetRegionEditor.Resize(wider,100,1000);
    Assert(maximum.AreaHalfSize==new Vector3(256,256,64),"editor bounds not enforced");
    var wall=new AnalyticWorld(0,new Box(new(50,-200,0),new(55,200,250)));
    Assert(!wider.Accepts(wall,new(120,20,100)),"widening accepted opposite side of a wall");
});
Test("Picked area edge preserves center and height and rejects another floor",()=>
{
    var target=new Target(new(10,20,100),96,new Vector3(96,80,12));
    var resized=TargetRegionEditor.WithEdge(target,new(110,20,100));
    Assert(resized.Position==target.Position && resized.AreaHalfSize==new Vector3(100,100,12),"edge changed height or center");
    bool rejected=false;
    try {TargetRegionEditor.WithEdge(target,new(110,20,0));} catch(ArgumentException) {rejected=true;}
    Assert(rejected,"cross-floor edge accepted");
    var outline=TargetRegionEditor.Outline(resized);
    Assert(Vector3.Distance(outline[0],outline[^1])<.001f && outline.All(p=>p.Z==102),"outline not closed/flat");
});
Test("Continuing a finished search retains visited angles and cumulative evaluations",()=>
{
    var seen=new HashSet<Aim>();bool duplicate=false;
    bool Filter(Aim aim) {if(!seen.Add(aim)) duplicate=true;return true;}
    var world=new EmptyWorld();
    using var search=new LineupSearch(world,physics with {MaxSeconds=1,MinDetonateSeconds=1},start,new(new(100000,0,0)),new SearchOptions {MaxCandidates=20},canRunUp:Filter);
    while(search.Advance()){}
    int first=search.Evaluated;
    search.ContinueExpanded(world,Filter);
    while(search.Advance()) {if(search.Evaluated>=first+500) search.RequestFinish();}
    Assert(first==20 && search.Evaluated>first && search.Continuations==1,"continuation reset progress");
    Assert(!duplicate,"re-simulated an already visited nominal angle");
    Assert(search.Result!.Matches.Count==0,"empty world manufactured a landing");
    search.Dispose();Assert(!search.CanContinue,"disposed search can resume");
});
Test("Retained recommendations must pass current action geometry after continuation",()=>
{
    var world=new AnalyticWorld();var endpoint=SmokeSimulation.Run(world,physics,start,new(-25,0,ThrowMode.Left)).Endpoint;
    using var search=new LineupSearch(world,physics,start,SmokeTargets.Around(endpoint),new SearchOptions {MaxCandidates=1000});
    while(search.Advance()){}
    Assert(search.Result!.Matches.Count>0,"fixture did not find a recommendation");
    search.ContinueExpanded(world,_=>false);
    while(search.Advance()){}
    Assert(search.Result!.Matches.Count==0,"old result survived blocked player geometry");
});
Test("Expanded area research keeps fragile hits separate from recommendations",()=>
{
    var world=new AnalyticWorld();var endpoint=SmokeSimulation.Run(world,physics,start,new(-25,0,ThrowMode.Left)).Endpoint;
    using var search=new LineupSearch(world,physics,start,new(endpoint,3,new Vector3(3,3,12)),new SearchOptions {MaxCandidates=1000,Exhaustive=true});
    while(search.Advance()){}
    Assert(search.Result!.Matches.All(c=>c.Reliability>=.75f),"expanded mode promoted a fragile candidate");
    Assert(search.Result.Experimental is {Count:>0},"lost experimental evidence");
});
Test("Release interval never bridges a failed timing sample and checks speed",()=>
{
    var w=ReleaseWindow.FromSamples([true,false,true,true,true],250,25)!;
    Near(w.EarlySeconds,0);Near(w.LateSeconds,.1f);
    Assert(ReleaseWindow.FromSamples([true,true,false,true,true],250,25) is null,"invalid nominal release got a window");
    var aim=new Aim(-20,0,ThrowMode.Left,250,ThrowAction.Run);
    Assert(ReleaseGuidance.Stage(new(10,0,0),Vector3.Zero,new(250,0,0),aim,true,w)==ReleaseStage.Window,"passing late window not represented");
    Assert(ReleaseGuidance.Stage(new(30,0,0),Vector3.Zero,new(250,0,0),aim,true,w)==ReleaseStage.Passed,"late throw stayed in window");
    Assert(ReleaseGuidance.Stage(new(10,0,0),Vector3.Zero,new(200,0,0),aim,true,w)==ReleaseStage.Correct,"wrong speed accepted");
});
Test("Route library survives serialization without mixing feedback and landing evidence",()=>
{
    var library=new RouteLibrary();var target=SmokeTargets.Around(new(100,0,2));var aim=new Aim(-25,0,ThrowMode.Left);
    library.Remember(start,target,aim,"world","physics",true,new("throw1",true,false,"sealed"));
    library.Remember(start,target,aim,"world","physics",true,new("throw1",true,false,"unknown"));
    library.Remember(start,target,aim,"world","physics",true,new("throw2",true,true));
    string json=JsonSerializer.Serialize(library.Snapshot().ToArray(),NadeLabJsonContext.Default.SavedRouteArray);
    var restored=new RouteLibrary();restored.Import(JsonSerializer.Deserialize(json,NadeLabJsonContext.Default.SavedRouteArray)!);
    var route=restored.Find(start,target,aim,"world","physics")!;
    Assert(route.Trials.Count==2 && route.Trials.Count(t=>t.Matched && t.LandingHit)==1,"duplicated feedback or invented real success");
    Assert(route.Trials.All(t=>t.CoverageVerdict=="unknown"),"withdrawn coverage survived");
    Assert(restored.Hints(start,target,null,"world","physics").Length==1,"lost persisted hint");
    Assert(restored.Hints(start,target,null,"changed-world","physics").Length==0 && restored.Hints(start,target,null,"world","changed-physics").Length==0,"mixed incompatible versions");
    Assert(restored.Hints(start with {Feet=new(1000,0,0)},target,null,"world","physics").Length==0,"reused distant origin as nearby");
});
Test("Failure explanation distinguishes fragility and height without claiming impossibility",()=>
{
    var target=SmokeTargets.MirageVip;
    var c=new Candidate(new(-20,0,ThrowMode.Left),new(target.Position,SimulationStatus.Rested,4,2,[]),0,2,10,Checks:[new("surface",1,4,100,50)]);
    var result=new SearchResult(physics.Id,false,100,true,[],[c],Experimental:[c]);
    Assert(SearchExplanation.For(result,target).Summary.Contains("反弹参数"),"lost surface sensitivity diagnosis");
    var low=c with {Trajectory=c.Trajectory with {Endpoint=target.Position-new Vector3(0,0,100)}};
    Assert(SearchExplanation.For(result with {Experimental=[],Nearest=[low]},target).Summary.Contains("高度"),"XY widening incorrectly suggested for another floor");
});
Test("Continuing search rechecks newly rejected failure evidence",()=>
{
    var world=new AnalyticWorld();var endpoint=SmokeSimulation.Run(world,physics,start,new(-25,0,ThrowMode.Left)).Endpoint;
    using var search=new LineupSearch(world,physics,start,SmokeTargets.Around(endpoint),new SearchOptions {MaxCandidates=1000});
    while(search.Advance()){}
    Assert(search.Result!.Matches.Count>0,"fixture did not produce a recommendation");
    search.ContinueExpanded(world,evidenceFilter:_=>true);
    while(search.Advance()) {if(search.Evaluated>=1200) search.RequestFinish();}
    Assert(search.Result!.Matches.Count==0 && search.Result.Experimental?.Count==0,"old candidate bypassed new failure evidence");
});
Test("Guided release accepts its sampled corridor without loosening strict calibration",()=>
{
    var aim=new Aim(-20,0,ThrowMode.Left,250,ThrowAction.Run);
    var c=new Candidate(aim,new(Vector3.Zero,SimulationStatus.Rested,3,0,[]),0,ReleaseWindow:new(.1f,.1f,225,275,.05f));
    foreach(var (offset,speed) in new[]{(20f,275f),(-20f,225f)})
    {
        var observed=new ThrowObservation(new(offset,0,0),64,aim,1,new(speed,0,0),false,10,true,true);
        Assert(!Verification.Matches(start,aim,observed,10),"widened strict model calibration");
        Assert(Verification.TryGuidedSetup(start,c,observed,10,out var shifted,out var adjusted),"advertised corridor rejected");
        Near(shifted.Feet.X,offset);Near(adjusted.ForwardSpeed,speed);
        Assert(!Verification.TryGuidedSetup(start,c,observed with {Feet=new(30,0,0)},10,out _,out _),"out-of-window offset accepted");
        Assert(!Verification.TryGuidedSetup(start,c,observed with {Feet=new(offset,12,0)},10,out _,out _),"sideways drift accepted");
        Assert(!Verification.TryGuidedSetup(start,c,observed with {Aim=aim with {Pitch=-19.4f}},10,out _,out _),"angle outside contract accepted");
    }
    var jump=ThrowCatalog.Families(physics,new SearchOptions {IncludeActions=true},2000,0).First(a=>a.EffectiveAction==ThrowAction.RunJump && a.Mode==ThrowMode.Left && a.ForwardSpeed==250) with {Pitch=-25};
    var moved=jump with {ForwardSpeed=275,ForwardOffset=jump.ForwardOffset+2.5f};
    var shiftedStart=start with {Feet=new(20,0,0)};
    var observation=new ThrowObservation(moved.ReleaseFeet(shiftedStart),64,jump,1,moved.PlayerVelocity,false,10,false,false,true);
    Assert(Verification.TryGuidedSetup(start,c with {Aim=jump},observation,10,out var js,out var ja),"moving jump corridor rejected");
    var launch=new SmokeSimulation(new EmptyWorld(),physics,js,ja);
    Assert(Verification.MatchesJumpLaunch(launch.Position,launch.Velocity,launch.Velocity,launch.Position,1f/64),"adjusted actual launch cannot validate");
    var nominal=new SmokeSimulation(new EmptyWorld(),physics,start,jump);
    Assert(!Verification.MatchesJumpLaunch(nominal.Position,nominal.Velocity,launch.Velocity,launch.Position,1f/64),"guided launch corrupted strict nominal check");
});
Test("Every displayed ground release boundary accounts for speed and button delay",()=>
{
    var aim=new Aim(-20,0,ThrowMode.Left,250,ThrowAction.Run);
    var window=new ReleaseWindow(.1f,.1f,225,275,.05f);
    var c=new Candidate(aim,new(Vector3.Zero,SimulationStatus.Rested,3,0,[]),0,ReleaseWindow:window);
    var zone=window.ButtonOffsets(aim,.1f);
    Near(zone.X,-45);Near(zone.Y,-2.5f);
    foreach(float speed in new[]{225f,250f,275f}) foreach(float buttonPosition in new[]{zone.X,zone.Y})
    {
        var observed=new ThrowObservation(new(buttonPosition+speed*.1f,0,0),64,aim,1,new(speed,0,0),false,10,true,true);
        Assert(Verification.TryGuidedSetup(start,c,observed,10,out _,out _),$"visible boundary rejected at {buttonPosition}/{speed}");
        Assert(ReleaseGuidance.Stage(new(buttonPosition,0,0),new(-25,0,0),new(speed,0,0),aim,true,window)==ReleaseStage.Window,"guidance disagrees with rendered zone");
    }
});
Test("Multiple landing regions use their own connectivity and exclusions win",()=>
{
    var target=new Target(new(-100,0,2),80,new Vector3(80,80,12),ExtraRegions:[new(new(100,0,2),new(80,80,24))],Exclusions:[new(new(110,0,2),new(8,8,12))]);
    target.Validate();var wall=new AnalyticWorld(0,new Box(new(-2,-200,0),new(2,200,150)));
    Assert(target.Accepts(wall,new(80,0,2)),"second region connected to wrong center");
    Assert(!target.Contains(new(0,0,2)),"gap between regions accepted");
    Assert(!target.Accepts(wall,new(110,0,2)),"exclusion did not win");
    Assert(target.MissDistance(new(110,0,2))>0 && target.InteriorMargin(new(110,0,2))<0,"excluded seed received a good score");
    Assert(target.Contains(new(110,0,20)),"outside exclusion height should use allowed height");
});
Test("Target drafts and route snapshots cannot mutate prior geometry or evidence",()=>
{
    var extras=new[]{new LandingRegion(new(120,0,2),new(40,40,12))};
    var target=new Target(new(0,0,2),80,new Vector3(80,80,24),ExtraRegions:extras);
    var snapshot=target.Snapshot();var edited=TargetRegionEditor.Edit(snapshot,1,t=>TargetRegionEditor.Resize(t,2));
    Assert(snapshot.ExtraRegions![0].HalfSize.X==40 && edited.ExtraRegions![0].HalfSize.X==80,"draft mutated committed target");
    var library=new RouteLibrary();var aim=new Aim(-25,0,ThrowMode.Left);
    library.Remember(start,target,aim,"map","physics",true,new("1",true,true,"sealed"));
    extras[0]=new(new(900,0,2),new(30,30,12));
    Assert(library.Find(start,snapshot,aim,"map","physics")?.Trials.Count==1,"library retained caller array");
    Assert(library.Find(start,edited,aim,"map","physics") is null,"different region inherited proof");
    var excluded=snapshot with {Exclusions=[new(new(10,0,2),new(10,10,12))]};
    Assert(library.Find(start,excluded,aim,"map","physics") is null,"different exclusion inherited proof");
    var json=JsonSerializer.Serialize(excluded,NadeLabJsonContext.Default.Target);
    Assert(excluded.SameDefinition(JsonSerializer.Deserialize(json,NadeLabJsonContext.Default.Target)),"target JSON lost geometry");
    var reordered=excluded with {Exclusions=[new(new(20,0,2),new(10,10,12)),new(new(10,0,2),new(10,10,12))]};
    Assert(reordered.SameDefinition(reordered with {Exclusions=reordered.Exclusions!.Reverse().ToArray()}),"region ordering changed evidence identity");
    var legacy=JsonSerializer.Deserialize("{\"Position\":{\"X\":0,\"Y\":0,\"Z\":2},\"Radius\":32}",NadeLabJsonContext.Default.Target);
    legacy.Validate();Assert(legacy.ExtraRegions is null && legacy.Objective is null && !legacy.IsArea,"legacy target changed meaning");
});
Test("Marked face validates corners and keeps viewing evidence scoped",()=>
{
    var face=SmokeObjective.FromCorners(new(100,-32,32),new(102,32,96),-Vector3.UnitX);
    Near(face.Center.X,100);Near(face.HalfWidth,32);Near(face.HalfHeight,32);
    face=face with {ObserverEye=new(0,0,64),ObserverView=new(0,179,ThrowMode.Left)};
    Assert(face.SameView(new(20,0,64),new(0,-179,ThrowMode.Left)),"wrapped viewing angle rejected");
    Assert(!face.SameView(new(40,0,64),new(0,179,ThrowMode.Left)),"other observing position conflated");
    bool rejected=false;try {SmokeObjective.FromCorners(new(100,-32,32),new(130,32,96),-Vector3.UnitX);}catch(ArgumentException){rejected=true;}
    Assert(rejected,"different planes accepted as one frame");
    var target=SmokeTargets.Around(new(100,0,2)) with {Objective=face};
    var library=new RouteLibrary();var aim=new Aim(-25,0,ThrowMode.Left);library.Remember(start,target,aim,"m","p",true);
    Assert(library.Find(start,target with {Objective=face with {ObserverEye=new(100,0,64)}},aim,"m","p") is null,"new observer inherited old evidence");
});
Test("Route lanes share a bounded early budget and retain independent review opportunities",()=>
{
    var world=new AnalyticWorld(0,new Box(new(-200,-300,0),new(-190,300,300)),new Box(new(-200,290,0),new(1400,300,300)));
    var area=new Target(new(900,0,2),96,new Vector3(96,96,12),Objective:new(new(600,0,100),-Vector3.UnitX,40,60));
    using var search=new LineupSearch(world,physics,start,area,new SearchOptions{IncludeActions=true,MaxCandidates=120});
    while(search.Advance()){}
    Assert(search.Evaluated<=120,"candidate budget exceeded");
    foreach(string name in new[]{"direct","high","opening","bank"})
        Assert(search.Result!.Routes!.Any(r=>r.Lane==name && r.Attempted>0 && r.Evaluated>0),"lane starved: "+name);
    Assert(search.Result!.Matches.All(c=>c.Reliability>=.75f && area.Accepts(world,c.Trajectory.Endpoint)),"route hint bypassed final checks");
});
Test("Secondary allowed region supplies its own search headings",()=>
{
    var world=new AnalyticWorld();var known=SmokeSimulation.Run(world,physics,start,new(-25,40,ThrowMode.Left));
    var target=new Target(known.Endpoint+new Vector3(0,-700,100),32,new Vector3(32,32,12),ExtraRegions:[new(known.Endpoint,new(96,96,12))]);
    using var search=new LineupSearch(world,physics,start,target,new SearchOptions{MaxCandidates=600});
    while(search.Advance()){}
    Assert(search.Result!.Matches.Any(c=>Vector3.Distance(c.Trajectory.Endpoint,known.Endpoint)<140),"secondary region never reached");
    Assert(search.Result.Matches.All(c=>target.Accepts(world,c.Trajectory.Endpoint)),"invalid union landing");
});
Test("Collision branch representatives preserve less accurate multi-contact jump routes",()=>
{
    var branches=new CollisionBranches();
    Candidate C(int id,int walls,float error)=>new(new(-27,0,ThrowMode.Left,Action:ThrowAction.Jump),new(new(error,0,2),SimulationStatus.Rested,3,walls+1,[],walls,id),error);
    branches.Observe(C(123,2,100),c=>c.Error);
    branches.Observe(C(123,3,110),c=>c.Error);
    for(int i=0;i<40;i++) branches.Observe(C(i,1,i+1),c=>c.Error);
    Assert(branches.Seeds(c=>c.Error).Any(c=>c.Trajectory.CollisionRoute==123),"closest one-contact seeds evicted multi-contact branch");
    branches.Observe(C(123,2,80),c=>c.Error);
    Assert(branches.Seeds(c=>c.Error).Single(c=>c.Trajectory.CollisionRoute==123 && c.Trajectory.WallBounces==2).Error==80,"same branch failed to improve");
    Assert(branches.Seeds(c=>c.Error).Any(c=>c.Trajectory.CollisionRoute==123 && c.Trajectory.WallBounces==3),"same early contacts merged a later third collision");
});
Test("Collision identity is stable with and without recorded polylines",()=>
{
    var world=new AnalyticWorld(0,new Box(new(100,-300,0),new(105,300,200)),new Box(new(-90,-300,0),new(-85,300,200)));
    var aim=new Aim(-10,0,ThrowMode.Left);var full=SmokeSimulation.Run(world,physics,start,aim);
    var sim=new SmokeSimulation(world,physics,start,aim,false);while(!sim.Done)sim.Step();var lean=sim.Result();
    Assert(full.WallBounces>=2 && full.CollisionRoute!=0,"fixture did not produce multiple wall contacts");
    Assert(full.CollisionRoute==lean.CollisionRoute && full.Endpoint==lean.Endpoint,"route metadata changed simulation");
});
Test("Stationary jump-bank scan advances to fresh angles on continuation",()=>
{
    var seen=new HashSet<Aim>();
    using var search=new LineupSearch(new AnalyticWorld(),physics,start,new(new(2700,0,2),96,new Vector3(96,80,12)),new SearchOptions{IncludeActions=true,MaxCandidates=160},canRunUp:a=>{seen.Add(a);return true;});
    while(search.Advance()){}
    Assert(search.Result!.Routes!.Any(r=>r.Lane=="jump-bank" && r.Evaluated>0),"dedicated jump lane not sampled");
    int before=seen.Count(a=>a.EffectiveAction==ThrowAction.Jump);
    search.ContinueExpanded(new AnalyticWorld(),a=>{seen.Add(a);return true;});
    while(search.Advance()){if(search.Evaluated>=320) search.RequestFinish();}
    Assert(seen.Count(a=>a.EffectiveAction==ThrowAction.Jump)>before+10,"continuation repeated old jump prefix");
    Assert(seen.Any(a=>a.EffectiveAction==ThrowAction.Jump && MathF.Abs(a.Yaw)>=24 && a.Pitch>=-48),"wide moderate-angle banks postponed behind dense local grid");
    Assert(search.Result!.Matches.All(c=>c.Reliability>=.75f),"multi-contact research bypassed stability gate");
});
Test("Known angle outcomes feed refinement without counting another simulation",()=>
{
    var world=new AnalyticWorld();var known=SmokeSimulation.Run(world,physics,start,new(-42.4f,-31.2f,ThrowMode.Left));
    using var search=new LineupSearch(world,physics,start,new(known.Endpoint,32,new Vector3(32,32,8)),new SearchOptions{IncludeActions=true,MaxCandidates=1200});
    while(search.Advance()){}
    var result=search.Result!;
    Assert(result.Efficiency is {CachedResults:>0,RefinementMoves:>0},"optimizer did not reuse known outcomes and recenter");
    Assert(result.Coverage!.Sum(c=>c.Evaluated)==result.Evaluated,"cached proposals charged as new physics");
    Assert(SearchPresentation.HasUsable(result) && result.Matches.All(SearchQuality.Recommended),"refinement failed reachable narrow target or promoted weak evidence");
    var closed=new ClosingWorld {Closed=true};search.ContinueExpanded(closed);
    while(search.Advance()){if(search.Evaluated>result.Evaluated+20)search.RequestFinish();}
    Assert(search.Result!.Matches.Count==0,"cached endpoints published through a closed world");
});
Test("Player clearance cache shares stationary jump checks but separates actual movement and stance",()=>
{
    var jump=new Aim(-25,0,ThrowMode.Left,Action:ThrowAction.Jump,ReleaseHeight:26,VerticalSpeed:219);
    Assert(ActionSpaceKey.For(jump)==ActionSpaceKey.For(jump with {Pitch=-65,Yaw=170,Mode=ThrowMode.Right}),"stationary jump incorrectly depends on view/strength");
    var moving=jump with {Action=ThrowAction.RunJump,ForwardOffset=26,ForwardSpeed=250};
    Assert(ActionSpaceKey.For(moving)!=ActionSpaceKey.For(moving with {Yaw=90}),"different player sweeps share clearance");
    Assert(ActionSpaceKey.For(jump)!=ActionSpaceKey.For(jump with {ReleaseHeight=30}),"different head clearance shared");
    Assert(ActionSpaceKey.For(new(0,0,ThrowMode.Left))!=ActionSpaceKey.For(new(0,0,ThrowMode.Left,Action:ThrowAction.Crouch)),"crouch hull reused for standing");
});
Test("Continuation refreshes retained refinement endpoints under its new collision world",()=>
{
    var world=new AnalyticWorld();var known=SmokeSimulation.Run(world,physics,start,new(-42.4f,-31.2f,ThrowMode.Left));
    var oldAngles=new HashSet<Aim>();
    using var search=new LineupSearch(world,physics,start,new(known.Endpoint,32,new Vector3(32,32,16)),new SearchOptions{IncludeActions=true,MaxCandidates=180},canRunUp:a=>{oldAngles.Add(a);return true;});
    while(search.Advance()){}
    int prior=search.Evaluated,refreshed=0;
    search.ContinueExpanded(new AnalyticWorld(10),a=>{if(oldAngles.Contains(a))refreshed++;return true;});
    while(search.Advance())if(search.Evaluated>=prior+1800)search.RequestFinish();
    Assert(search.Result!.Efficiency!.RevalidatedAngles>0 && refreshed>0,"old visited seeds were never recomputed");
    Assert(search.Result.Matches.All(c=>c.Trajectory.Endpoint.Z>=12),"old floor endpoint survived world transition");
});
Console.WriteLine($"{passed} passed; {failed} failed.");
return failed==0 ? 0 : 1;

sealed class EmptyWorld : ICollisionWorld
{ public SweepHit Sweep(Vector3 start,Vector3 end,float halfSize) => SweepHit.Miss(end); }
sealed class ClosingWorld : ICollisionWorld
{
    public bool Closed;
    private readonly AnalyticWorld inner=new();
    public SweepHit Sweep(Vector3 start,Vector3 end,float halfSize) => Closed ? new(0,start,Vector3.Zero,true) : inner.Sweep(start,end,halfSize);
}
sealed class CountingWorld(ICollisionWorld inner) : ICollisionWorld
{
    public int Traces;
    public SweepHit Sweep(Vector3 start,Vector3 end,float halfSize) { Traces++; return inner.Sweep(start,end,halfSize); }
}
