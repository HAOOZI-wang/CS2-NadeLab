using System.Numerics;
using System.Text.Json.Nodes;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private uint? measurementPawn;
    private void GrenadeProbe(CCSPlayerController? caller,CommandInfo command)
    {
        if(caller is not null || !Practice || Utilities.GetPlayers().Any(p=>!p.IsBot))
        {Say(caller,"自动校准只允许无人训练服的服务器控制台调用。");return;}
        if(command.ArgCount<2 || !Enum.TryParse<GrenadeKind>(command.ArgByIndex(1),true,out var kind) || !Enum.IsDefined(kind) || kind==GrenadeKind.Smoke)
        {Say(null,"css_nl_probe Flash|HighExplosive|Molotov|Incendiary|Decoy");return;}
        if(grenadeFlights.Count>0 || infernos.Count>0) {Say(null,"等待当前投掷和火区结束后再校准。");return;}
        var bots=Utilities.GetPlayers().Where(p=>p.IsBot && p.PlayerPawn.Value is {IsValid:true,LifeState:0}).ToArray();
        if(bots.Length<2) {Say(null,"校准需要两个存活测试机器人。");return;}
        var owner=bots[0];var dummy=bots[1].PlayerPawn.Value!;
        dummy.BotAllowActive=false;if(dummy.Bot is { } targetAi) targetAi.AllowActive=false;
        measurementPawn=dummy.EntityHandle.Raw;
        uint measuredHandle=dummy.EntityHandle.Raw;
        measurementPose=new(new Vector3(1250,-524,-168),64,new(0,0,ThrowMode.Left),100);
        dummy.TakesDamage=true;dummy.Health=100;dummy.ArmorValue=100;dummy.MoveType=MoveType_t.MOVETYPE_NONE;dummy.ActualMoveType=MoveType_t.MOVETYPE_NONE;
        dummy.Teleport(position:new Vector3(1250,-524,-168),angles:new Vector3(0,0,0),velocity:Vector3.Zero);
        var s=Get(owner);Invalidate(owner.Slot,s);s.Kind=kind;
        owner.PlayerPawn.Value!.Teleport(position:new Vector3(1200,-524,-168),angles:new Vector3(10,180,0),velocity:Vector3.Zero);
        GiveSelectedGrenade(owner,kind);
        var weapon=owner.PlayerPawn.Value.WeaponServices!.MyWeapons.Select(h=>h.Value).FirstOrDefault(w=>w?.IsValid==true && w.DesignerName==kind.Weapon())?.As<CBaseCSGrenade>();
        if(weapon is null) {Say(null,"PROBE inventory settling; retry in one second.");return;}
        owner.PlayerPawn.Value.WeaponServices.ActiveWeapon.Raw=weapon.EntityHandle.Raw;
        s.Start=new(new Vector3(1200,-524,-168));s.RequestedStart=s.Start;
        s.Target=new(new Vector3(1000,-524,-166),128,new Vector3(128,128,64),Airspace:kind.Timed());
        var profile=ProfileFor(s);s.SearchPhysics=profile;
        var candidate=new Candidate(new(10,180,ThrowMode.Left),new(Vector3.Zero,SimulationStatus.Flying,0,0,[]),0);
        BindPreparation(owner,s);
        s.Arm=new(candidate,s.Start.Value,s.Target.Value,s.Revision,Server.CurrentTime+30,true,profile);
        AddTimer(1,()=>
        {
            if(!loaded || !Practice || !weapon.IsValid || Utilities.GetPlayers().Any(p=>!p.IsBot)) {s.Arm=null;measurementPawn=null;return;}
            weapon.PinPulled=false;weapon.Redraw=false;weapon.ThrowStrength=1;weapon.IsHeldByPlayer=true;
            weapon.ThrowTime=Server.CurrentTime+.1f;
            Say(null,$"GRENADE PROBE releasing: {kind}; active={owner.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value?.DesignerName}.");
        });
        AddTimer(25,()=>{if(measurementPawn==measuredHandle) measurementPawn=null;});
        WriteGrenade(new JsonObject {["Kind"]=kind.ToString(),["Event"]="engine-weapon-release-request",["DummySlot"]=bots[1].Slot,
            ["DummyArmor"]=dummy.ArmorValue,["DummyTakesDamage"]=dummy.TakesDamage,["HumanExecutionCalibration"]=false},"grenade-probes.jsonl");
        Say(null,$"GRENADE PROBE scheduled: {kind}; engine weapon release, bot slot={owner.Slot}; target slot={bots[1].Slot}.");
    }
}
