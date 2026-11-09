using System.Numerics;
using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private static int DummyAnimationTick(CCSPlayerPawn pawn)=>pawn.CBodyComponent?.As<CBodyComponentBaseAnimGraph>().AnimationController.PrevAnimUpdateTick ?? -1;
    private static string DummyAnimationState(CCSPlayerPawn pawn)
    {
        var graph=pawn.CBodyComponent?.As<CBodyComponentBaseAnimGraph>().AnimationController;
        return graph is null ? "graph=missing" : $"animTick={graph.PrevAnimUpdateTick}; recipe={graph.SerializePoseRecipeVersionAG2}; iteration={graph.ServerGraphInstanceIteration}; skipped={graph.LastUpdateSkipped}; sequence={graph.Sequence}; seqStart={graph.SeqStartTime}; rate={graph.PlaybackRate}; graphEnabled={pawn.AnimGraphUpdateEnabled}";
    }
    private void RegisterDummyFlashDiagnostic()=>AddCommand("css_nl_dummyflashcheck","Empty-server real flash and dummy animation diagnostic",(caller,command)=>
    {
        if(caller is not null || !loaded || !Practice || !BenchmarkIdle || Utilities.GetPlayers().Any(p=>!p.IsBot))
        {Say(null,"DUMMYFLASH refused: empty idle practice server required.");return;}
        float pitch=0,yaw=0;
        if(command.ArgCount!=1 && (command.ArgCount!=3 ||
            !float.TryParse(command.ArgByIndex(1),NumberStyles.Float,CultureInfo.InvariantCulture,out pitch) || !float.IsFinite(pitch) || Math.Abs(pitch)>80 ||
            !float.TryParse(command.ArgByIndex(2),NumberStyles.Float,CultureInfo.InvariantCulture,out yaw) || !float.IsFinite(yaw) || Math.Abs(yaw)>180))
        {Say(null,"DUMMYFLASH refused: optional pitch [-80,80] and yaw [-180,180] expected.");return;}
        var owner=Utilities.GetPlayers().FirstOrDefault(p=>p.IsBot && p.PlayerPawn.Value is {IsValid:true,LifeState:0,AbsOrigin:not null});
        if(owner?.PlayerPawn.Value is not {IsValid:true,AbsOrigin:not null} thrower || FindDummyDiagnosticSite(NativeWorld.FromNative(thrower.AbsOrigin)) is not { } feet)
        {Say(null,"DUMMYFLASH refused: living seed bot and safe floor required.");return;}
        int epoch=grenadeEpoch,phase=0,passed=0,initialAnimationTick=-1;uint handle=owner.EntityHandle.Raw,throwerHandle=thrower.EntityHandle.Raw;
        var oldMove=thrower.MoveType;var oldActual=thrower.ActualMoveType;
        bool oldActive=thrower.BotAllowActive,oldAi=thrower.Bot?.AllowActive ?? false;
        string? oldStop=ConVar.Find("bot_stop")?.StringValue;
        float deadline=Server.CurrentTime+20,release=0,finish=0;EffectDummy? target=null;CBaseCSGrenadeProjectile? grenade=null;
        var pose=new EffectPose(feet,64,new(pitch,yaw,ThrowMode.Left),100);
        int facingSamples=0,maxViewCorrections=0;float maxEyeError=0,maxBodyError=0,maxNativeViewError=0;
        void SampleFacing()
        {
            if(target is not {Ready:true,Removing:false} || !target.Live || target.Bot.PlayerPawn.Value is not {IsValid:true,LifeState:0} pawn) return;
            facingSamples++;
            maxViewCorrections=Math.Max(maxViewCorrections,pawn.ServerViewAngleChanges.Count);
            maxEyeError=Math.Max(maxEyeError,Math.Max(Math.Abs(pawn.EyeAngles.X-pose.View.Pitch),Math.Abs(Geometry.NormalizeYaw(pawn.EyeAngles.Y-pose.View.Yaw))));
            var body=pawn.AbsRotation;var local=pawn.CBodyComponent?.SceneNode?.Rotation;
            maxBodyError=Math.Max(maxBodyError,Math.Max(Math.Abs(body?.X ?? float.NaN),Math.Max(Math.Abs(body?.Z ?? float.NaN),Math.Abs(Geometry.NormalizeYaw((body?.Y ?? float.NaN)-pose.View.Yaw)))));
            if(local is not null) maxBodyError=Math.Max(maxBodyError,Math.Max(Math.Abs(local.X),Math.Max(Math.Abs(local.Z),Math.Abs(Geometry.NormalizeYaw(local.Y-pose.View.Yaw)))));
            maxNativeViewError=Math.Max(maxNativeViewError,Math.Max(Math.Abs(pawn.V_angle.X-pose.View.Pitch),Math.Abs(Geometry.NormalizeYaw(pawn.V_angle.Y-pose.View.Yaw))));
        }
        void Require(bool ok,string message) {if(!ok) throw new InvalidOperationException(message);passed++;Say(null,"DUMMYFLASH PASS: "+message);}
        void Done(string? error=null)
        {
            dummyCheckPending=false;
            RemoveListener<Listeners.OnServerPostEntityThink>(SampleFacing);
            Say(null,$"DUMMYFLASH FACING: pitch={pose.View.Pitch}; yaw={pose.View.Yaw}; samples={facingSamples}; maxEyeError={maxEyeError}; maxBodyError={maxBodyError}; maxNativeViewError={maxNativeViewError}; maxViewCorrections={maxViewCorrections}");
            if(epoch==grenadeEpoch)
            {
                if(grenade is {IsValid:true}) grenade.Remove();
                if(dummyCreation is { } pending && pending.Owner==handle && pending.Epoch==epoch) pending.Cancelled=true;
                foreach(var own in effectDummies.Values.Where(d=>d.Owner==handle).ToArray()) RemoveDummy(own);
                if(thrower.IsValid && thrower.EntityHandle.Raw==throwerHandle) {thrower.BotAllowActive=oldActive;if(thrower.Bot is { } ai) ai.AllowActive=oldAi;SetMovement(thrower,oldMove,oldActual);}
                if(oldStop is not null && ConVar.Find("bot_stop") is { } stop) stop.StringValue=oldStop;
            }
            Say(null,$"DUMMYFLASH DONE {(error is null ? "PASS" : "FAIL")}: passed={passed}; error={error ?? "none"}; client pose still requires visual confirmation.");
        }
        void Sample(string stage,CCSPlayerPawn pawn)=>Say(null,$"DUMMYFLASH {stage}: health={pawn.Health}; armor={pawn.ArmorValue}; duration={pawn.FlashDuration}; alpha={pawn.FlashMaxAlpha}; start={pawn.BlindStartTime}; until={pawn.BlindUntilTime}; now={Server.CurrentTime}; pawnUpdate={pawn.BotAllowActive}; ai={pawn.Bot?.AllowActive}; drift={(target is null ? 0 : Vector3.Distance(NativeWorld.FromNative(pawn.AbsOrigin!),target.PositionedFeet))}; viewCorrections={pawn.ServerViewAngleChanges.Count}; priorView={pawn.V_anglePrevious}; {DummyAnimationState(pawn)}");
        void Step()
        {
            try
            {
                if(!loaded || !Practice || grenadeEpoch!=epoch || Utilities.GetPlayers().Any(p=>!p.IsBot) || !owner.IsValid || owner.EntityHandle.Raw!=handle)
                    throw new InvalidOperationException("world/owner/zero-human guard changed");
                if(Server.CurrentTime>deadline) throw new InvalidOperationException($"phase {phase} timed out");
                if(phase==0 && !creatingDummy && SelectedDummy(owner) is {Ready:true} ready)
                {
                    target=ready;initialAnimationTick=DummyAnimationTick(ready.Bot.PlayerPawn.Value!);Sample("BEFORE",ready.Bot.PlayerPawn.Value!);
                    owner.RemoveWeapons();owner.GiveNamedItem("weapon_flashbang");phase=1;
                }
                else if(phase==1 && thrower.WeaponServices?.MyWeapons.Select(w=>w.Value).FirstOrDefault(w=>w is {IsValid:true,DesignerName:"weapon_flashbang"})?.As<CBaseCSGrenade>() is { } weapon)
                {
                    thrower.WeaponServices.ActiveWeapon.Raw=weapon.EntityHandle.Raw;
                    ConVar.Find("bot_stop")!.StringValue="1";thrower.BotAllowActive=true;if(thrower.Bot is { } ai) ai.AllowActive=true;
                    release=Server.CurrentTime+1;phase=2;
                }
                else if(phase==2 && Server.CurrentTime>=release)
                {
                    var held=thrower.WeaponServices!.ActiveWeapon.Value!.As<CBaseCSGrenade>();
                    held.PinPulled=false;held.Redraw=false;held.ThrowStrength=0;held.IsHeldByPlayer=true;
                    release=Server.CurrentTime;held.ThrowTime=release+.1f;phase=3;
                }
                else if(phase==3)
                {
                    grenade=Utilities.FindAllEntitiesByDesignerName<CBaseCSGrenadeProjectile>("flashbang_projectile")
                        .FirstOrDefault(g=>g.IsValid && g.Thrower.Value?.EntityHandle.Raw==thrower.EntityHandle.Raw && g.CreateTime>=release-.1f);
                    if(grenade is not null)
                    {
                        // Isolate flash from impact damage: hold the real projectile
                        // clear of the player's hull while its native fuse runs.
                        ConVar.Find("bot_stop")!.StringValue="0";thrower.BotAllowActive=false;if(thrower.Bot is { } ai) ai.AllowActive=false;
                        var eye=target!.PositionedFeet+new Vector3(0,0,target.Bot.PlayerPawn.Value!.ViewOffset.Z);
                        var world=new NativeWorld(target.Bot.PlayerPawn.Value);
                        float heading=pose.View.Yaw*MathF.PI/180;
                        var forward=new Vector3(MathF.Cos(heading),MathF.Sin(heading),0);
                        Vector3? source=null;
                        foreach(var offset in new[]{forward*64,forward*40+Vector3.UnitZ*48,Vector3.UnitZ*64})
                            if(!world.Sweep(eye,eye+offset,2).Hit) {source=eye+offset;break;}
                        if(source is not { } point) throw new InvalidOperationException("no clear flash source outside the diagnostic target hull");
                        grenade.MoveType=MoveType_t.MOVETYPE_NONE;grenade.ActualMoveType=MoveType_t.MOVETYPE_NONE;
                        grenade.Teleport(position:point,velocity:Vector3.Zero);
                        Say(null,$"DUMMYFLASH SOURCE: distance={Vector3.Distance(eye,point)}; health={target.Bot.PlayerPawn.Value.Health}; nativeFuse={grenade.DetonateTime-Server.CurrentTime}");phase=4;
                    }
                }
                else if(phase==4 && target is {HasBlindMeasurement:true})
                {
                    var pawn=target.Bot.PlayerPawn.Value!;
                    Require(target.LastBlindDuration>0,"real engine flash produced measured blindness");Sample("BLINDED",pawn);
                    Require(pawn.BotAllowActive,"real blindness opens pawn updates");
                    finish=Math.Max(target.FlashAnimationUntil,Server.CurrentTime+Math.Clamp(target.LastBlindDuration,0,15))+1;deadline=finish+3;phase=5;
                }
                else if(phase==5)
                {
                    var pawn=target!.Bot.PlayerPawn.Value!;
                    if(Server.CurrentTime<finish) {Sample("DURING",pawn);AddTimer(.5f,Step);return;}
                    Sample("AFTER",pawn);
                    Require(initialAnimationTick>=0 && DummyAnimationTick(pawn)>initialAnimationTick+32,"native animation actually advanced during real flash");
                    Require(Vector3.Distance(NativeWorld.FromNative(pawn.AbsOrigin!),target.PositionedFeet)<1,"flash did not move the target");
                    Require(facingSamples>=64 && maxEyeError<1,"recorded eye direction retained throughout the flash");
                    Require(maxBodyError<1,"body facing retained throughout the flash");
                    Require(maxNativeViewError<1,"native view direction retained throughout the flash");
                    Require(pawn.Health==100 && pawn.ArmorValue==100,$"flash did not change measured health or armor (hp={pawn.Health}, armor={pawn.ArmorValue})");
                    Require(pawn.Bot?.AllowActive==false && pawn.WeaponServices?.MyWeapons.Count==0,"target remains unarmed without tactical AI");
                    Require(!pawn.BotAllowActive && Server.CurrentTime>=pawn.BlindUntilTime,"native recovery completed and temporary pawn updates ended");
                    ResetDummy(target);Require(pawn.BlindUntilTime==0 && pawn.FlashDuration==0 && !target.HasBlindMeasurement && target.FlashAnimationUntil==0,"reset clears flash state and animation window");
                    Done();return;
                }
                AddTimer(.1f,Step);
            }
            catch(Exception ex) {Done(ex.Message);}
        }
        RegisterListener<Listeners.OnServerPostEntityThink>(SampleFacing);
        dummyCheckPending=true;thrower.BotAllowActive=false;if(thrower.Bot is { } ai) ai.AllowActive=false;
        SetMovement(thrower,MoveType_t.MOVETYPE_NONE,MoveType_t.MOVETYPE_NONE);
        PlaceEffectDummyCore(owner,pose);AddTimer(.1f,Step);Say(null,"DUMMYFLASH START: native flash projectile, pose state, expiry, reset and cleanup.");
    });
}
