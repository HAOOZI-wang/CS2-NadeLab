using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using CounterStrikeSharp.API;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private bool BenchmarkIdle=>routeDiagnosis is null && BenchmarkWorkIdle;
    private bool BenchmarkWorkIdle=>stopPreparationContinuationCheck is null && job is null && flashJob is null && !flashSearchCheckPending && !flashVerificationCheckPending && !DummyLayoutLoadActive && !dummyCheckPending && !panelCheckPending && placementCheck is null &&
        effectStressCheck is null && recentImpactCheck is null && !creatingDummy && !dummyRemoving &&
        flights.Count==0 && grenadeFlights.Count==0 && infernos.Count==0 && measurementPawn is null &&
        grenadeBodies.Count==0 && effectDummies.Count==0 && sessions.Values.All(s=>s.Placement is null && s.Pending is null && s.Arm is null);

    private void RegisterBenchmark()
    {
        RegisterRouteDiagnosis();
        AddCommand("css_nl_benchmarkstatus","Read-only native benchmark protocol state",(caller,command)=>
        {
            if(caller is not null) return;
            Say(null,"BENCHMARKSTATUS "+new JsonObject {
                ["SchemaVersion"]=1,["Map"]=Server.MapName,["WorldVersion"]=worldVersion?.DeepClone(),
                ["Practice"]=Practice,["Humans"]=Utilities.GetPlayers().Count(p=>!p.IsBot),["Idle"]=BenchmarkIdle,["Epoch"]=grenadeEpoch,
                ["BenchmarkRunId"]=job?.BenchmarkRunId,["BenchmarkCaseId"]=job?.BenchmarkCaseId,["BenchmarkPassId"]=job?.BenchmarkPassId,
                ["BenchmarkCacheIsolated"]=true,
                ["RouteDiagnosisRequestId"]=routeDiagnosis?.RequestId,
                ["CoreBuild"]=typeof(LineupSearch).Assembly.ManifestModule.ModuleVersionId.ToString(),
                ["PluginBuild"]=typeof(NadeLabPlugin).Assembly.ManifestModule.ModuleVersionId.ToString()
            }.ToJsonString());
        });
        AddCommand("css_nl_benchmarkcancel","Cancel only the named console benchmark run",(caller,command)=>
        {
            if(caller is not null || command.ArgCount!=2 || job?.ConsoleSession is null || job.BenchmarkRunId is null ||
                job.BenchmarkRunId!=command.GetArg(1)) {Say(null,"BENCHMARK cancel refused: no matching console run.");return;}
            CancelSearch();Say(null,"BENCHMARK cancelled.");
        });
    }

    private sealed class RouteDiagnosisRun
    {
        internal required string RequestId,Map,WorldKey;
        internal required PhysicsProfile Physics;
        internal required KnownRouteEvaluation Evaluation;
        internal required NativeWorld World;
        internal required JsonObject Report;
        internal required List<RouteEvidence> Evidence;
        internal int Epoch;
        internal readonly System.Diagnostics.Stopwatch Clock=System.Diagnostics.Stopwatch.StartNew();
    }
    private RouteDiagnosisRun? routeDiagnosis;
    private static string RouteToken(string? value)
    {
        if(string.IsNullOrEmpty(value) || value.Length>80 || value.Any(c=>!char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new InvalidOperationException("Invalid route diagnostic identity");
        return value;
    }
    private void RegisterRouteDiagnosis()
    {
        AddCommand("css_nl_routediagnose","Console-only bounded known-angle native route diagnostic",(caller,command)=>
        {
            if(caller is not null) return;
            string? requestId=null;
            try
            {
                if(command.ArgCount!=2) throw new InvalidOperationException("Expected one request ID");
                requestId=RouteToken(command.GetArg(1));
                if(!loaded || !Practice || !BenchmarkIdle || pendingMapChange is not null || Utilities.GetPlayers().Any(p=>!p.IsBot))
                    throw new InvalidOperationException("Requires an empty idle practice server");
                if(File.Exists(Path.Combine(DataPath,"route-diagnosis-"+requestId+".json")))
                    throw new InvalidOperationException("Diagnostic request already has a report; use a fresh ID");
                using var stream=File.OpenRead(Path.Combine(DataPath,"route-diagnosis-input.json"));
                if(stream.Length is <2 or >131072) throw new InvalidOperationException("Diagnostic input exceeds bounded size");
                byte[] bytes=new byte[(int)stream.Length];stream.ReadExactly(bytes);
                using var document=JsonDocument.Parse(bytes);var input=document.RootElement;
                if(input.GetProperty("SchemaVersion").GetInt32()!=1 || RouteToken(input.GetProperty("RequestId").GetString())!=requestId)
                    throw new InvalidOperationException("Diagnostic schema/request mismatch");
                string caseId=RouteToken(input.GetProperty("CaseId").GetString());
                if(!ReplayWorldMatches(input)) throw new InvalidOperationException("Diagnostic map/build differs from current world");
                string coreBuild=typeof(LineupSearch).Assembly.ManifestModule.ModuleVersionId.ToString();
                string pluginBuild=typeof(NadeLabPlugin).Assembly.ManifestModule.ModuleVersionId.ToString();
                foreach(var pair in new[]{("CoreBuild",coreBuild),("PluginBuild",pluginBuild)})
                    if(input.TryGetProperty(pair.Item1,out var expected) && expected.GetString()!=pair.Item2)
                        throw new InvalidOperationException("Diagnostic binary build mismatch");
                var profile=input.GetProperty("Physics").Deserialize<PhysicsProfile>(Json) ?? throw new InvalidOperationException("Missing diagnostic physics");
                foreach(var property in JsonSerializer.SerializeToElement(profile,NadeLabJsonContext.Default.PhysicsProfile).EnumerateObject())
                    if(!input.GetProperty("Physics").TryGetProperty(property.Name,out _)) throw new InvalidOperationException("Incomplete diagnostic physics");
                profile.Validate();
                if(profile!=ProfileFor(new Session {Kind=profile.Kind})) throw new InvalidOperationException("Diagnostic physics differs from current profile");
                // Struct deserialization otherwise silently supplies zero coordinates/angles.
                foreach(var coordinate in new[]{"X","Y","Z"})
                {
                    _=input.GetProperty("Start").GetProperty("Feet").GetProperty(coordinate).GetSingle();
                    _=input.GetProperty("Target").GetProperty("Position").GetProperty(coordinate).GetSingle();
                }
                foreach(var property in new[]{"Pitch","Yaw","Mode","ForwardSpeed","Action","ReleaseHeight","VerticalSpeed","ForwardOffset"})
                    _=input.GetProperty("Aim").GetProperty(property).GetSingle();
                _=input.GetProperty("Start").GetProperty("EyeHeight").GetSingle();
                _=input.GetProperty("Target").GetProperty("Radius").GetSingle();
                var start=input.GetProperty("Start").Deserialize<ThrowStart>(Json);
                var aim=input.GetProperty("Aim").Deserialize<Aim>(Json);
                var target=input.GetProperty("Target").Deserialize<Target>(Json);
                string worldKey=WorldIdentity.Key(worldVersion!.ToJsonString(),Server.MapName)!;
                var evidence=new List<RouteEvidence>();
                if(input.TryGetProperty("Evidence",out var supplied))
                {
                    if(supplied.ValueKind!=JsonValueKind.Array || supplied.GetArrayLength()>32) throw new InvalidOperationException("Invalid bounded evidence list");
                    foreach(var item in supplied.EnumerateArray())
                    {
                        if(!Enum.TryParse<RouteEvidenceKind>(item.GetProperty("Kind").GetString(),out var kind) || !Enum.IsDefined(kind))
                            throw new InvalidOperationException("Unknown route evidence kind");
                        // Missing bindings are attached only after strict request/world/profile validation.
                        evidence.Add(new(kind,item.GetProperty("Value").GetBoolean(),
                            item.TryGetProperty("RequestId",out var boundRequest) ? boundRequest.GetString() ?? "" : requestId,
                            item.TryGetProperty("WorldKey",out var boundWorld) ? boundWorld.GetString() ?? "" : worldKey));
                    }
                }
                var report=new JsonObject { ["SchemaVersion"]=1,["RequestId"]=requestId,["CaseId"]=caseId,
                    ["RequestSha256"]=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                    ["CoreBuild"]=coreBuild,["PluginBuild"]=pluginBuild,["Map"]=Server.MapName,["WorldVersion"]=worldVersion.DeepClone(),["WorldKey"]=worldKey,
                    ["Start"]=JsonSerializer.SerializeToNode(start,NadeLabJsonContext.Default.ThrowStart),
                    ["Target"]=JsonSerializer.SerializeToNode(target,NadeLabJsonContext.Default.Target),
                    ["Aim"]=JsonSerializer.SerializeToNode(aim,NadeLabJsonContext.Default.Aim),
                    ["Physics"]=JsonSerializer.SerializeToNode(profile,NadeLabJsonContext.Default.PhysicsProfile),
                    ["PhysicsSha256"]=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(profile,NadeLabJsonContext.Default.PhysicsProfile)))).ToLowerInvariant(),
                    ["Input"]=JsonNode.Parse(input.GetRawText()),["Epoch"]=grenadeEpoch,["StartedUtc"]=DateTimeOffset.UtcNow.ToString("O"),
                    ["EvidenceScope"]="Native prediction plus explicitly supplied observations; does not prove manual reproduction or final effect.",
                    ["CacheIsolated"]=true };
                // Constructor performs only the launch trace; all flight steps yield on timers.
                var world=new NativeWorld(null);var evaluation=new KnownRouteEvaluation(world,profile,start,aim,target);
                var run=new RouteDiagnosisRun {RequestId=requestId,Map=Server.MapName,WorldKey=worldKey,Physics=profile,
                    Evaluation=evaluation,World=world,Report=report,Evidence=evidence,Epoch=grenadeEpoch};
                routeDiagnosis=run;
                Say(null,"ROUTEDIAGNOSIS started "+requestId);
                AddTimer(.02f,()=>StepRouteDiagnosis(run));
            }
            catch(Exception ex) {Say(null,"ROUTEDIAGNOSIS refused "+(requestId ?? "unknown")+": "+ex.Message);}
        });
        AddCommand("css_nl_routediagnosiscancel","Cancel only the named route diagnostic",(caller,command)=>
        {
            if(caller is not null) return;
            if(command.ArgCount!=2 || routeDiagnosis is not { } run || run.RequestId!=command.GetArg(1))
            {Say(null,"ROUTEDIAGNOSIS no matching run");return;}
            FinishRouteDiagnosis(run,"cancelled","Console cancellation");
        });
    }
    private void StepRouteDiagnosis(RouteDiagnosisRun run)
    {
        if(!ReferenceEquals(routeDiagnosis,run)) return;
        try
        {
            if(!loaded || !Practice || run.Epoch!=grenadeEpoch || run.Map!=Server.MapName || pendingMapChange is not null || !BenchmarkWorkIdle ||
                Utilities.GetPlayers().Any(p=>!p.IsBot) || WorldIdentity.Key(worldVersion?.ToJsonString(),Server.MapName)!=run.WorldKey ||
                run.Physics!=ProfileFor(new Session {Kind=run.Physics.Kind}))
            {FinishRouteDiagnosis(run,"cancelled","Practice, activity or world/profile identity changed");return;}
            if(run.Clock.Elapsed.TotalSeconds>30) {FinishRouteDiagnosis(run,"cancelled","Diagnostic wall-clock limit");return;}
            run.Evaluation.Advance(8,.5);
            if(run.Evaluation.Done) {FinishRouteDiagnosis(run,"complete",null);return;}
            AddTimer(.02f,()=>StepRouteDiagnosis(run));
        }
        catch(Exception ex) {FinishRouteDiagnosis(run,"failed",ex.Message);}
    }
    private void FinishRouteDiagnosis(RouteDiagnosisRun run,string status,string? error)
    {
        if(!ReferenceEquals(routeDiagnosis,run)) return;
        routeDiagnosis=null;
        if(status!="complete") run.Evaluation.Cancel();
        var report=run.Report;report["Status"]=status;report["Error"]=error;report["FinishedUtc"]=DateTimeOffset.UtcNow.ToString("O");
        report["WallMilliseconds"]=run.Clock.Elapsed.TotalMilliseconds;report["NativeTraces"]=run.World.TraceCount;
        if(status=="complete" && run.Evaluation.Result is { } result)
        {
            report["TargetHit"]=run.Evaluation.TargetHit;report["SimulationStatus"]=result.Status.ToString();
            report["SimulatedEndpoint"]=JsonSerializer.SerializeToNode(result.Endpoint,NadeLabJsonContext.Default.Vector3);
            report["SimulationSeconds"]=result.Time;
            run.Evidence.Add(new(RouteEvidenceKind.NativeSimulation,run.Evaluation.TargetHit!.Value,run.RequestId,run.WorldKey));
            var diagnosis=RouteDiagnosis.Classify(run.RequestId,run.WorldKey,run.Evidence);
            report["Diagnosis"]=new JsonObject {["ModelMismatch"]=diagnosis.ModelMismatch.ToString(),["SearchCoverage"]=diagnosis.SearchCoverage.ToString(),
                ["Presentation"]=diagnosis.Presentation.ToString(),["ManualReproducibility"]=diagnosis.ManualReproducibility.ToString(),
                ["FinalEffect"]=diagnosis.FinalEffect.ToString(),["IgnoredEvidence"]=diagnosis.IgnoredEvidence};
        }
        else
        {
            report["TargetHit"]=null;report["SimulationStatus"]=null;report["SimulatedEndpoint"]=null;
            report["Diagnosis"]=null;
        }
        var proofs=new JsonArray();
        for(int i=0;i<run.Evidence.Count;i++)
        {
            var proof=run.Evidence[i];
            proofs.Add(new JsonObject {["Kind"]=proof.Kind.ToString(),["Value"]=proof.Value,["RequestId"]=proof.RequestId,
                ["WorldKey"]=proof.WorldKey,["Origin"]=status=="complete" && i==run.Evidence.Count-1 ? "native" : "supplied"});
        }
        report["Evidence"]=proofs;
        string json=report.ToJsonString();
        bool queued=telemetry?.Write(Path.Combine(DataPath,"route-diagnosis-"+run.RequestId+".json"),json,false)==true;
        Say(null,"ROUTEDIAGNOSIS "+status+" "+run.RequestId+" reportQueued="+queued);
    }

    private (string? Case,string? Run,string? Pass) ReadBenchmarkIdentity(JsonElement record)
    {
        if(!record.TryGetProperty("BenchmarkRunId",out var run) || run.ValueKind==JsonValueKind.Null) return (null,null,null);
        static string Token(JsonElement item)
        {
            var value=item.GetString();
            if(string.IsNullOrEmpty(value) || value.Length>80 || value.Any(c=>!char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
                throw new InvalidOperationException("Invalid benchmark identity");
            return value;
        }
        string runId=Token(run),caseId=Token(record.GetProperty("BenchmarkCaseId"));
        string? passId=record.TryGetProperty("BenchmarkPassId",out var pass) ? Token(pass) : null;
        if(passId is not null and not "cold" and not "warm") throw new InvalidOperationException("Invalid benchmark pass");
        using var current=JsonDocument.Parse(worldVersion?.ToJsonString() ?? "null");
        if(!WorldIdentity.Matches(record.GetProperty("Map").GetString(),record.GetProperty("WorldVersion"),Server.MapName,current.RootElement))
            throw new InvalidOperationException("Benchmark map/build differs from the running world");
        return (caseId,runId,passId);
    }
}
