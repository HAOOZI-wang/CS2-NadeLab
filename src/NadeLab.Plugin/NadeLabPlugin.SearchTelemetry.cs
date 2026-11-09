using System.Text.Json;
using System.Text.Json.Nodes;
using CounterStrikeSharp.API;
using NadeLab.Core;
using Stopwatch=System.Diagnostics.Stopwatch;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private void QueueFlashSearchTelemetry(FlashSearchJob current,Session session,FlashSearchResult result)
    {
        // Freeze the request alongside the outcome, including unsuccessful runs.
        // Point-search logs cannot reconstruct observer roles, poses or failures.
        string map=Server.MapName,utc=DateTimeOffset.UtcNow.ToString("O");
        var version=worldVersion?.DeepClone();
        var start=current.Search.Start;
        var observers=current.Search.Objective.Observers.ToArray();
        float minimum=current.Search.Objective.MinimumFlashSeconds;
        var profile=session.SearchPhysics!;
        var view=session.ReferenceView;
        var diagnostics=lastSearchDiagnostics!;
        int budget=current.Search.MaxCandidates,probes=current.Search.StabilityProbes;
        var seeds=current.Search.SeedAims.ToArray();
        double seconds=Stopwatch.GetElapsedTime(current.Started).TotalSeconds;
        string actor=Utilities.GetPlayerFromSlot(current.Slot) is {IsValid:true} player ? player.IsBot ? "bot" : "human" : "unknown";
        telemetry!.Write(Path.Combine(DataPath,"flash-searches.jsonl"),()=>new JsonObject
        {
            ["SchemaVersion"]=2,["ObjectiveMode"]="flash-observers",["Kind"]="Flash",["Utc"]=utc,["Map"]=map,["WorldVersion"]=version,
            ["Physics"]=JsonSerializer.SerializeToNode(profile,NadeLabJsonContext.Default.PhysicsProfile),
            ["Start"]=JsonSerializer.SerializeToNode(start,NadeLabJsonContext.Default.ThrowStart),
            ["PreferredView"]=view is { } reference ? JsonSerializer.SerializeToNode(reference,NadeLabJsonContext.Default.Aim) : null,
            ["Observers"]=JsonSerializer.SerializeToNode(observers,NadeLabJsonContext.Default.FlashObserverArray),
            ["MinimumFlashSeconds"]=minimum,["MaxCandidates"]=budget,["StabilityProbes"]=probes,["ElapsedSeconds"]=seconds,
            ["PersonalHintCount"]=seeds.Length,["SeedAims"]=JsonSerializer.SerializeToNode(seeds,NadeLabJsonContext.Default.AimArray),
            ["Result"]=JsonSerializer.SerializeToNode(result,NadeLabJsonContext.Default.FlashSearchResult),
            ["Diagnostics"]=DiagnosticsJson(diagnostics),["Actor"]=actor
        }.ToJsonString(Json));
    }

    private void QueueSearchTelemetry(SearchJob current,Session session,SearchResult result)
    {
        // Read game/session state now. The serializer below captures no native
        // wrapper, session, mutable job or live world. Completed result records
        // and their trajectory arrays are never mutated after publication.
        string map=Server.MapName,utc=DateTimeOffset.UtcNow.ToString("O");
        var version=worldVersion?.DeepClone();
        var start=current.SearchedStart ?? session.Start!.Value;
        var requested=session.RequestedStart ?? session.Start!.Value;
        var target=session.Target!.Value;
        var profile=current.Search.Profile;
        var view=session.ReferenceView;
        var diagnostics=lastSearchDiagnostics!;
        var phases=current.PhaseWork.ToArray();
        double seconds=current.ActiveSeconds;
        int continuations=current.Search.Continuations;bool expanded=current.Expanded;
        double? firstRecommendation=current.FirstRecommendationSeconds;
        double? firstUsable=current.FirstUsableSeconds;
        string? benchmarkCase=current.BenchmarkCaseId,benchmarkRun=current.BenchmarkRunId;
        string? benchmarkPass=current.BenchmarkPassId;int priorHints=current.Search.InitialHintCount;
        string actor=current.ConsoleSession is not null ? "console-replay" :
            Utilities.GetPlayerFromSlot(current.Slot) is {IsValid:true} player ? player.IsBot ? "bot" : "human" : "unknown";
        JsonObject PhaseNode()
        {
            var node=new JsonObject(); foreach(var (phase,ms) in phases) node[phase]=ms; return node;
        }
        telemetry!.Write(Path.Combine(DataPath,"searches.jsonl"),()=>new JsonObject
        {
            ["Kind"]=profile.Kind.ToString(),["Physics"]=JsonSerializer.SerializeToNode(profile,NadeLabJsonContext.Default.PhysicsProfile),["SchemaVersion"]=4,["Continuations"]=continuations,["Expanded"]=expanded,["Map"]=map,["WorldVersion"]=version,["Utc"]=utc,
            ["Start"]=JsonSerializer.SerializeToNode(start,NadeLabJsonContext.Default.ThrowStart),
            ["RequestedStart"]=JsonSerializer.SerializeToNode(requested,NadeLabJsonContext.Default.ThrowStart),
            ["PreferredView"]=view is { } reference ? JsonSerializer.SerializeToNode(reference,NadeLabJsonContext.Default.Aim) : null,
            ["Target"]=JsonSerializer.SerializeToNode(target,NadeLabJsonContext.Default.Target),
            ["Result"]=JsonSerializer.SerializeToNode(result,NadeLabJsonContext.Default.SearchResult),
            ["Diagnostics"]=DiagnosticsJson(diagnostics),["PhaseWorkMs"]=PhaseNode(),["FirstRecommendationSeconds"]=firstRecommendation,["FirstUsableSeconds"]=firstUsable,
            ["BenchmarkCaseId"]=benchmarkCase,["BenchmarkRunId"]=benchmarkRun,["BenchmarkPassId"]=benchmarkPass,["PriorHintCount"]=priorHints,["Actor"]=actor
        }.ToJsonString(Json));
        if(current.ConsoleSession is not null)
            telemetry.Write(Path.Combine(DataPath,"replay-last.json"),()=>new JsonObject
            {
                ["BenchmarkCaseId"]=benchmarkCase,["BenchmarkRunId"]=benchmarkRun,["BenchmarkPassId"]=benchmarkPass,["PriorHintCount"]=priorHints,["Map"]=map,["WorldVersion"]=version?.DeepClone(),["Kind"]=profile.Kind.ToString(),["Utc"]=utc,
                ["Start"]=JsonSerializer.SerializeToNode(start,NadeLabJsonContext.Default.ThrowStart),["RequestedStart"]=JsonSerializer.SerializeToNode(requested,NadeLabJsonContext.Default.ThrowStart),
                ["Target"]=JsonSerializer.SerializeToNode(target,NadeLabJsonContext.Default.Target),["Physics"]=JsonSerializer.SerializeToNode(profile,NadeLabJsonContext.Default.PhysicsProfile),
                ["Seconds"]=seconds,["Result"]=JsonSerializer.SerializeToNode(result,NadeLabJsonContext.Default.SearchResult),
                ["Diagnostics"]=DiagnosticsJson(diagnostics),["PhaseWorkMs"]=PhaseNode(),["FirstRecommendationSeconds"]=firstRecommendation,["FirstUsableSeconds"]=firstUsable
            }.ToJsonString(Json),false);
    }
}
