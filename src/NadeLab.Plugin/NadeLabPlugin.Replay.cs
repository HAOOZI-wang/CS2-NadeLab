using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private bool ReplayWorldMatches(JsonElement record)
    {
        using var current=JsonDocument.Parse(worldVersion?.ToJsonString() ?? "null");
        return record.TryGetProperty("Map",out var map) && record.TryGetProperty("WorldVersion",out var world) &&
            WorldIdentity.Matches(map.GetString(),world,CounterStrikeSharp.API.Server.MapName,current.RootElement);
    }
    private void ReplayCommand(CCSPlayerController? p,CommandInfo command)
    {
        if (p is not null || !Practice || !BenchmarkIdle || CounterStrikeSharp.API.Utilities.GetPlayers().Any(player=>!player.IsBot))
        {Say(p,"回放仅允许训练服控制台在无人且无其他训练任务时执行。"); return;}
        bool throws=command.ArgCount>1 && command.ArgByIndex(1)=="throws";
        string file=Path.Combine(DataPath,throws ? "throws.jsonl" : "searches.jsonl");
        if (!File.Exists(file)) {Say(null,"没有可回放的记录。"); return;}
        try
        {
            var lines=File.ReadLines(file).Where(line=>!string.IsNullOrWhiteSpace(line)).TakeLast(throws ? 20 : 1).ToArray();
            if (throws)
            {
                var comparisons=new JsonArray();
                foreach (string line in lines)
                {
                    using var doc=JsonDocument.Parse(line); var root=doc.RootElement;
                    if(!ReplayWorldMatches(root)) continue;
                    if(root.TryGetProperty("ObservationOnly",out var observation) && observation.GetBoolean()) continue;
                    var start=root.GetProperty("Start").Deserialize<ThrowStart>(Json);
                    var predicted=root.GetProperty("Predicted");
                    var aim=predicted.GetProperty("Aim").Deserialize<Aim>(Json);
                    if(aim.Airborne) aim=aim with {ReleaseHeight=JumpLatch.Height(physics),VerticalSpeed=JumpLatch.VerticalSpeed(physics),ForwardOffset=aim.ForwardSpeed*JumpLatch.Seconds};
                    var actual=root.GetProperty("Actual").Deserialize<Vector3>(Json);
                    var trajectory=SmokeSimulation.Run(new NativeWorld(null),physics,start,aim);
                    float error=Vector3.Distance(actual,trajectory.Endpoint);
                    var position=root.GetProperty("FirstObservedPosition").Deserialize<Vector3>(Json);
                    var velocity=root.GetProperty("FirstObservedVelocity").Deserialize<Vector3>(Json);
                    var observed=root.GetProperty("ActualThrow").Deserialize<ThrowObservation>(Json)!;
                    var launchStart=new ThrowStart(observed.Feet,observed.EyeHeight);
                    var launchAim=aim.Airborne ? aim : observed.Aim;
                    var expectedLaunch=new SmokeSimulation(new NativeWorld(null),physics,start,aim,recordPoints:false);
                    var actualFeetLaunch=new SmokeSimulation(new NativeWorld(null),physics,launchStart,launchAim,recordPoints:false);
                    Vector3 reconstructed=position-velocity/64-new Vector3(0,0,physics.Gravity/(2*64*64));
                    int modelHits=0,modelTrials=0;
                    foreach(var (source,variant) in TrajectorySensitivity.Cases(expectedLaunch.Position,aim,physics,trajectory.WallBounces))
                    {
                        var sim=new SmokeSimulation(new NativeWorld(null),variant,source,expectedLaunch.Velocity,recordPoints:false);
                        while(!sim.Done)sim.Step();
                        modelTrials++;
                        var recordedTarget=root.GetProperty("Target").Deserialize<Target>(Json);
                        if(sim.Status==SimulationStatus.Rested && recordedTarget.Accepts(new NativeWorld(null),sim.Position)) modelHits++;
                    }
                    float ReplayLaunch(float step)
                    {
                        var sim=new SmokeSimulation(new NativeWorld(null),physics with {StepSeconds=step},position,velocity,recordPoints:false);
                        while(!sim.Done) sim.Step();
                        return Vector3.Distance(sim.Position,actual);
                    }
                    comparisons.Add(new JsonObject { ["Utc"]=root.GetProperty("Utc").GetString(),["Matched"]=root.GetProperty("Matched").GetBoolean(),
                        ["Aim"]=JsonSerializer.SerializeToNode(aim,NadeLabJsonContext.Default.Aim),["OldError"]=root.GetProperty("ModelError").GetSingle(),["NewError"]=error,
                        ["FromObservedLaunchError"]=ReplayLaunch(1f/64),["HalfStepLaunchError"]=ReplayLaunch(1f/128),
                        ["ActualFeetEyeError"]=aim.Airborne ? null : Vector3.Distance(SmokeSimulation.Run(new NativeWorld(null),physics,launchStart,launchAim).Endpoint,actual),
                        ["ModelHits"]=modelHits,["ModelTrials"]=modelTrials,
                        ["PredictedSource"]=JsonSerializer.SerializeToNode(expectedLaunch.Position,NadeLabJsonContext.Default.Vector3),
                        ["ActualFeetSource"]=JsonSerializer.SerializeToNode(actualFeetLaunch.Position,NadeLabJsonContext.Default.Vector3),
                        ["Eye64Source"]=JsonSerializer.SerializeToNode(new SmokeSimulation(new NativeWorld(null),physics,launchStart with {EyeHeight=64},launchAim,recordPoints:false).Position,NadeLabJsonContext.Default.Vector3),
                        ["ObservedSourceEstimate"]=JsonSerializer.SerializeToNode(reconstructed,NadeLabJsonContext.Default.Vector3),
                        ["FixedEye64Error"]=Vector3.Distance(SmokeSimulation.Run(new NativeWorld(null),physics,start with {EyeHeight=64,Feet=start.Feet-new Vector3(0,0,.5f)},aim).Endpoint,actual),
                        ["Status"]=trajectory.Status.ToString(),["Endpoint"]=JsonSerializer.SerializeToNode(trajectory.Endpoint,NadeLabJsonContext.Default.Vector3) });
                    Say(null,$"REPLAY throw: old error {root.GetProperty("ModelError").GetSingle():F1}u / new error {error:F1}u / {trajectory.Status}");
                }
                telemetry!.Write(Path.Combine(DataPath,"replay-throws.json"),new JsonObject { ["Physics"]=JsonSerializer.SerializeToNode(physics,NadeLabJsonContext.Default.PhysicsProfile),
                    ["Comparisons"]=comparisons }.ToJsonString(Json),false);
                return;
            }
            string requested=command.ArgCount>1 ? command.ArgByIndex(1) : "last";
            if((requested.StartsWith("vip") || requested.EndsWith("-region")) && CounterStrikeSharp.API.Server.MapName!="de_mirage")
                throw new InvalidOperationException("VIP replay presets require Mirage");
            if(requested.StartsWith("recent") && int.TryParse(requested.Split('-')[0].AsSpan(6),out int recentNumber))
            {
                using var recent=JsonDocument.Parse(File.ReadAllText(Path.Combine(DataPath,"replay-recent.json")));
                lines=[recent.RootElement.GetProperty("Cases")[recentNumber-1].GetRawText()];
            }
            if(requested.StartsWith("vip"))
            {
                using var vipCases=JsonDocument.Parse(File.ReadAllText(Path.Combine(DataPath,"replay-smoke-area.json")));
                lines=[vipCases.RootElement.GetProperty("Cases")[requested.Contains("underpass") ? 1 : 0].GetRawText()];
            }
            if(requested.StartsWith("case") && int.TryParse(requested.Split('-')[0].AsSpan(4),out int caseNumber))
            {
                string casesFile=Path.Combine(DataPath,"replay-cases.json");
                if(!File.Exists(casesFile)) {Say(null,"没有失败回放快照。"); return;}
                using var cases=JsonDocument.Parse(File.ReadAllText(casesFile));
                var array=cases.RootElement.GetProperty("Cases");
                if(caseNumber<1 || caseNumber>array.GetArrayLength()) {Say(null,"没有该编号的失败记录。"); return;}
                lines=[array[caseNumber-1].GetRawText()];
            }
            using var searchDoc=JsonDocument.Parse(lines[^1]);
            var recorded=searchDoc.RootElement;
            if(!ReplayWorldMatches(recorded)) throw new InvalidOperationException("Recorded map/build differs from the running world");
            var benchmark=ReadBenchmarkIdentity(recorded);
            if(benchmark.Run is not null && !System.Text.RegularExpressions.Regex.IsMatch(requested,@"\Arecent[1-9][0-9]*-(?:cold-)?auto\z"))
                throw new InvalidOperationException("Benchmark requires a fixed-origin recent replay");
            if(benchmark.Pass=="cold" && !requested.Contains("-cold") || benchmark.Pass=="warm" && requested.Contains("-cold"))
                throw new InvalidOperationException("Benchmark pass differs from requested search mode");
            var session=new Session {Start=recorded.GetProperty("Start").Deserialize<ThrowStart>(Json),Target=recorded.GetProperty("Target").Deserialize<Target>(Json)};
            if(recorded.TryGetProperty("Kind",out var recordedKind) && Enum.TryParse<GrenadeKind>(recordedKind.GetString(),out var kind)) session.Kind=kind;
            if(recorded.TryGetProperty("Physics",out var recordedPhysics) && recordedPhysics.Deserialize<PhysicsProfile>(Json)!.Kind!=session.Kind) throw new InvalidOperationException("Recorded grenade/profile type mismatch");
            var replayPhysics=ProfileFor(session);
            session.SearchPhysics=replayPhysics;
            session.RequestedStart=session.Start;
            if(recorded.TryGetProperty("PreferredView",out var preferred) && preferred.ValueKind==JsonValueKind.Object) session.ReferenceView=preferred.Deserialize<Aim>(Json);
            var world=new NativeWorld(null);
            string mode=command.ArgCount>1 ? command.ArgByIndex(1) : "last";
            if(mode.EndsWith("-pose"))
            {
                if(CounterStrikeSharp.API.Utilities.GetPlayers().Any(player=>!player.IsBot))
                {Say(null,"玩家在线时不执行姿态诊断。");return;}
                var aim=recorded.GetProperty("DiagnosticAim").Deserialize<Aim>(Json);
                var start=session.Start.Value;
                var options=new CounterStrikeSharp.API.Modules.Utils.TraceOptions
                {InteractsAs=CounterStrikeSharp.API.Modules.Utils.Contents.Player,
                    InteractsWith=CounterStrikeSharp.API.Modules.Utils.Masks.PlayerSolid|CounterStrikeSharp.API.Modules.Utils.Contents.PhysicsProp};
                var tests=new JsonArray();
                foreach(var feet in ExecutionEnvelope.Cases(start,aim).Select(t=>t.Start.Feet).Prepend(start.Feet).Distinct())
                {
                    var from=feet+new Vector3(0,0,.5f);
                    var body=CounterStrikeSharp.API.Modules.Utils.Trace.TraceHullShape(NativeWorld.ToNative(from),NativeWorld.ToNative(from),new(-16,-16,0),new(16,16,72),null,options);
                    var floor=CounterStrikeSharp.API.Modules.Utils.Trace.TraceHullShape(NativeWorld.ToNative(from),NativeWorld.ToNative(from-new Vector3(0,0,2)),new(-16,-16,0),new(16,16,72),null,options);
                    bool clear=!body.IsAllSolid && !body.DidHit();
                    bool supported=floor.DidHit() && !floor.IsAllSolid && floor.Normal.Z>=.7f;
                    bool motionClear=true;
                    if(aim.Airborne)
                    {
                        var end=ThrowExecution.Resolve(start with {Feet=feet},aim,replayPhysics).ReleaseFeet+new Vector3(0,0,.5f);
                        var motion=CounterStrikeSharp.API.Modules.Utils.Trace.TraceHullShape(NativeWorld.ToNative(from),NativeWorld.ToNative(end),new(-16,-16,0),new(16,16,72),null,options);
                        motionClear=!motion.IsAllSolid && !motion.DidHit();
                    }
                    tests.Add(new JsonObject { ["Feet"]=JsonSerializer.SerializeToNode(feet,NadeLabJsonContext.Default.Vector3),
                        ["Nominal"]=feet==start.Feet,["BodyClear"]=clear,["Supported"]=supported,["MotionClear"]=motionClear,
                        ["Executable"]=clear && supported && motionClear });
                }
                var data=new JsonObject { ["Start"]=JsonSerializer.SerializeToNode(start,NadeLabJsonContext.Default.ThrowStart),
                    ["Aim"]=JsonSerializer.SerializeToNode(aim,NadeLabJsonContext.Default.Aim),["Cases"]=tests,
                    ["Scope"]="Conservative standing player hull and support check; diagnostic only, scores unchanged." };
                telemetry!.Write(Path.Combine(DataPath,"replay-pose.json"),data.ToJsonString(Json),false);
                Say(null,$"POSE diagnostic saved: {tests.Count} distinct origins; search scores unchanged.");
                return;
            }
            if(mode=="geometry" || mode.EndsWith("-geometry"))
            {
                var feet=session.Start.Value.Feet;
                float yaw=MathF.Atan2(session.Target.Value.Position.Y-feet.Y,session.Target.Value.Position.X-feet.X)*180/MathF.PI;
                var heading=Geometry.Direction(0,yaw);
                var options=new CounterStrikeSharp.API.Modules.Utils.TraceOptions {InteractsAs=CounterStrikeSharp.API.Modules.Utils.Contents.Player,
                    InteractsWith=CounterStrikeSharp.API.Modules.Utils.Masks.PlayerSolid|CounterStrikeSharp.API.Modules.Utils.Contents.PhysicsProp};
                var target=session.Target.Value.Position;
                var support=world.Sweep(target+new Vector3(0,0,128),target-new Vector3(0,0,512),2);
                Say(null,$"TARGET support: z={support.Position.Z:F2}, hit={support.Hit}, normal Z={support.Normal.Z:F2}, requested Z={target.Z:F2}; start={feet}.");
                for(int distance=0;distance<=120;distance+=20)
                {
                    var pos=feet-heading*distance;
                    var floor=CounterStrikeSharp.API.Modules.Utils.Trace.TraceEndShape(NativeWorld.ToNative(pos+new Vector3(0,0,32)),NativeWorld.ToNative(pos-new Vector3(0,0,32)),null,options);
                    var occupancy=CounterStrikeSharp.API.Modules.Utils.Trace.TraceHullShape(NativeWorld.ToNative(pos+new Vector3(0,0,1)),NativeWorld.ToNative(pos+new Vector3(0,0,1)),new(-16,-16,0),new(16,16,72),null,options);
                    Say(null,$"RUNUP {distance}u: floor hit={floor.DidHit()}, z={floor.EndPos.Z:F2}, n={floor.Normal.Z:F2}; hull solid={occupancy.IsAllSolid}, fraction={occupancy.Fraction:F3}.");
                }
                return;
            }
            if(mode.StartsWith("vip") || mode.EndsWith("-region"))
            {
                bool area=!mode.EndsWith("point");
                session.Target=area ? SmokeTargets.MirageVip : new Target(session.Target.Value.Position,32);
                if(area)
                {
                    var t=session.Target.Value;
                    var support=world.Sweep(t.Position+new Vector3(0,0,48),t.Position-new Vector3(0,0,96),2);
                    if(!support.Hit || support.StartSolid || support.Normal.Z<.7f) throw new InvalidOperationException("VIP region anchor has no floor support");
                    session.Target=t with {Position=support.Position-new Vector3(0,0,2)};
                }
            }
            bool moving=mode is "moving" or "moving-free" || mode.StartsWith("vip") || mode.StartsWith("case") || mode.StartsWith("recent") || mode.EndsWith("-region");
            job=new(-1,0,0,world,new(world,replayPhysics,session.Start.Value,session.Target.Value,
                new SearchOptions {IncludeActions=moving,MaxCandidates=6000,AirForwardSpeed=AirForwardSpeed(),PreferredView=session.ReferenceView,SeedAims=requested.Contains("-cold") ? null : PriorAims(session.Start.Value,session.Target.Value,session.ReferenceView,replayPhysics,benchmark.Run)},canRunUp:moving ? ActionFilter(null,session.Start.Value) : null,
                runUpSteps:moving ? IncrementalRunUpFilter(null,session.Start.Value) : null,rejectedByEvidence:session.Kind==GrenadeKind.Smoke ? FailureFilter(session.Start.Value) : null,
                executionSpace:ExecutionSpaceFilter(null,replayPhysics)),20)
                {ConsoleSession=session,AutomaticExpansion=requested.EndsWith("-auto"),BenchmarkCaseId=benchmark.Case,BenchmarkRunId=benchmark.Run,BenchmarkPassId=benchmark.Pass};
            if(mode.Contains("nearby") && (job.AlternativeStart=NearbyAlternative(null,session.Start.Value,session.Target.Value)) is not null) job.TimeLimit=8;
            Say(null,"REPLAY search started; captured coordinates only, no player movement.");
        }
        catch (Exception ex) {Say(null,"REPLAY failed: "+ex.Message);}
    }
}
