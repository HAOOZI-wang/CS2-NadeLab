using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using NadeLab.Core;
using CsTrace = CounterStrikeSharp.API.Modules.Utils.Trace;
using TraceOptions = CounterStrikeSharp.API.Modules.Utils.TraceOptions;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    // These dimensions are DISPLAY REFERENCES, never acceptance criteria or
    // calibrated effect limits. A missing sample does not prove a safe position.
    private const int EffectSegmentLimit=48;
    private readonly record struct EffectSegment(Vector3 A,Vector3 B,Color Color,float Width=.65f,float EndWidth=-1);
    private sealed class EffectBeam
    {
        public required CBeam Entity;
        public required int Slot;
        public EffectSegment Applied;
        public long Version;
        public bool Marker;
        public bool Visible;
        public int PulsePhase=int.MinValue;
    }
    private sealed record EffectBuildRequest(GrenadeKind Kind,Vector3 Center,Vector3[] FireCells);
    private sealed class EffectPreviewScene
    {
        public required int Slot;
        public required uint Controller;
        public required ulong SteamId;
        public GrenadeKind Kind;
        public Vector3 Center;
        public bool Valid,Area,Marker,Pulse;
        public Vector3[] FireCells=[];
        public float Refreshed;
        public IEnumerator<EffectSegment?>? Builder;
        public readonly EffectBuildQueue<EffectBuildRequest> Work=new();
        public readonly List<EffectSegment> Building=[];
        public readonly List<EffectSegment> MarkerSegments=[];
        public readonly List<EffectSegment> RangeSegments=[];
        public readonly List<EffectSegment> Desired=[];
        public readonly List<long> DesiredVersions=[];
        public long MarkerVersion,RangeVersion;
        public readonly List<EffectBeam> Beams=[];
        public int Cursor;
    }
    private readonly Dictionary<int,EffectPreviewScene> effectPreviewScenes=[];
    private readonly Queue<EffectBeam> retiredEffectBeams=[];
    private readonly Dictionary<int,int> effectBeamCounts=[];
    private int effectPreviewCursor;
    private bool effectPreviewRegistered;
    private long effectBeamsCreated,effectBeamsUpdated,effectRayCount,effectFilteredTransmits;
    private double effectTransmitTotalMs,effectTransmitPeakMs;
    private long effectTransmitCalls;
    private string EffectPreviewDiagnostics => $"effects: owners={effectPreviewScenes.Count}; entities={effectBeamCounts.Values.Sum()}; visible={effectPreviewScenes.Values.Sum(s=>s.Beams.Count(b=>b.Visible))}; retired={retiredEffectBeams.Count}; created={effectBeamsCreated}; updated={effectBeamsUpdated}; rays={effectRayCount}; ownerFiltered={effectFilteredTransmits}; transmitAvgMs={effectTransmitTotalMs/Math.Max(1,effectTransmitCalls):F4}; transmitPeakMs={effectTransmitPeakMs:F4}; ids={string.Join(',',effectPreviewScenes.Values.SelectMany(s=>s.Beams).Take(8).Select(b=>$"{b.Slot}:{b.Entity.Index}"))}";
    // Reuse the native vectors as all calls occur on the game thread.
    private readonly CounterStrikeSharp.API.Modules.Utils.Vector effectRayStart=new(),effectRayEnd=new();
    private static readonly TraceOptions EffectSurfaceOptions=new()
    {
        InteractsWith=Contents.Solid|Contents.Window|Contents.PassBullets|Contents.PhysicsProp
    };
    // Deliberately separate from grenade flight collision. Grenade clips and
    // player clips must not cast a supposed flash shadow. This remains geometric
    // visibility, not the game's full flash attenuation/material calculation.
    private static readonly TraceOptions EffectSightOptions=new()
    {
        InteractsWith=Contents.Solid|Contents.BlockLos|Contents.BlockLight|Contents.CsgoOpaque|Contents.PhysicsProp
    };

    private void RegisterEffectPreviewDrawing()
    {
        if(effectPreviewRegistered) return;
        effectPreviewRegistered=true;
        AddCommand("css_nl_effectstatus","Server-console effect drawing counters",(p,c)=>{if(p is null) Say(null,EffectPreviewDiagnostics);});
        RegisterListener<Listeners.CheckTransmit>(infoList=>
        {
            if(effectPreviewScenes.Count==0 && retiredEffectBeams.Count==0 && previewScenes.Count==0 && retiredBeams.Count==0) return;
            long began=Stopwatch.GetTimestamp();
            foreach(var (info,player) in infoList)
            {
                foreach(var scene in effectPreviewScenes.Values)
                {
                    bool owner=player is {IsValid:true} && player.Slot==scene.Slot && player.EntityHandle.Raw==scene.Controller && player.SteamID==scene.SteamId;
                    foreach(var beam in scene.Beams)
                        if((!owner || !beam.Visible) && beam.Entity.IsValid) {info.TransmitEntities.Remove(beam.Entity);effectFilteredTransmits++;}
                }
                // Retired entities stop being visible immediately, even while
                // their bounded removal queue is draining over later frames.
                foreach(var beam in retiredEffectBeams)
                    if(beam.Entity.IsValid) info.TransmitEntities.Remove(beam.Entity);
                // Target boundaries, trajectory lines and release guides are
                // personal too. Previously these leaked to every player while
                // effect references were correctly owner-filtered.
                foreach(var (slot,scene) in previewScenes)
                {
                    bool owner=player is {IsValid:true} && player.Slot==slot &&
                        player.EntityHandle.Raw==scene.Owner && player.SteamID==scene.SteamId;
                    foreach(var (beam,expires) in scene.Beams)
                        if((!owner || Server.CurrentTime>=expires) && beam.IsValid) info.TransmitEntities.Remove(beam);
                }
                foreach(var beam in retiredBeams)
                    if(beam.IsValid) info.TransmitEntities.Remove(beam);
            }
            double ms=Stopwatch.GetElapsedTime(began).TotalMilliseconds;
            effectTransmitCalls++;effectTransmitTotalMs+=ms;effectTransmitPeakMs=Math.Max(effectTransmitPeakMs,ms);
        });
    }

    private bool EffectMarkerReady(int slot,GrenadeKind kind,PlacementResult point)
    {
        if(!point.Valid || !effectPreviewScenes.TryGetValue(slot,out var scene) || scene.Kind!=kind || !scene.Valid || !scene.Marker || scene.Beams.Count<3) return false;
        // Confirmation must agree with the published cross, not a new camera
        // sample whose geometry is still queued. All three axes must be applied.
        foreach(var beam in scene.Beams.Take(3))
            if(!beam.Visible || !beam.Marker || !EffectDrawRules.Current(beam.Version,scene.MarkerVersion) || !beam.Entity.IsValid ||
                Vector3.Distance((beam.Applied.A+beam.Applied.B)*.5f,point.Position)>1) return false;
        return true;
    }

    private void SetEffectPreview(CCSPlayerController owner,GrenadeKind kind,Vector3 center,bool valid,bool area,bool marker,IReadOnlyList<Vector3>? actualFireCells=null,bool pulse=false)
    {
        area &= EffectReference.HasAreaPreview(kind);
        if(ShowingTargetRange(owner.Slot) && !marker) {ClearEffectPreview(owner.Slot);return;}
        if(!owner.IsValid || !FiniteEffectPoint(center) || (!area && !marker)) {ClearEffectPreview(owner.Slot);return;}
        if(effectPreviewScenes.TryGetValue(owner.Slot,out var old) && (old.SteamId!=owner.SteamID || old.Controller!=owner.EntityHandle.Raw)) ClearEffectPreview(owner.Slot);
        if(!effectPreviewScenes.TryGetValue(owner.Slot,out var scene))
            effectPreviewScenes[owner.Slot]=scene=new() {Slot=owner.Slot,Controller=owner.EntityHandle.Raw,SteamId=owner.SteamID};
        var cells=actualFireCells?.Where(FiniteEffectPoint).Take(12).ToArray() ?? [];
        pulse &= kind.Timed() && marker && valid;
        bool same=scene.Kind==kind && scene.Center==center && scene.Valid==valid && scene.Area==area && scene.Marker==marker && scene.Pulse==pulse && scene.FireCells.SequenceEqual(cells);
        // Periodically resample stationary scenes too: doors/props can move.
        if(same && (scene.Builder is not null || Server.CurrentTime-scene.Refreshed<1)) return;
        bool markerChanged=scene.Marker!=marker || scene.Center!=center || scene.Kind!=kind || scene.Valid!=valid;
        bool rangeChanged=scene.Center!=center || scene.Kind!=kind || scene.Valid!=valid || scene.Area!=area || !scene.FireCells.SequenceEqual(cells);
        scene.Kind=kind;scene.Center=center;scene.Valid=valid;scene.Area=area;scene.Marker=marker;scene.Pulse=pulse;scene.FireCells=cells;scene.Refreshed=Server.CurrentTime;
        if(markerChanged || (marker && scene.MarkerSegments.Count==0))
        {
            scene.MarkerVersion++;scene.MarkerSegments.Clear();
            if(marker)
            {
                var color=valid ? Color.FromArgb(200,60,235,225) : Color.FromArgb(230,255,65,65);
                foreach(var axis in new[]{Vector3.UnitX,Vector3.UnitY,Vector3.UnitZ})
                    scene.MarkerSegments.Add(new(center-axis*5,center+axis*5,color,1));
            }
        }
        if(!area || !valid || kind==GrenadeKind.Decoy)
        {
            // Disabling the display is a cancellation, not another movement
            // request. Stop obsolete native traces immediately in this case.
            scene.Builder?.Dispose();scene.Builder=null;scene.Work.Clear();scene.Building.Clear();scene.RangeSegments.Clear();
        }
        else
        {
            // Never reset active geometry because the camera moved. One latest
            // replacement is enough; completed superseded work is not displayed
            // beside a newer marker at a different centre.
            scene.Work.Enqueue(new(kind,center,cells));
            if(rangeChanged) scene.RangeSegments.Clear();
            StartEffectBuild(scene,owner.PlayerPawn.Value);
        }
        RefreshEffectDesired(scene);
    }

    private void StartEffectBuild(EffectPreviewScene scene,CCSPlayerPawn? pawn)
    {
        if(scene.Builder is not null || scene.Work.Active is not { } work) return;
        var request=work.Value;scene.Building.Clear();
        scene.Builder=BuildEffectSegments(pawn,request.Kind,request.Center,true,true,false,request.FireCells).GetEnumerator();
    }

    private static void RefreshEffectDesired(EffectPreviewScene scene)
    {
        scene.Desired.Clear();scene.DesiredVersions.Clear();
        foreach(var marker in scene.MarkerSegments)
        {scene.Desired.Add(marker);scene.DesiredVersions.Add(scene.MarkerVersion);}
        foreach(var segment in scene.RangeSegments.Take(EffectSegmentLimit-scene.Desired.Count))
        {scene.Desired.Add(segment);scene.DesiredVersions.Add(scene.RangeVersion);}
        // Identical native content can adopt a new version without a network
        // update, avoiding a one-frame blink on stationary periodic refreshes.
        // Every changed segment stays hidden until the new endpoints are applied.
        for(int i=0;i<scene.Beams.Count;i++)
        {
            var beam=scene.Beams[i];
            if(i<scene.Desired.Count && beam.Marker==(i<scene.MarkerSegments.Count) && beam.Applied==scene.Desired[i])
                beam.Version=scene.DesiredVersions[i];
            beam.Visible=i<scene.Desired.Count && beam.Marker==(i<scene.MarkerSegments.Count) &&
                EffectDrawRules.Current(beam.Version,scene.DesiredVersions[i]) && beam.Applied==scene.Desired[i];
        }
        scene.Cursor=0;
    }

    private void ClearEffectPreview(int slot)
    {
        if(!effectPreviewScenes.Remove(slot,out var scene)) return;
        scene.Builder?.Dispose();scene.Work.Clear();
        foreach(var beam in scene.Beams) {beam.Visible=false;retiredEffectBeams.Enqueue(beam);}
    }

    private void ClearEffectPreviews()
    {
        foreach(var slot in effectPreviewScenes.Keys.ToArray()) ClearEffectPreview(slot);
        // Map end/unload is the only unbudgeted drain; no native entities survive
        // as references in the next world or plugin instance.
        while(retiredEffectBeams.TryDequeue(out var beam)) if(beam.Entity.IsValid) beam.Entity.Remove();
        effectBeamCounts.Clear();
    }

    private void TickEffectPreviewDrawing()
    {
        if(effectPreviewScenes.Count==0 && retiredEffectBeams.Count==0) return;
        var watch=Stopwatch.StartNew();
        int removed=0,made=0,updated=0,geometrySteps=0;
        var slots=effectPreviewScenes.Keys.ToArray();
        int first=slots.Length==0 ? 0 : (effectPreviewCursor++&int.MaxValue)%slots.Length;
        var scenes=new List<(EffectPreviewScene Scene,CCSPlayerPawn Pawn)>();
        for(int offset=0;offset<slots.Length;offset++)
        {
            int slot=slots[(first+offset)%slots.Length];
            if(!effectPreviewScenes.TryGetValue(slot,out var scene)) continue;
            var owner=Utilities.GetPlayerFromSlot(slot);
            if(owner is not {IsValid:true} || owner.EntityHandle.Raw!=scene.Controller || owner.SteamID!=scene.SteamId || owner.PlayerPawn.Value is not {IsValid:true,LifeState:0} pawn || !Practice)
            {ClearEffectPreview(slot);continue;}
            scenes.Add((scene,pawn));
        }

        // Markers are independent of range tracing/publication. In the common
        // warm case all three axes update in one frame; cold creation keeps the
        // existing global two-entity limit and therefore takes at least two.
        foreach(var (scene,_) in scenes)
        {
            if(watch.Elapsed.TotalMilliseconds>=.3) break;
            try
            {
                for(int i=scene.Beams.Count-1;i>=0;i--)
                    if(!scene.Beams[i].Entity.IsValid)
                    {scene.Beams.RemoveAt(i);effectBeamCounts[scene.Slot]=Math.Max(0,effectBeamCounts.GetValueOrDefault(scene.Slot)-1);}
                RefreshEffectVisibility(scene);
                for(int i=0;i<scene.MarkerSegments.Count && watch.Elapsed.TotalMilliseconds<.3;i++)
                    ApplyEffectDesired(scene,i,ref made,ref updated);
                TickMarkerPulse(scene,watch,ref updated);
            }
            catch(Exception ex) {StopEffectScene(scene,ex);}
        }
        // Retired entities are invisible immediately, but deletion still shares
        // the global frame budget and per-owner entity cap with new allocations.
        while(removed<2 && watch.Elapsed.TotalMilliseconds<.3 && retiredEffectBeams.TryDequeue(out var retired))
        {if(retired.Entity.IsValid) retired.Entity.Remove();effectBeamCounts[retired.Slot]=Math.Max(0,effectBeamCounts.GetValueOrDefault(retired.Slot)-1);removed++;}

        // Finish uploading a published generation before spending the remaining
        // frame on more traces. Version checks keep every old line hidden during
        // this bounded partial upload; ranges never delay marker work.
        foreach(var (scene,_) in scenes)
        {
            if(watch.Elapsed.TotalMilliseconds>=.3) break;
            if(!effectPreviewScenes.ContainsKey(scene.Slot)) continue;
            try
            {
                while(made<2 && scene.Beams.Count<scene.Desired.Count && watch.Elapsed.TotalMilliseconds<.3)
                {
                    int before=scene.Beams.Count;
                    ApplyEffectDesired(scene,before,ref made,ref updated);
                    if(scene.Beams.Count==before) break;
                }
                int count=Math.Min(scene.Beams.Count,scene.Desired.Count)-scene.MarkerSegments.Count;
                for(int examined=0;examined<count && updated<4 && watch.Elapsed.TotalMilliseconds<.3;examined++)
                {
                    int i=scene.MarkerSegments.Count+(scene.Cursor++&int.MaxValue)%count;
                    ApplyEffectDesired(scene,i,ref made,ref updated);
                }
            }
            catch(Exception ex) {StopEffectScene(scene,ex);}
        }
        foreach(var (scene,pawn) in scenes)
        {
            if(watch.Elapsed.TotalMilliseconds>=.3 || geometrySteps>=6) break;
            if(!effectPreviewScenes.ContainsKey(scene.Slot)) continue;
            try
            {
                StartEffectBuild(scene,pawn);
                while(scene.Builder is not null && geometrySteps<6 && watch.Elapsed.TotalMilliseconds<.3)
                {
                    geometrySteps++;
                    if(scene.Builder.MoveNext())
                    {
                        if(scene.Builder.Current is { } segment && scene.Building.Count<EffectSegmentLimit-3) scene.Building.Add(segment);
                        continue;
                    }
                    scene.Builder.Dispose();scene.Builder=null;
                    var completed=scene.Work.Complete();
                    if(completed.Version==scene.Work.LatestVersion)
                    {
                        scene.RangeSegments.Clear();scene.RangeSegments.AddRange(scene.Building);scene.RangeVersion=completed.Version;
                        RefreshEffectDesired(scene);
                    }
                    StartEffectBuild(scene,pawn);
                }
            }
            catch(Exception ex) {StopEffectScene(scene,ex);}
        }
    }

    private static void RefreshEffectVisibility(EffectPreviewScene scene)
    {
        for(int i=0;i<scene.Beams.Count;i++)
        {
            var beam=scene.Beams[i];
            beam.Visible=i<scene.Desired.Count && beam.Marker==(i<scene.MarkerSegments.Count) &&
                EffectDrawRules.Current(beam.Version,scene.DesiredVersions[i]) && beam.Applied==scene.Desired[i];
        }
    }

    private void ApplyEffectDesired(EffectPreviewScene scene,int i,ref int made,ref int updated)
    {
        if(i>=scene.Desired.Count || i>scene.Beams.Count) return;
        var segment=scene.Desired[i];bool marker=i<scene.MarkerSegments.Count;
        if(i==scene.Beams.Count)
        {
            if(made>=2 || effectBeamCounts.GetValueOrDefault(scene.Slot)>=EffectSegmentLimit) return;
            var entity=Utilities.CreateEntityByName<CBeam>("beam");made++;
            if(entity is null) return;
            // Register before DispatchSpawn so transmission never exposes an
            // unowned or partially initialized entity to another player.
            var created=new EffectBeam {Entity=entity,Slot=scene.Slot,Applied=segment,Marker=marker,Version=scene.DesiredVersions[i]};
            scene.Beams.Add(created);effectBeamCounts[scene.Slot]=effectBeamCounts.GetValueOrDefault(scene.Slot)+1;effectBeamsCreated++;
            ApplyEffectSegment(entity,segment,true);entity.DispatchSpawn();created.Visible=true;
            return;
        }
        var beam=scene.Beams[i];
        if(beam.Applied!=segment)
        {
            if(updated>=4) return;
            ApplyEffectSegment(beam.Entity,segment,false);beam.Applied=segment;updated++;effectBeamsUpdated++;
            beam.PulsePhase=int.MinValue;
        }
        beam.Marker=marker;beam.Version=scene.DesiredVersions[i];beam.Visible=true;
    }

    private void TickMarkerPulse(EffectPreviewScene scene,Stopwatch watch,ref int updated)
    {
        // Reuse the exact three marker entities. Tint/width are an independent
        // cosmetic overlay: positions and geometry versions do not change, so
        // EffectMarkerReady never waits for a cosmetic pulse update.
        int phase=scene.Pulse ? (int)(Server.CurrentTime*2)%2+(scene.Kind==GrenadeKind.Flash ? 0 : 2) : -1;
        foreach(var beam in scene.Beams.Take(scene.MarkerSegments.Count))
        {
            if(updated>=4 || watch.Elapsed.TotalMilliseconds>=.3) break;
            if(!beam.Marker || !beam.Visible || !beam.Entity.IsValid || beam.PulsePhase==phase) continue;
            if(phase<0 && beam.PulsePhase==int.MinValue) {beam.PulsePhase=phase;continue;}
            bool bright=phase>=0 && phase%2==1;
            var color=phase<0 ? beam.Applied.Color : scene.Kind==GrenadeKind.Flash ?
                Color.FromArgb(bright ? 225 : 135,255,250,230) : Color.FromArgb(bright ? 225 : 135,255,180,65);
            float width=phase<0 ? beam.Applied.Width : bright ? 1.4f : .75f;
            beam.Entity.Render=color;beam.Entity.Width=width;beam.Entity.EndWidth=width;
            Utilities.SetStateChanged(beam.Entity,"CBaseModelEntity","m_clrRender");
            Utilities.SetStateChanged(beam.Entity,"CBeam","m_fWidth");
            Utilities.SetStateChanged(beam.Entity,"CBeam","m_fEndWidth");
            beam.PulsePhase=phase;updated++;effectBeamsUpdated++;
        }
    }

    private void StopEffectScene(EffectPreviewScene scene,Exception ex)
    {
        ClearEffectPreview(scene.Slot);
        Logger.LogWarning(ex,"Effect preview stopped safely for slot {Slot}",scene.Slot);
    }

    private static void ApplyEffectSegment(CBeam beam,EffectSegment segment,bool spawning)
    {
        beam.Render=segment.Color;beam.Width=segment.Width;
        beam.EndWidth=segment.EndWidth<0 ? segment.Width : segment.EndWidth;
        beam.EndPos.X=segment.B.X;beam.EndPos.Y=segment.B.Y;beam.EndPos.Z=segment.B.Z;
        beam.Teleport((Vector3?)segment.A,null,null);
        if(spawning) return;
        Utilities.SetStateChanged(beam,"CBeam","m_vecEndPos");
        Utilities.SetStateChanged(beam,"CBeam","m_fWidth");
        Utilities.SetStateChanged(beam,"CBeam","m_fEndWidth");
        Utilities.SetStateChanged(beam,"CBaseModelEntity","m_clrRender");
    }

    private TraceResult EffectRay(CCSPlayerPawn? ignored,Vector3 a,Vector3 b,TraceOptions options)
    {
        effectRayStart.X=a.X;effectRayStart.Y=a.Y;effectRayStart.Z=a.Z;
        effectRayEnd.X=b.X;effectRayEnd.Y=b.Y;effectRayEnd.Z=b.Z;
        effectRayCount++;
        return CsTrace.TraceEndShape(effectRayStart,effectRayEnd,ignored is {IsValid:true} ? ignored : null,options);
    }
    private bool EffectClear(CCSPlayerPawn? ignored,Vector3 a,Vector3 b,TraceOptions options)
    {
        var hit=EffectRay(ignored,a,b,options);
        return EffectDrawRules.RayClear(hit.IsAllSolid,hit.DidHit(),hit.Fraction);
    }
    private static bool FiniteEffectPoint(Vector3 point)=>float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
    private static Vector3 EffectDirection(float angle)=>new(MathF.Cos(angle),MathF.Sin(angle),0);

    private IEnumerable<EffectSegment?> BuildEffectSegments(CCSPlayerPawn? pawn,GrenadeKind kind,Vector3 center,bool valid,bool area,bool marker,Vector3[] cells)
    {
        if(marker)
        {
            var color=valid ? Color.FromArgb(200,60,235,225) : Color.FromArgb(230,255,65,65);
            foreach(var axis in new[]{Vector3.UnitX,Vector3.UnitY,Vector3.UnitZ})
                yield return new(center-axis*5,center+axis*5,color,1);
        }
        if(!area || !valid || !EffectReference.HasAreaPreview(kind)) yield break;
        if(kind==GrenadeKind.Smoke)
        {
            // Neutral dashed volume reference; the blue solid boundary belongs
            // exclusively to acceptable grenade landing positions.
            var color=Color.FromArgb(135,210,220,235);
            float radius=EffectReference.Radius(kind);
            var interior=center+Vector3.UnitZ*64;
            // Open dashes, not an opaque shell. Clip samples and each visible
            // chord so separated regions are never joined through a wall.
            for(int plane=0;plane<3;plane++)
                for(int i=0;i<(plane==0 ? 16 : 8);i++)
                {
                    int n=plane==0 ? 16 : 8;
                    float a=i*(plane==0 ? MathF.Tau : MathF.PI)/n,b=(i+.7f)*(plane==0 ? MathF.Tau : MathF.PI)/n;
                    Vector3 Point(float t)=>plane==0 ? center+EffectDirection(t)*radius+Vector3.UnitZ*8 :
                        center+(plane==1 ? Vector3.UnitX : Vector3.UnitY)*MathF.Cos(t)*radius+Vector3.UnitZ*(8+MathF.Sin(t)*128);
                    var start=Point(a);var end=Point(b);
                    bool visible=EffectClear(pawn,interior,start,EffectSurfaceOptions) && EffectClear(pawn,interior,end,EffectSurfaceOptions) && EffectClear(pawn,start,end,EffectSurfaceOptions);
                    yield return visible ? new(start,end,color) : null;
                }
            yield break;
        }
        if(kind.Fire())
        {
            if(cells.Length>0)
            {
                // Engine-observed fire cells are their own layer. Combining
                // them with a hypothetical orange ring at another candidate's
                // endpoint made the displayed coverage impossible to interpret.
                foreach(var point in cells)
                {
                    var p=point+Vector3.UnitZ*3;
                    yield return new(p-Vector3.UnitX*4,p+Vector3.UnitX*4,Color.FromArgb(220,100,255,110),1);
                    yield return new(p-Vector3.UnitY*4,p+Vector3.UnitY*4,Color.FromArgb(220,100,255,110),1);
                }
                yield break;
            }
            var source=center+Vector3.UnitZ*8;
            float radius=EffectReference.Radius(kind);
            for(int i=0;i<16;i++)
            {
                var direction=EffectDirection(i*MathF.Tau/16);
                var wall=EffectRay(pawn,source,source+direction*radius,EffectSurfaceOptions);
                float reach=Math.Max(0,radius*wall.Fraction-4);
                if(wall.IsAllSolid || reach<12) {yield return null;continue;}
                var edge=source+direction*reach;
                var ground=EffectRay(pawn,edge+Vector3.UnitZ*24,edge-Vector3.UnitZ*40,EffectSurfaceOptions);
                if(!ground.DidHit() || ground.IsAllSolid || ground.Normal.Z<.7f) {yield return null;continue;}
                var point=NativeWorld.FromNative(ground.EndPos)+Vector3.UnitZ*2;
                var tangent=new Vector3(-direction.Y,direction.X,0)*7;
                var a=point-tangent;var b=point+tangent;
                bool clear=EffectClear(pawn,source,point,EffectSurfaceOptions) && EffectClear(pawn,a,b,EffectSurfaceOptions);
                yield return clear ? new(a,b,Color.FromArgb(165,255,150,45),.75f) : null;
            }
            yield break;
        }
        if(kind==GrenadeKind.HighExplosive)
        {
            // One dashed 3D extent instead of three planar "damage zones".
            // Nested arbitrary radii implied measured damage thresholds and
            // hid the vertical extent of a freely placed air explosion.
            var color=Color.FromArgb(145,255,195,75);
            float radius=EffectReference.Radius(kind);
            foreach(var edge in EffectRangeGeometry.Sphere(center,radius))
            {
                bool clear=EffectClear(pawn,center,edge.A,EffectSurfaceOptions) && EffectClear(pawn,center,edge.B,EffectSurfaceOptions) && EffectClear(pawn,edge.A,edge.B,EffectSurfaceOptions);
                yield return clear ? new(edge.A,edge.B,color,.65f) : null;
            }
            yield break;
        }
    }
}
