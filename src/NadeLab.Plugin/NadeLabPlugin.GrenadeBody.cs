using System.Drawing;
using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    // Verified in the installed pak01_dir.vpk, rather than guessed Source 1 paths.
    private const string FlashBodyModel="weapons/models/grenade/flashbang/weapon_flashbang.vmdl";
    private const string HeBodyModel="weapons/models/grenade/hegrenade/weapon_hegrenade.vmdl";
    // Official game/core/base.fgd defines info_target as an invisible position
    // entity, with flag 2 = always transmit. ViewEntity accepts CBaseEntity;
    // it needs a networked transform, not a prop or a model/physics resource.
    private const string BodyCameraClass="info_target";
    private const uint BodyCameraSpawnFlags=2u;
    private const uint BodyHiddenBits=(uint)(EntityEffects_t.EF_NODRAW|EntityEffects_t.EF_NODRAW_BUT_TRANSMIT);
    private sealed record BodyWeaponState(uint Handle,RenderMode_t RenderMode,Color Render,uint Effects);
    private sealed class GrenadeBodyState
    {
        public required int Slot,Epoch;
        public required ulong Steam;
        public required uint Controller,Pawn,CameraHandle;
        public required GrenadeKind Kind;
        public required string Model;
        public required ulong MeshMask;
        public required Vector3 ViewOffset;
        public required uint ViewEntity,Effects;
        public required RenderMode_t RenderMode;
        public required Color Render;
        public required bool PreventPickup;
        public required bool DisableAutoDeploy;
        public required uint ActiveWeapon;
        public required CInfoTarget Camera;
        public required NativeWorld World;
        public readonly Dictionary<uint,BodyWeaponState> Weapons=[];
        public bool ThirdPerson;
        public bool PawnModelChanged;
        public float NextFlightNotice;
        public float NextStationaryCameraTrace;
        public Vector3? LastCenter,LastInput;
        public bool LastThirdPerson;
        public Vector3? LastCameraPosition,LastCameraAngles;
    }
    private readonly Dictionary<int,GrenadeBodyState> grenadeBodies=[];
    private bool grenadeBodyRegistered;
    private int lastGrenadeBodyTick=-1;

    private void RegisterGrenadeBody()
    {
        if(grenadeBodyRegistered) return;
        grenadeBodyRegistered=true;
        RegisterListener<Listeners.OnServerPrecacheResources>(manifest=>
        {manifest.AddResource(FlashBodyModel);manifest.AddResource(HeBodyModel);});
        // Movement can rewrite the standing view offset during entity think.
        // Keep the camera at the grenade centre after that update, once per tick.
        RegisterListener<Listeners.OnServerPostEntityThink>(TickGrenadeBodies);
        RegisterListener<Listeners.OnServerPreEntityThink>(KeepGrenadeBodiesFlying);
        // V remains the user's native noclip bind outside placement. During
        // grenade placement WALK would put a human hull/ground logic at a 2u
        // grenade centre; prevent that transition before the engine runs it.
        AddCommandListener("noclip",(player,command)=>GuardGrenadeBodyNoclip(player),HookMode.Pre);
        RegisterEventHandler<EventPlayerDeath>((ev,info)=>
        {if(ev.Userid is { } player) RestoreGrenadeBody(player.Slot);return HookResult.Continue;});
        RegisterListener<Listeners.CheckTransmit>(infos=>
        {
            if(grenadeBodies.Count==0) return;
            foreach(var (info,viewer) in infos)
                foreach(var state in grenadeBodies.Values)
                {
                    if(state.Epoch!=grenadeEpoch) continue;
                    if(BodyCamera(state) is {IsValid:true} camera &&
                        (viewer?.IsValid!=true || viewer.Slot!=state.Slot || viewer.SteamID!=state.Steam))
                        info.TransmitEntities.Remove(camera);
                    // Rendering flags alone do not cover the client-generated
                    // held/holstered models. Withhold the owned inventory from
                    // every viewer while its owner is a grenade, including the
                    // owner's predicted first-person hand/weapon attachments.
                    foreach(uint raw in state.Weapons.Keys)
                        if(new CHandle<CBasePlayerWeapon>(raw).Value is {IsValid:true} weapon && BodyOwnsWeapon(state,weapon))
                            info.TransmitEntities.Remove(weapon);
                }
        });
        AddPlayerCommand("css_nl_camera","Toggle first/third person while placing a flash or HE",(p,c)=>ToggleGrenadeBodyCamera(p));
    }

    // Caller saves the original standing position FIRST, enters noclip, and then
    // moves AbsOrigin to the intended grenade centre (normally the original eye).
    // This module never changes the pawn's position or its collision dimensions.
    private bool BeginGrenadeBody(CCSPlayerController player,CCSPlayerPawn pawn,GrenadeKind kind,out string reason)
    {
        reason="";
        if(!Practice || !kind.Timed() || !player.IsValid || !pawn.IsValid || pawn.LifeState!=0 ||
            player.PlayerPawn.Raw!=pawn.EntityHandle.Raw || pawn.CameraServices is null)
        {reason="当前角色或训练条件无法进入道具本体模式。";return false;}
        if(grenadeBodies.ContainsKey(player.Slot))
        {reason="上一次道具本体状态尚未退出。";return false;}
        CInfoTarget? camera=null;
        uint cameraHandle=uint.MaxValue;
        try
        {
            var modelState=pawn.CBodyComponent?.SceneNode?.GetSkeletonInstance().ModelState;
            string? original=modelState?.ModelName;
            if(string.IsNullOrWhiteSpace(original)) {reason="无法保存原角色模型，已保留原状态。";return false;}
            var eyeOffset=new Vector3(pawn.ViewOffset.X,pawn.ViewOffset.Y,pawn.ViewOffset.Z);
            if(!Geometry.Finite(eyeOffset)) {reason="原视点数据无效，已保留原状态。";return false;}
            string bodyModel=kind==GrenadeKind.Flash ? FlashBodyModel : HeBodyModel;
            // OnServerPrecacheResources covers normal map load; this API also
            // covers a plugin hot reload after the map's manifest was built.
            Server.PrecacheModel(bodyModel);
            camera=Utilities.CreateEntityByName<CInfoTarget>(BodyCameraClass);
            if(camera is null || !camera.IsValid) {reason="无法创建观察镜头，已保留原状态。";return false;}
            cameraHandle=camera.EntityHandle.Raw;
            var state=new GrenadeBodyState
            {
                Slot=player.Slot,Epoch=grenadeEpoch,Steam=player.SteamID,Controller=player.EntityHandle.Raw,Pawn=pawn.EntityHandle.Raw,CameraHandle=cameraHandle,Kind=kind,
                Model=original,MeshMask=modelState!.MeshGroupMask,ViewOffset=eyeOffset,ViewEntity=pawn.CameraServices.ViewEntity.Raw,
                Effects=pawn.Effects,RenderMode=pawn.RenderMode,Render=pawn.Render,
                PreventPickup=pawn.WeaponServices?.PreventWeaponPickup ?? false,
                DisableAutoDeploy=pawn.WeaponServices?.As<CCSPlayer_WeaponServices>().DisableAutoDeploy ?? false,
                ActiveWeapon=pawn.WeaponServices?.ActiveWeapon.Raw ?? uint.MaxValue,Camera=camera,World=new NativeWorld(pawn)
            };
            // Track ownership before spawn or any reversible player mutation.
            grenadeBodies.Add(player.Slot,state);
            // No model is attached to the camera, before or after spawn.
            // Apart from avoiding staging SetModel assertions, this prevents
            // propdata validation from deleting an otherwise valid camera.
            using(var keys=new CEntityKeyValues())
            {
                keys.SetString("classname",BodyCameraClass);
                keys.SetUInt("spawnflags",BodyCameraSpawnFlags);
                camera.DispatchSpawn(keys);
            }
            // Resolve again after native spawn: a rejected entity can have
            // removed itself. Never keep using its pre-spawn native pointer.
            camera=BodyCamera(state) ?? throw new InvalidOperationException("Camera was removed during native spawn.");
            if(!camera.IsValid || camera.DesignerName!=BodyCameraClass || camera.CBodyComponent?.SceneNode is null)
                throw new InvalidOperationException("Native camera has no surviving networked point transform after spawn.");
            state.Camera=camera;
            camera.MoveType=MoveType_t.MOVETYPE_NONE;camera.ActualMoveType=MoveType_t.MOVETYPE_NONE;
            state.PawnModelChanged=true;
            pawn.SetModel(bodyModel);
            var newModel=pawn.CBodyComponent?.SceneNode?.GetSkeletonInstance().ModelState;
            if(newModel is null || !string.Equals(newModel.ModelName,bodyModel,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Native pawn model did not adopt the requested grenade model.");
            SetBodyEyeOffset(pawn,Vector3.Zero);
            HideBodyWeapons(pawn,state);
            ApplyBodyVisibility(pawn,state);
            UpdateBodyCamera(pawn,state,true);
            SetBodyViewEntity(pawn,cameraHandle);
            return true;
        }
        catch(Exception ex)
        {
            RestoreGrenadeBody(player.Slot);
            if(new CHandle<CInfoTarget>(cameraHandle).Value is {IsValid:true} remainingCamera) remainingCamera.Remove();
            Logger.LogWarning(ex,"Could not enter grenade body mode for slot {Slot}",player.Slot);
            reason="道具本体或镜头未能建立，已尝试恢复原角色状态。";return false;
        }
    }

    private bool GrenadeBodyActive(CCSPlayerPawn pawn)=>grenadeBodies.Values.Any(s=>s.Epoch==grenadeEpoch && s.Pawn==pawn.EntityHandle.Raw);

    private void ToggleGrenadeBodyCamera(CCSPlayerController? player)
    {
        if(!Check(player,out var pawn,allowPlacement:true)) return;
        ToggleGrenadeBodyCameraCore(player!,pawn);
    }

    // The server-only, zero-human diagnostic can call this shared operation on
    // its owned bot. Player commands still pass through Check above.
    private void ToggleGrenadeBodyCameraCore(CCSPlayerController player,CCSPlayerPawn pawn)
    {
        if(!grenadeBodies.TryGetValue(player.Slot,out var state) || !SameGrenadeBody(player,pawn,state))
        {Say(player,"切换镜头仅适用于闪光或高爆手雷的道具定位模式；在定位菜单选择视角。");return;}
        try
        {
            state.ThirdPerson=!state.ThirdPerson;
            ApplyBodyVisibility(pawn,state);UpdateBodyCamera(pawn,state,true);
            Say(player,state.ThirdPerson ? "第三人称：当前道具本体位置就是爆点，在定位菜单切回第一人称。" : "第一人称：镜头位于道具中心，在定位菜单切换为从外部观察道具。");
        }
        catch(Exception ex)
        {Logger.LogWarning(ex,"Grenade body camera toggle failed");AbortGrenadeBody(player,state,"镜头切换失败，已取消道具定位。");}
    }

    private bool SameGrenadeBody(CCSPlayerController player,CCSPlayerPawn pawn,GrenadeBodyState state)=>
        state.Epoch==grenadeEpoch && player.IsValid && player.SteamID==state.Steam && player.EntityHandle.Raw==state.Controller &&
        pawn.IsValid && pawn.EntityHandle.Raw==state.Pawn && player.PlayerPawn.Raw==state.Pawn;

    private static CInfoTarget? BodyCamera(GrenadeBodyState state)=>new CHandle<CInfoTarget>(state.CameraHandle).Value;

    private HookResult GuardGrenadeBodyNoclip(CCSPlayerController? player)
    {
        if(player?.PlayerPawn.Value is not {IsValid:true,LifeState:0} pawn ||
            !grenadeBodies.TryGetValue(player.Slot,out var state) || !SameGrenadeBody(player,pawn,state) ||
            !sessions.TryGetValue(player.Slot,out var session) || session.Placement is null) return HookResult.Continue;
        if(Server.CurrentTime>=state.NextFlightNotice)
        {state.NextFlightNotice=Server.CurrentTime+2;Say(player,"道具定位中保持飞行；使用键确认返回，换弹键取消后可从菜单切换飞行。");}
        return HookResult.Handled;
    }

    private void KeepGrenadeBodiesFlying()
    {
        if(!loaded || !Practice) return;
        foreach(var (slot,state) in grenadeBodies.ToArray())
        {
            var player=Utilities.GetPlayerFromSlot(slot);var pawn=player?.PlayerPawn.Value;
            if(player is null || pawn is not {IsValid:true,LifeState:0} || !SameGrenadeBody(player,pawn,state) ||
                !sessions.TryGetValue(slot,out var session) || session.Placement is null) continue;
            // Also cover movement changed by a plugin or engine path that did
            // not pass through the noclip console-command listener.
            try
            {
                if((pawn.MoveType!=MoveType_t.MOVETYPE_NOCLIP || pawn.ActualMoveType!=MoveType_t.MOVETYPE_NOCLIP))
                {SetMovement(pawn,MoveType_t.MOVETYPE_NOCLIP,MoveType_t.MOVETYPE_NOCLIP);pawn.Teleport(velocity:Vector3.Zero);}
                HideBodyWeapons(pawn,state);
            }
            catch(Exception ex)
            {Logger.LogWarning(ex,"Could not keep grenade inventory stowed for slot {Slot}",slot);AbortGrenadeBody(player,state,"装备隐藏失效，已取消道具定位并恢复装备。");}
        }
    }

    private void TickGrenadeBodies()
    {
        if(grenadeBodies.Count==0 || lastGrenadeBodyTick==Server.TickCount) return;
        lastGrenadeBodyTick=Server.TickCount;
        foreach(var (slot,state) in grenadeBodies.ToArray())
        {
            if(state.Epoch!=grenadeEpoch) {grenadeBodies.Remove(slot);continue;}
            var player=Utilities.GetPlayerFromSlot(slot);var pawn=player?.PlayerPawn.Value;
            if(!loaded || !Practice || player is null || pawn is null || !SameGrenadeBody(player,pawn,state) || pawn.LifeState!=0 ||
                !sessions.TryGetValue(slot,out var session) || session.Placement is null || session.Kind!=state.Kind)
            {RestoreGrenadeBody(slot);continue;}
            if(BodyCamera(state) is not {IsValid:true})
            {AbortGrenadeBody(player,state,"观察镜头已失效，已取消道具定位。");continue;}
            try
            {
                SetBodyEyeOffset(pawn,Vector3.Zero);
                if(pawn.CameraServices is null) throw new InvalidOperationException("Player camera services disappeared.");
                if(pawn.CameraServices.ViewEntity.Raw!=state.CameraHandle) SetBodyViewEntity(pawn,state.CameraHandle);
                // The engine may deploy/reset weapon rendering during think.
                // Repair after think as well; only changed fields are dirtied.
                HideBodyWeapons(pawn,state);
                UpdateBodyCamera(pawn,state,false);
            }
            catch(Exception ex)
            {Logger.LogWarning(ex,"Grenade body update failed for slot {Slot}",slot);AbortGrenadeBody(player,state,"道具视点更新失败，已取消定位。");}
        }
    }

    private void AbortGrenadeBody(CCSPlayerController player,GrenadeBodyState state,string reason)
    {
        var pawn=player.PlayerPawn.Value;
        if(pawn is null || !SameGrenadeBody(player,pawn,state)) {RestoreGrenadeBody(state.Slot);return;}
        var center=pawn.AbsOrigin is { } origin ? NativeWorld.FromNative(origin) : (Vector3?)null;
        try {FinishPlacement(player,false);}
        catch(Exception ex) {Logger.LogWarning(ex,"Could not return after grenade camera failure for slot {Slot}",state.Slot);}
        // A blocked return must not leave a placement draft alive after its
        // native body/camera is gone. Leave ordinary noclip with its eye at the
        // last grenade centre instead; no new target is committed.
        if(sessions.TryGetValue(state.Slot,out var session) && session.SteamId==state.Steam &&
            session.Placement is { } draft && draft.Pawn==state.Pawn && draft.Epoch==state.Epoch)
        {
            RestoreGrenadeBody(state.Slot);
            AbandonPlacement(state.Slot,session);
            if(pawn.LifeState==0 && SameGrenadeBody(player,pawn,state) && center is { } point && Geometry.Finite(point))
            {
                SetMovement(pawn,MoveType_t.MOVETYPE_NOCLIP,MoveType_t.MOVETYPE_NOCLIP);
                pawn.Teleport(position:point-state.ViewOffset,velocity:Vector3.Zero);
            }
        }
        else RestoreGrenadeBody(state.Slot);
        Say(player,reason);
    }

    private static void SetBodyEyeOffset(CCSPlayerPawn pawn,Vector3 offset)
    {
        // m_vecViewOffset is a networked embedded object, not a Vector leaf.
        // Notifying its root offset produces "Couldn't resolve offset 2072"
        // every movement tick. Notify the actual changed scalar leaf instead.
        void Changed(string field)=>Utilities.SetStateChanged(pawn,"CBaseModelEntity","m_vecViewOffset",
            Schema.GetSchemaOffset("CNetworkViewOffsetVector",field));
        if(pawn.ViewOffset.X!=offset.X) {pawn.ViewOffset.X=offset.X;Changed("m_vecX");}
        if(pawn.ViewOffset.Y!=offset.Y) {pawn.ViewOffset.Y=offset.Y;Changed("m_vecY");}
        if(pawn.ViewOffset.Z!=offset.Z) {pawn.ViewOffset.Z=offset.Z;Changed("m_vecZ");}
    }
    private static void SetBodyViewEntity(CCSPlayerPawn pawn,uint raw)
    {
        if(pawn.CameraServices is not { } camera) return;
        camera.ViewEntity.Raw=raw;
        MarkBodyComponent(camera,"CPlayer_CameraServices","m_hViewEntity");
    }
    private static void ApplyBodyVisibility(CCSPlayerPawn pawn,GrenadeBodyState state)
    {
        // Keep the pawn transmitted for movement prediction. Only its rendering
        // is hidden in first person, so the camera is not inside an opaque shell.
        pawn.RenderMode=state.ThirdPerson ? RenderMode_t.kRenderNormal : RenderMode_t.kRenderNone;
        pawn.Render=Color.White;
        pawn.Effects=(state.Effects&~BodyHiddenBits)|(state.ThirdPerson ? 0 : (uint)EntityEffects_t.EF_NODRAW_BUT_TRANSMIT);
        MarkBodyRendering(pawn);
    }
    private static void MarkBodyRendering(CBaseModelEntity entity)
    {
        Utilities.SetStateChanged(entity,"CBaseModelEntity","m_nRenderMode");
        Utilities.SetStateChanged(entity,"CBaseModelEntity","m_clrRender");
        Utilities.SetStateChanged(entity,"CBaseEntity","m_fEffects");
    }
    private static void HideBodyWeapons(CCSPlayerPawn pawn,GrenadeBodyState state)
    {
        if(pawn.WeaponServices is not { } weapons) return;
        weapons.PreventWeaponPickup=true;
        weapons.As<CCSPlayer_WeaponServices>().DisableAutoDeploy=true;
        SetBodyActiveWeapon(weapons,uint.MaxValue);
        foreach(var handle in weapons.MyWeapons)
        {
            if(handle.Value is not {IsValid:true} weapon || !BodyOwnsWeapon(state,weapon)) continue;
            state.Weapons.TryAdd(handle.Raw,new(handle.Raw,weapon.RenderMode,weapon.Render,weapon.Effects));
            if(weapon.RenderMode!=RenderMode_t.kRenderNone || (weapon.Effects&BodyHiddenBits)!=BodyHiddenBits)
            {
                weapon.RenderMode=RenderMode_t.kRenderNone;weapon.Effects|=BodyHiddenBits;
                MarkBodyRendering(weapon);
            }
        }
        // A death, admin operation or drop can transfer a weapon. Restore it
        // promptly and stop filtering it, rather than hiding the new owner's gun.
        foreach(var saved in state.Weapons.Values.ToArray())
            if(new CHandle<CBasePlayerWeapon>(saved.Handle).Value is not {IsValid:true} weapon)
                state.Weapons.Remove(saved.Handle);
            else if(!BodyOwnsWeapon(state,weapon))
            {RestoreBodyWeapon(weapon,saved);state.Weapons.Remove(saved.Handle);}
    }
    private static bool BodyOwnsWeapon(GrenadeBodyState state,CBasePlayerWeapon weapon)=>weapon.OwnerEntity.Raw==state.Pawn;
    private static void MarkBodyComponent(CPlayerPawnComponent component,string className,string field)
    {
        // Pawn components have their own network chainer; passing the pawn's
        // m_pWeaponServices pointer field does not identify the active handle.
        NativeAPI.SchemaNetworkStateChanged(component.__m_pChainEntity.Handle,
            (uint)Schema.GetSchemaOffset(className,field),uint.MaxValue,uint.MaxValue);
    }
    private static void SetBodyActiveWeapon(CPlayer_WeaponServices weapons,uint raw)
    {
        if(weapons.ActiveWeapon.Raw==raw) return;
        weapons.ActiveWeapon.Raw=raw;
        MarkBodyComponent(weapons,"CPlayer_WeaponServices","m_hActiveWeapon");
    }
    private static void RestoreBodyWeapon(CBasePlayerWeapon weapon,BodyWeaponState saved)
    {
        weapon.RenderMode=saved.RenderMode;weapon.Render=saved.Render;weapon.Effects=saved.Effects;
        MarkBodyRendering(weapon);
    }
    private static void UpdateBodyCamera(CCSPlayerPawn pawn,GrenadeBodyState state,bool force)
    {
        if(pawn.AbsOrigin is not { } origin) throw new InvalidOperationException("Grenade centre is unavailable.");
        var center=NativeWorld.FromNative(origin);
        var input=new Vector3(pawn.V_angle.X,pawn.V_angle.Y,0);
        bool unchanged=state.LastCenter==center && state.LastInput==input && state.LastThirdPerson==state.ThirdPerson;
        // Moving cameras follow every tick. A stationary third-person view only
        // retraces at 20 Hz, to notice moving doors/props without idle trace spam.
        if(!force && unchanged && (!state.ThirdPerson || Server.CurrentTime<state.NextStationaryCameraTrace)) return;
        state.LastCenter=center;state.LastInput=input;state.LastThirdPerson=state.ThirdPerson;
        state.NextStationaryCameraTrace=Server.CurrentTime+.05f;
        var pose=GrenadeBodyCamera.Resolve(state.World,center,input.X,input.Y,state.ThirdPerson);
        if(!force && state.LastCameraPosition==pose.Position && state.LastCameraAngles==pose.Angles) return;
        var camera=BodyCamera(state) ?? throw new InvalidOperationException("Grenade camera entity disappeared.");
        camera.Teleport(position:pose.Position,angles:pose.Angles,velocity:Vector3.Zero);
        state.LastCameraPosition=pose.Position;state.LastCameraAngles=pose.Angles;
    }

    // Restore by full entity handle, never by slot/index alone. A disconnected
    // or dead original pawn can be restored without touching its replacement.
    private void RestoreGrenadeBody(int slot)
    {
        if(!grenadeBodies.Remove(slot,out var state) || state.Epoch!=grenadeEpoch) return;
        void RestorePart(string part,Action restore)
        {try {restore();} catch(Exception ex) {Logger.LogWarning(ex,"Could not restore grenade body {Part} for slot {Slot}",part,slot);}}
        var pawn=new CHandle<CCSPlayerPawn>(state.Pawn).Value;
        if(pawn is {IsValid:true} && pawn.EntityHandle.Raw==state.Pawn)
        {
            RestorePart("camera",()=>
            {
                // A death/observer camera installed by the game takes priority.
                if(pawn.CameraServices?.ViewEntity.Raw==state.CameraHandle)
                {
                    uint original=state.ViewEntity==uint.MaxValue || new CHandle<CBaseEntity>(state.ViewEntity).Value?.IsValid==true ? state.ViewEntity : uint.MaxValue;
                    SetBodyViewEntity(pawn,original);
                }
            });
            if(state.PawnModelChanged)
                RestorePart("model",()=>
                {
                    pawn.SetModel(state.Model);
                    if(pawn.CBodyComponent?.SceneNode?.GetSkeletonInstance().ModelState is { } model) model.MeshGroupMask=state.MeshMask;
                    Utilities.SetStateChanged(pawn,"CBaseEntity","m_CBodyComponent");
                });
            RestorePart("eye offset",()=>SetBodyEyeOffset(pawn,state.ViewOffset));
            RestorePart("rendering",()=>
            {pawn.RenderMode=state.RenderMode;pawn.Render=state.Render;pawn.Effects=state.Effects;MarkBodyRendering(pawn);});
            RestorePart("equipment",()=>
            {
                if(pawn.WeaponServices is not { } weapons) return;
                weapons.PreventWeaponPickup=state.PreventPickup;
                weapons.As<CCSPlayer_WeaponServices>().DisableAutoDeploy=state.DisableAutoDeploy;
                // Restore this exact inventory entity, preserving its ammunition,
                // skin and state. A dropped/removed weapon is never reclaimed.
                if(pawn.LifeState==0 && new CHandle<CBasePlayerWeapon>(state.ActiveWeapon).Value is {IsValid:true} original &&
                    BodyOwnsWeapon(state,original) && weapons.MyWeapons.Any(h=>h.Raw==state.ActiveWeapon))
                    SetBodyActiveWeapon(weapons,state.ActiveWeapon);
            });
        }
        foreach(var weaponState in state.Weapons.Values)
            RestorePart("weapon rendering",()=>
            {
                if(new CHandle<CBasePlayerWeapon>(weaponState.Handle).Value is not {IsValid:true} weapon) return;
                RestoreBodyWeapon(weapon,weaponState);
            });
        RestorePart("camera entity",()=>{if(BodyCamera(state) is {IsValid:true} camera) camera.Remove();});
    }
    private void RestoreGrenadeBodies()
    {foreach(int slot in grenadeBodies.Keys.ToArray()) RestoreGrenadeBody(slot);lastGrenadeBodyTick=-1;}
}
