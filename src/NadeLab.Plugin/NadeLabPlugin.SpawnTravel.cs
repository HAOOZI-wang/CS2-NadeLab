using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private static IReadOnlyList<NumberedSpawn> TrainingSpawns(bool ct)=>SpawnTravel.CompetitiveCatalog(
        Utilities.FindAllEntitiesByDesignerName<SpawnPoint>(ct ? "info_player_counterterrorist" : "info_player_terrorist")
            .Where(e=>e.IsValid && e.AbsOrigin is not null)
            .Select(e=>new SpawnLocation((int)e.Index,NativeWorld.FromNative(e.AbsOrigin!),e.AbsRotation?.Y ?? 0,e.Enabled,e.Priority)));

    private static bool TryTrainingSpawn(CCSPlayerPawn? pawn,IReadOnlyList<NumberedSpawn> catalog,int number,out SpawnLocation spot)
    {
        var world=new NativeWorld(pawn);
        // Map spawns can float above their floor. Validate ground, full standing
        // hull and other players before changing any player/session state.
        var occupied=Utilities.GetPlayers().Select(p=>p.PlayerPawn.Value)
            .Where(p=>p is {IsValid:true,LifeState:0,AbsOrigin:not null} && p.Handle!=pawn?.Handle)
            .Select(p=>NativeWorld.FromNative(p!.AbsOrigin!)).ToArray();
        Vector3? Resolve(SpawnLocation candidate)
        {
            var floor=world.Sweep(candidate.Position+Vector3.UnitZ*18,candidate.Position-Vector3.UnitZ*128,0);
            if(!floor.Hit || floor.StartSolid || floor.Normal.Z<.7f) return null;
            if(!TryStandingPosition(pawn,floor.Position+Vector3.UnitZ*.1f,out var feet,fullHeight:true)) return null;
            if(occupied.Any(p=>MathF.Abs(p.X-feet.X)<34 && MathF.Abs(p.Y-feet.Y)<34 && p.Z<feet.Z+74 && p.Z+74>feet.Z)) return null;
            return feet;
        }
        return SpawnTravel.TryResolve(catalog,number,Resolve,out spot);
    }

    private void OpenSpawnPoints(CCSPlayerController player,bool ct)
    {
        if(!Check(player,out _)) return;
        var catalog=TrainingSpawns(ct);string side=ct ? "CT" : "T";
        var menu=WorkflowPanel(player,$"{side} 竞技出生位 · {catalog.Count}个",out var Add,identity:$"spawns/{side}");
        menu.Back=OpenTargetTools;
        foreach(var entry in catalog)
        {
            int number=entry.Number;var expected=entry.Location;
            Add($"{side}-{number:00}{(!expected.Enabled ? " · 地图未启用" : " · 就位")}",
                who=>TravelToSpawn(who,ct,number,expected),!expected.Enabled,
                hint:"对应常驻绿色地框；保留目标，就位后关闭菜单练习。编号为 NadeLab 编号。",
                key:number.ToString());
        }
        if(catalog.Count==0) Add("地图没有此侧出生位",_=>{},true);
        Add($"查看{(ct ? "T" : "CT")}出生位",who=>OpenSpawnPoints(who,!ct));
        Add("返回目标与站位",OpenTargetTools);
        menu.Open(player);
    }

    private void TravelToSpawn(CCSPlayerController player,bool ct,int number,SpawnLocation expected)
    {
        if(!Check(player,out var pawn)) return;
        var catalog=TrainingSpawns(ct);string label=$"{(ct ? "CT" : "T")}-{number:00}";
        if(number<1 || number>catalog.Count || catalog[number-1].Location!=expected)
        {OpenSpawnPoints(player,ct);Say(player,"出生位已变化，请在新列表重新选择；未移动，原任务保留。");return;}
        if(!TryTrainingSpawn(pawn,catalog,number,out var spot))
        {OpenSpawnPoints(player,ct);Say(player,$"{label} 被占用或无法安全站立；未移动，也不会换到其他编号。");return;}
        var s=Get(player);bool flash=s.FlashMode;
        ClosePanel(player);
        Invalidate(player.Slot,s);s.Start=null;s.RequestedStart=null;s.ReferenceView=null;s.FlashMode=flash;
        s.TimingNotice=null;s.TimingNoticeUntil=0;
        pawn.Teleport(position:spot.Position,angles:new Vector3(0,spot.Yaw,0),velocity:Vector3.Zero);
        SetMovement(pawn,MoveType_t.MOVETYPE_WALK,MoveType_t.MOVETYPE_WALK);
        OpenSpawnPoints(player,ct);
        Say(player,$"已就位 {label}，恢复出生朝向；目标与假人要求保留。关闭菜单练习，或继续选择其他编号。");
    }
    private void SpawnTravelDiagnostic(CCSPlayerController? player,CommandInfo command)
    {
        if(player is not null || !Practice) return;
        foreach(bool ct in new[]{false,true})
        {
            var catalog=TrainingSpawns(ct);int safe=0,disabled=0;
            foreach(var entry in catalog)
            {
                bool found=TryTrainingSpawn(null,catalog,entry.Number,out var spot);
                if(found) safe++;if(!entry.Location.Enabled) disabled++;
                Say(null,$"SPAWNCHECK: map={Server.MapName}; label={(ct ? "CT" : "T")}-{entry.Number:00}; {(found ? "PASS" : "BLOCKED")}; priority={entry.Location.Priority}; entity={entry.Location.Id}; raw={entry.Location.Position}; feet={spot.Position}; yaw={entry.Location.Yaw}.");
            }
            Say(null,$"SPAWNCHECK SUMMARY: side={(ct ? "CT" : "T")}; competitive={catalog.Count}; safe={safe}; disabled={disabled}; read-only, no player moved; markerEntities={spawnMarkers.Count}; pending={spawnMarkerQueue.Count}; ungrounded={ungroundedSpawns.Count}; created={spawnMarkersCreated}.");
        }
    }
}
