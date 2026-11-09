using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    // Only managed snapshots cross to the writer. No Flight, Session, pawn or
    // projectile may be captured by a serialization work item.
    private sealed record RecordedThrow(string Id,string Utc,string Map,PhysicsProfile Physics,JsonObject? WorldVersion,
        Arm Arm,ThrowObservation? Snapshot,Vector3 InitialVelocity,Vector3 FirstObservedPosition,Vector3 FirstObservedVelocity,
        Vector3 Actual,float FlightSeconds,bool Matched,bool TargetHit,float TargetError,float? ModelError,float? ReleaseDelay,
        string[] ExecutionIssues,Sample[] Points,SmokeFeedback? Feedback=null,bool GuidedMatched=false,ThrowStart? GuidedStart=null,Aim? GuidedAim=null,
        ulong? PlayerSteamId=null,bool? PlayerIsBot=null)
    {
        public bool Success => !Arm.EffectObjective && Arm.Target is not null && Matched && TargetHit;
        public bool GuidedLandingSuccess=>!Arm.EffectObjective && Arm.Target is not null && GuidedMatched && TargetHit;
    }
    private sealed record SmokeFeedback(string Verdict,string Utc,float SmokeAge,Vector3 ObserverEye,Aim ObserverView,bool? ObjectiveViewMatched=null);
    private sealed record SmokeReview(RecordedThrow Record,int Revision,float DetonatedAt,bool Queued);

    private static string SerializeThrow(RecordedThrow r)
    {
        if(r.Arm.EffectObjective || r.Arm.Target is not { } target)
            throw new InvalidOperationException("Landing telemetry requires an explicit landing target; flash-effect evidence has a separate record.");
        var record=new JsonObject { ["SchemaVersion"]=3,["ThrowId"]=r.Id,["Utc"]=r.Utc,["Map"]=r.Map,
            ["PlayerSteamId"]=r.PlayerSteamId?.ToString(),["PlayerIsBot"]=r.PlayerIsBot,
            ["Physics"]=JsonSerializer.SerializeToNode(r.Physics,NadeLabJsonContext.Default.PhysicsProfile),["WorldVersion"]=r.WorldVersion?.DeepClone(),
            ["Start"]=JsonSerializer.SerializeToNode(r.Arm.Start,NadeLabJsonContext.Default.ThrowStart),
            ["Target"]=JsonSerializer.SerializeToNode(target,NadeLabJsonContext.Default.Target),
            ["Predicted"]=r.Arm.ObservationOnly ? null : JsonSerializer.SerializeToNode(r.Arm.Candidate,NadeLabJsonContext.Default.Candidate),
            ["ActualThrow"]=r.Snapshot is null ? null : JsonSerializer.SerializeToNode(r.Snapshot,NadeLabJsonContext.Default.ThrowObservation),
            ["InitialVelocity"]=JsonSerializer.SerializeToNode(r.InitialVelocity,NadeLabJsonContext.Default.Vector3),
            ["FirstObservedPosition"]=JsonSerializer.SerializeToNode(r.FirstObservedPosition,NadeLabJsonContext.Default.Vector3),
            ["FirstObservedVelocity"]=JsonSerializer.SerializeToNode(r.FirstObservedVelocity,NadeLabJsonContext.Default.Vector3),
            ["Actual"]=JsonSerializer.SerializeToNode(r.Actual,NadeLabJsonContext.Default.Vector3),["FlightSeconds"]=r.FlightSeconds,
            ["Matched"]=r.Matched,["Success"]=r.Success,["LandingSuccess"]=r.Success,["TargetError"]=r.TargetError,["ModelError"]=r.ModelError,
            ["GuidedMatched"]=r.GuidedMatched,["GuidedLandingSuccess"]=r.GuidedLandingSuccess,
            ["GuidedStart"]=r.GuidedStart is { } guidedStart ? JsonSerializer.SerializeToNode(guidedStart,NadeLabJsonContext.Default.ThrowStart) : null,
            ["GuidedAim"]=r.GuidedAim is { } guidedAim ? JsonSerializer.SerializeToNode(guidedAim,NadeLabJsonContext.Default.Aim) : null,
            ["ObservationOnly"]=r.Arm.ObservationOnly,["TargetHit"]=r.TargetHit,["CoverageVerified"]=false,["TargetIsArea"]=target.IsArea,
            ["CoverageAssessment"]=r.Feedback is null ? "unconfirmed" : "player-reported",
            ["CoverageVerdict"]=r.Feedback?.Verdict ?? "unknown",["CoverageFeedback"]=FeedbackNode(r.Feedback),
            ["ObservedReleaseDelay"]=r.ReleaseDelay,
            ["ExecutionIssues"]=new JsonArray(r.ExecutionIssues.Select(issue=>(JsonNode?)JsonValue.Create(issue)).ToArray()),
            ["Samples"]=new JsonArray(r.Points.Select(point=>(JsonNode)new JsonObject { ["Time"]=point.Time,
                ["Position"]=JsonSerializer.SerializeToNode(point.Position,NadeLabJsonContext.Default.Vector3) }).ToArray()) };
        return record.ToJsonString(Json);
    }
    private static JsonNode? FeedbackNode(SmokeFeedback? f) => f is null ? null : new JsonObject
    {
        ["Verdict"]=f.Verdict,["Utc"]=f.Utc,["SmokeAge"]=f.SmokeAge,
        ["ObserverEye"]=JsonSerializer.SerializeToNode(f.ObserverEye,NadeLabJsonContext.Default.Vector3),
        ["ObserverView"]=JsonSerializer.SerializeToNode(f.ObserverView,NadeLabJsonContext.Default.Aim),
        ["ObjectiveViewMatched"]=f.ObjectiveViewMatched,
        ["Scope"]="player-view-only; other viewpoints and transient holes are unverified"
    };
}
