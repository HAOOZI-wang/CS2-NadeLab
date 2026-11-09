using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private sealed class PreviewScene
    {
        public required uint Owner;
        public required ulong SteamId;
        public readonly Queue<(Vector3 Start,Vector3 End,Color Color,float Width,float Expires)> Pending=[];
        public readonly List<(CBeam Beam,float Expires)> Beams=[];
    }
    private readonly Dictionary<int,PreviewScene> previewScenes=[];
    private readonly Queue<CBeam> retiredBeams=[];
    private readonly Dictionary<int,float> targetRangePreviewUntil=[];
    private static readonly Color TargetRegionColor=Color.FromArgb(195,65,155,255);
    private bool drawingWarm;
    private CBeam? CreatePreviewBeam(Vector3 start,Vector3 end,Color? color=null,float width=.65f,Action<CBeam>? beforeSpawn=null)
    {
        var beam=Utilities.CreateEntityByName<CBeam>("beam");
        if(beam is null) return null;
        beam.Render=color ?? Color.FromArgb(180,235,185,70); beam.Width=width;
        beam.EndPos.X=end.X; beam.EndPos.Y=end.Y; beam.EndPos.Z=end.Z;
        beam.Teleport(NativeWorld.ToNative(start),null,null);beforeSpawn?.Invoke(beam);beam.DispatchSpawn();
        return beam;
    }
    private void WarmNativeDrawing(Vector3 position)
    {
        if(drawingWarm || Utilities.GetPlayers().Any(p=>!p.IsBot)) return;
        var watch=Stopwatch.StartNew();
        var beam=CreatePreviewBeam(position,position+Vector3.UnitZ);
        if(beam is not null) {beam.Remove(); drawingWarm=true;}
        Logger.LogInformation("Preview drawing warmup ready={Ready}; startup cost {Ms:F1}ms before clients join",drawingWarm,watch.Elapsed.TotalMilliseconds);
    }
    private void ClearPreview(int slot)
    {
        targetRangePreviewUntil.Remove(slot);
        if(!previewScenes.Remove(slot,out var scene)) return;
        foreach(var item in scene.Beams) retiredBeams.Enqueue(item.Beam);
    }
    private void ClearPreviews()
    {
        foreach(var slot in previewScenes.Keys.ToArray()) ClearPreview(slot);
        while(retiredBeams.TryDequeue(out var beam)) if(beam.IsValid) beam.Remove();
        targetRangePreviewUntil.Clear();
    }
    private void Draw(CCSPlayerController owner,IReadOnlyList<Vector3> points,float seconds=8,Color? color=null,float width=.65f)
    {
        if(!owner.IsValid || points.Count<2) return;
        if(previewScenes.TryGetValue(owner.Slot,out var previous) &&
            (previous.Owner!=owner.EntityHandle.Raw || previous.SteamId!=owner.SteamID)) ClearPreview(owner.Slot);
        if(!previewScenes.TryGetValue(owner.Slot,out var scene))
            previewScenes[owner.Slot]=scene=new() {Owner=owner.EntityHandle.Raw,SteamId=owner.SteamID};
        float expires=Server.CurrentTime+Math.Clamp(seconds,1,30);
        int stride=Math.Max(1,(points.Count-1+23)/24);
        for(int i=0;i<points.Count-1 && scene.Pending.Count+scene.Beams.Count<26;i+=stride)
            scene.Pending.Enqueue((points[i],points[Math.Min(i+stride,points.Count-1)],color ?? Color.FromArgb(180,235,185,70),width,expires));
    }
    private bool ShowingTargetRange(int slot)=>targetRangePreviewUntil.TryGetValue(slot,out var until) && Server.CurrentTime<until;
    private void DrawTargetRegion(CCSPlayerController owner,Target target,float seconds=8,Color? color=null,bool showHeight=false)
    {
        // Editing acceptance bounds temporarily hides the effect approximation.
        // F4's persistent preference is preserved and resumes when this expires.
        targetRangePreviewUntil[owner.Slot]=Server.CurrentTime+Math.Clamp(seconds,1,30);
        ClearEffectPreview(owner.Slot);
        foreach(var edge in TargetRangeGeometry.Edges(target,showHeight)) Draw(owner,[edge.A,edge.B],seconds,color ?? TargetRegionColor,.6f);
    }
    private void TickPreviews()
    {
        var watch=Stopwatch.StartNew();
        // Entity creates/removes produce network work too. Replace each player's
        // preview and spread both operations across frames, without per-beam timers.
        for(int i=0;i<2 && retiredBeams.TryDequeue(out var old);i++) if(old.IsValid) old.Remove();
        int made=0;
        foreach(var (slot,scene) in previewScenes.ToArray())
        {
            var owner=Utilities.GetPlayerFromSlot(slot);
            if(!Practice || owner is not {IsValid:true} || owner.EntityHandle.Raw!=scene.Owner || owner.SteamID!=scene.SteamId)
            {ClearPreview(slot);continue;}
            for(int i=scene.Beams.Count-1;i>=0;i--)
                if(Server.CurrentTime>=scene.Beams[i].Expires) {retiredBeams.Enqueue(scene.Beams[i].Beam);scene.Beams.RemoveAt(i);}
            if(scene.Pending.Count==0 && scene.Beams.Count==0) {previewScenes.Remove(slot);continue;}
            while(made<2 && watch.Elapsed.TotalMilliseconds<.25 && scene.Pending.TryDequeue(out var segment))
            {
                if(Server.CurrentTime>=segment.Expires) continue;
                var beam=CreatePreviewBeam(segment.Start,segment.End,segment.Color,segment.Width,b=>scene.Beams.Add((b,segment.Expires)));
                if(beam is null) continue;
                made++;
            }
        }
    }
    private void RefreshSelectedGuides(CCSPlayerController p)
    {
        ClearPreview(p.Slot);
        var s=Get(p);
        if(s.Start is not { } start || s.Result?.Matches.ElementAtOrDefault(s.Selected-1) is not { } candidate) return;
        if(s.GuideEnabled && candidate.Aim.NeedsRunUp)
        {
            var aim=candidate.Aim;
            var profile=s.SearchPhysics ?? ProfileFor(s);
            var center=ThrowExecution.Resolve(start,aim,profile).ButtonFeet;
            var side=Geometry.Direction(0,aim.Yaw+90);
            var world=new NativeWorld(p.PlayerPawn.Value);
            Vector3? Ground(Vector3 point)
            {
                var floor=world.Sweep(point+new Vector3(0,0,18),point-new Vector3(0,0,18),.1f);
                return floor.Hit && !floor.StartSolid && floor.Normal.Z>=.7f ? floor.Position+Vector3.UnitZ*1.5f : null;
            }
            // Two short transverse ground segments, with a gap on the movement
            // centerline. Nothing extends upward through the aiming reticle.
            var color=Color.FromArgb(175,70,220,150);
            void Mark(Vector3 a,Vector3 b)
            {if(Ground(a) is { } ga && Ground(b) is { } gb) Draw(p,[ga,gb],30,color,.65f);}
            if(candidate.ReleaseWindow is { } window)
            {
                var edges=ThrowExecution.WindowEdges(start,aim,profile,window);
                foreach(var edge in new[]{edges.Early,edges.Late})
                {Mark(edge-side*20,edge-side*5);Mark(edge+side*5,edge+side*20);}
                Say(p,$"绿色前后短线之间为采样释放区：长度{Vector3.Distance(edges.Early,edges.Late):F0}u（已计松键延迟）；保持速度{window.MinSpeed:F0}–{window.MaxSpeed:F0}u/s。模拟采样通过，仍需手投验证。");
            }
            else
            {
                Mark(center-side*20,center-side*5);Mark(center+side*5,center+side*20);
                Say(p,"绿色短线是名义松键位置；尚未确认宽松释放区，可优先选择更简单的投法。标记30秒，菜单可关闭。");
            }
        }
        if(s.PreviewEnabled) Draw(p,candidate.Trajectory.Points,8,Color.FromArgb(160,235,185,70),.55f);
    }
}
