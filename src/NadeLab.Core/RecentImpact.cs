using System.Numerics;
using System.Text.Json;

namespace NadeLab.Core;

public readonly record struct ImpactOwner(int Epoch,uint Controller,ulong Steam);
public sealed record RecordedImpact(string Id,GrenadeKind Kind,ulong Steam,Vector3 Position,DateTimeOffset Utc);

/// <summary>Only event positions qualify. Trajectory samples and intended targets never do.</summary>
public static class RecentImpactRules
{
    public static bool Matches(ImpactOwner released,ImpactOwner current,GrenadeKind releasedKind,GrenadeKind eventKind,
        uint projectile,uint liveProjectile) => released==current && released.Controller!=0 &&
        releasedKind==eventKind && Enum.IsDefined(eventKind) && projectile!=0 && projectile==liveProjectile;

    public static Target TargetFor(GrenadeKind kind,Vector3 actual)
    {
        if(!Enum.IsDefined(kind) || !Geometry.Finite(actual)) throw new ArgumentException("Invalid real impact");
        // Keep the exact event centre, including an airborne timed explosion.
        // Do not snap to a floor, a trajectory sample or an existing preset.
        return kind==GrenadeKind.Smoke ? SmokeTargets.Around(actual) :
            new(actual,48,new Vector3(48,48,kind.Timed() ? 48 : 12),
                kind.Timed() ? "真实爆点区域（效果待实投）" : kind.Fire() ? "实投火瓶爆发点（不代表起火）" : "真实诱饵生效点",Airspace:kind.Timed());
    }

    public static RecordedImpact? ReadArchive(string line,string map,string worldJson)
    {
        try
        {
            using var document=JsonDocument.Parse(line);using var world=JsonDocument.Parse(worldJson);
            var root=document.RootElement;
            if(!root.TryGetProperty("PlayerSteamId",out var steamNode) || steamNode.ValueKind!=JsonValueKind.String ||
                !ulong.TryParse(steamNode.GetString(),out ulong steam) || steam==0 ||
                !root.TryGetProperty("Map",out var mapNode) || mapNode.GetString()!=map ||
                !root.TryGetProperty("WorldVersion",out var recordedWorld) || !WorldIdentity.Matches(map,recordedWorld,map,world.RootElement) ||
                !root.TryGetProperty("Kind",out var kindNode) || kindNode.ValueKind!=JsonValueKind.String ||
                !Enum.TryParse<GrenadeKind>(kindNode.GetString(),out var kind) || !Enum.IsDefined(kind) ||
                !root.TryGetProperty("Outcome",out var outcomeNode) || !AcceptedOutcome(kind,outcomeNode.GetString()) ||
                !root.TryGetProperty("Actual",out var point) || !TryVector(point,out var actual) ||
                !root.TryGetProperty("Utc",out var utcNode) || !DateTimeOffset.TryParse(utcNode.GetString(),out var utc) ||
                !root.TryGetProperty("ThrowId",out var idNode) || string.IsNullOrWhiteSpace(idNode.GetString())) return null;
            return new(idNode.GetString()!,kind,steam,actual,utc);
        }
        catch(Exception ex) when(ex is JsonException or InvalidOperationException or FormatException or OverflowException)
        {return null;}
    }

    private static bool AcceptedOutcome(GrenadeKind kind,string? outcome)=>kind switch
    {
        GrenadeKind.Flash or GrenadeKind.HighExplosive=>outcome=="detonated",
        GrenadeKind.Smoke=>outcome=="smoke-detonated",
        GrenadeKind.Decoy=>outcome=="sound-started",
        GrenadeKind.Molotov or GrenadeKind.Incendiary=>outcome=="burst-ignition-unconfirmed",
        _=>false
    };
    private static bool TryVector(JsonElement node,out Vector3 point)
    {
        point=default;
        if(node.ValueKind!=JsonValueKind.Object || !node.TryGetProperty("X",out var x) || !node.TryGetProperty("Y",out var y) ||
            !node.TryGetProperty("Z",out var z) || !x.TryGetSingle(out float a) || !y.TryGetSingle(out float b) || !z.TryGetSingle(out float c)) return false;
        point=new(a,b,c);return Geometry.Finite(point);
    }
}
