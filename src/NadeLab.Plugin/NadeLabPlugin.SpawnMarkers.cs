using System.Numerics;
using System.Drawing;
using System.Diagnostics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private sealed record SpawnMarker(CBaseEntity Entity,uint Handle);
    private sealed record SpawnMarkerDraw(Vector3 Start,Vector3 End,string? Text,Vector3 Angles);
    private readonly List<SpawnMarker> spawnMarkers=[];
    private readonly Queue<SpawnMarkerDraw> spawnMarkerQueue=[];
    private readonly List<(bool Ct,NumberedSpawn Entry)> ungroundedSpawns=[];
    private string? spawnMarkerKey;
    private float spawnMarkerRetryAt;
    private int spawnMarkersCreated;
    private static readonly Color SpawnGreen=Color.FromArgb(225,70,245,65);

    private void RegisterSpawnMarkers()
    {
        AddTimer(1,EnsureSpawnMarkers,TimerFlags.REPEAT);
        RegisterListener<Listeners.OnTick>(TickSpawnMarkers);
        AddCommand("css_nl_spawnmarkcheck","Empty-server persistent spawn marker cleanup check",(player,command)=>
        {
            if(player is not null || !Practice || !BenchmarkIdle || Utilities.GetPlayers().Any(p=>!p.IsBot) || spawnMarkerQueue.Count>0 || ungroundedSpawns.Count>0 || spawnMarkers.Count==0)
            {Say(null,"SPAWNMARKCHECK refused: empty idle practice server with completed markers required.");return;}
            int epoch=grenadeEpoch;var handles=spawnMarkers.Select(m=>m.Handle).ToArray();ClearSpawnMarkers();
            AddTimer(.2f,()=>
            {
                if(grenadeEpoch!=epoch || !loaded) return;
                bool removed=handles.All(h=>new CHandle<CBaseEntity>(h).Value is not {IsValid:true});
                Say(null,$"SPAWNMARKCHECK {(removed ? "PASS" : "FAIL")}: {handles.Length} retired native handles; pending={spawnMarkerQueue.Count}; scene restores automatically.");
                EnsureSpawnMarkers();
            });
        });
    }

    private void EnsureSpawnMarkers()
    {
        if(!loaded || !Practice) {if(spawnMarkerKey is not null) ClearSpawnMarkers();return;}
        if(Server.CurrentTime<spawnMarkerRetryAt) return;
        var sites=new[]{false,true}.SelectMany(ct=>TrainingSpawns(ct).Select(s=>(Ct:ct,Entry:s))).ToArray();
        string key=Server.MapName+"|"+string.Join('|',sites.Select(s=>$"{s.Ct}:{s.Entry.Number}:{s.Entry.Location.Position}:{s.Entry.Location.Priority}"));
        if(key!=spawnMarkerKey || spawnMarkers.Any(m=>!m.Entity.IsValid || m.Entity.EntityHandle.Raw!=m.Handle))
        {ClearSpawnMarkers();spawnMarkerKey=key;ungroundedSpawns.AddRange(sites);}
        if(ungroundedSpawns.Count==0) return;
        var world=new NativeWorld(null);
        Vector3? Ground(Vector3 p)
        {
            var hit=world.Sweep(p+Vector3.UnitZ*32,p-Vector3.UnitZ*192,0);
            return hit.Hit && !hit.StartSolid && hit.Normal.Z>=.7f ? hit.Position : null;
        }
        // Retry only missing ground projections. Successful boxes stay intact,
        // so a delayed map collision load never causes whole-scene flashing.
        for(int index=ungroundedSpawns.Count-1;index>=0;index--)
        {
            var site=ungroundedSpawns[index];
            if(!SpawnGroundGeometry.TryCreate(site.Entry.Location.Position,Ground,out var mark)) continue;
            for(int i=0;i<4;i++) spawnMarkerQueue.Enqueue(new(mark.Corners[i],mark.Corners[(i+1)%4],null,mark.TextAngles));
            // The label's plane faces the ground normal and never billboards.
            spawnMarkerQueue.Enqueue(new(mark.Center,default,site.Entry.Number.ToString(),mark.TextAngles));
            ungroundedSpawns.RemoveAt(index);
        }
        spawnMarkerRetryAt=ungroundedSpawns.Count>0 ? Server.CurrentTime+5 : 0;
    }

    private void TickSpawnMarkers()
    {
        if(!loaded || !Practice || spawnMarkerQueue.Count==0) return;
        var watch=Stopwatch.StartNew();
        try
        {
            for(int i=0;i<2 && watch.Elapsed.TotalMilliseconds<.35 && spawnMarkerQueue.TryDequeue(out var draw);i++)
            {
                if(draw.Text is null)
                {
                    var beam=CreatePreviewBeam(draw.Start,draw.End,SpawnGreen,.8f,b=>spawnMarkers.Add(new(b,b.EntityHandle.Raw)));
                    if(beam is null) throw new InvalidOperationException("Spawn border entity could not be created.");
                }
                else
                {
                    var text=Utilities.CreateEntityByName<CPointWorldText>("point_worldtext") ?? throw new InvalidOperationException("Spawn label entity could not be created.");
                    spawnMarkers.Add(new(text,text.EntityHandle.Raw));
                    using(var keys=new CEntityKeyValues())
                    {
                        keys.SetString("message",draw.Text);keys.SetString("font_name","Arial Black");keys.SetString("color","70 245 65 225");
                        keys.SetInt("enabled",1);keys.SetInt("fullbright",1);keys.SetInt("font_size",80);keys.SetFloat("world_units_per_pixel",.35f);
                        keys.SetInt("justify_horizontal",1);keys.SetInt("justify_vertical",1);keys.SetInt("reorient_mode",0);keys.SetFloat("depth_render_offset",.125f);
                        text.DispatchSpawn(keys);
                    }
                    text.Teleport(position:draw.Start,angles:draw.Angles,velocity:Vector3.Zero);
                }
                spawnMarkersCreated++;
            }
        }
        catch(Exception ex)
        {ClearSpawnMarkers();spawnMarkerRetryAt=Server.CurrentTime+10;Logger.LogWarning(ex,"Could not create ground spawn markers; retry is rate limited");}
    }

    private void ClearSpawnMarkers()
    {
        foreach(var marker in spawnMarkers)
            if(marker.Entity.IsValid && marker.Entity.EntityHandle.Raw==marker.Handle) marker.Entity.Remove();
        spawnMarkers.Clear();spawnMarkerQueue.Clear();ungroundedSpawns.Clear();spawnMarkerKey=null;spawnMarkerRetryAt=0;
    }
}
