using System.Numerics;
using System.Text.Json;
using CounterStrikeSharp.API;
using NadeLab.Core;
using Microsoft.Extensions.Logging;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private readonly List<(ThrowStart Start,Aim Aim)> failedLaunches=[];
    private void ReadFailureEvidence()
    {
        failedLaunches.Clear();
        string path=Path.Combine(DataPath,"throws.jsonl");
        if(!File.Exists(path)) return;
        string[] lines;
        try {lines=BoundedLogTail.ReadLines(path);}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {Logger.LogWarning(ex,"Failed-throw history could not be read; search will continue without failure hints");return;}
        foreach(string line in lines)
        {
            try
            {
                using var doc=JsonDocument.Parse(line); var root=doc.RootElement;
                // Only matching actions on this build can disqualify a launch.
                // Misses due to human execution never become an unreachable claim.
                using var current=JsonDocument.Parse(worldVersion?.ToJsonString() ?? "null");
                if(!WorldIdentity.Matches(root.GetProperty("Map").GetString(),root.GetProperty("WorldVersion"),Server.MapName,current.RootElement) ||
                    !root.GetProperty("Matched").GetBoolean() || root.GetProperty("TargetHit").GetBoolean() ||
                    root.GetProperty("ModelError").GetSingle()<40) continue;
                var start=root.GetProperty("Start").Deserialize<ThrowStart>(Json);
                var aim=root.GetProperty("Predicted").GetProperty("Aim").Deserialize<Aim>(Json);
                // Jump model changed. Older mismatches must not ban the corrected latch.
                if(aim.Airborne) continue;
                failedLaunches.Add((start,aim));
            }
            catch(JsonException) { }
            catch(InvalidOperationException) { }
            catch(KeyNotFoundException) { }
        }
        Logger.LogInformation("Loaded {Count} matching failed launches for this build",failedLaunches.Count);
    }
    private Func<Aim,bool> FailureFilter(ThrowStart start)
    {
        var nearby=failedLaunches.Where(f=>f.Start.Origin==start.Origin && (start.Origin==ThrowOrigin.Release || f.Start.RunUpDistance==start.RunUpDistance) &&
            Vector3.Distance(f.Start.Feet,start.Feet)<=4 && MathF.Abs(f.Start.EyeHeight-start.EyeHeight)<=2).ToArray();
        return aim=>nearby.Any(f=>f.Aim.EffectiveAction==aim.EffectiveAction && f.Aim.Mode==aim.Mode &&
            MathF.Abs(f.Aim.ForwardSpeed-aim.ForwardSpeed)<5 && MathF.Abs(f.Aim.Pitch-aim.Pitch)<=.5f &&
            MathF.Abs(Geometry.NormalizeYaw(f.Aim.Yaw-aim.Yaw))<=.5f);
    }
}
