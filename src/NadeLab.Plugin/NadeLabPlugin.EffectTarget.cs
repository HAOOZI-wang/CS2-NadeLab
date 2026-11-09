using System.Numerics;
using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.ValveConstants.Protobuf;
using Microsoft.Extensions.Logging;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private sealed record EffectPose(Vector3 Feet,float EyeHeight,Aim View,int Armor);
    private sealed class EffectDummy(CCSPlayerController bot,DummyCreation request,int number,int labelIndex)
    {
        public readonly CCSPlayerController Bot=bot;
        public readonly uint Handle=bot.EntityHandle.Raw,Owner=request.Owner;
        public readonly int BotSlot=bot.Slot,OwnerSlot=request.Slot,Number=number,LabelIndex=labelIndex;
        public readonly ulong OwnerSteam=request.Steam;
        public readonly EffectPose Pose=request.Pose;
        public Vector3 PositionedFeet=request.Pose.Feet;
        public uint Pawn;
        public uint ModelPawn;
        public string? PlayerModel;
        public ulong PlayerMeshMask;
        public long OwnerTransmitSamples,OwnerTransmitIncluded;
        public uint OwnerTransmitPawn;
        public float OwnerTransmitSampleTime;
        public bool Ready,Removing,TeamPrepared,ResetRequested=true;
        public float NextRepair,NextFeedback,ResetDeadline=Server.CurrentTime+6;
        public int DamageTotal,LastDamage,LastHealth=100,LastArmor=request.Pose.Armor;
        public float LastBlindDuration,BlindUntil,NextMarkerRepair,FlashAnimationUntil;
        public bool HasBlindMeasurement;
        public FlashRole SearchRole=request.SearchRole;
        public CPointWorldText? Marker;
        public uint MarkerHandle;
        public string? MarkerText;
        public Vector3? MarkerPosition;
        public string? Feedback;
        public bool Live=>Bot.IsValid && Bot.IsBot && Bot.EntityHandle.Raw==Handle;
    }
    private sealed class DummyCreation(CCSPlayerController owner,EffectPose pose,int epoch)
    {
        public readonly uint Owner=owner.EntityHandle.Raw;
        public readonly int Slot=owner.Slot,Epoch=epoch;
        public readonly ulong Steam=owner.SteamID;
        public readonly EffectPose Pose=pose;
        public HashSet<uint> Known=[];
        public bool Requested,Cancelled;
        public float ReadyAt=Server.CurrentTime+.2f,Deadline=Server.CurrentTime+6;
        public EffectDummy? Claimed;
        public int? PreferredLabel;
        public FlashRole SearchRole;
        public DummyBatchState<EffectPose>? Batch;
        public long BatchRequest;
    }
    private readonly Dictionary<uint,EffectDummy> effectDummies=[];
    private readonly Dictionary<uint,uint> selectedDummies=[];
    private DummyCreation? dummyCreation;
    private int grenadeEpoch,nextDummyNumber;
    private EffectPose? measurementPose; // Reserved for the isolated GrenadeProbe.
    private const int MaxPlayerDummies=DummyRoster.PlayerLimit,MaxServerDummies=DummyRoster.ServerLimit;
    private bool dummyQuotaLease;
    private string? dummyOriginalJoin,dummyOriginalVacate;
    private string? dummyOriginalImmunityAlpha,dummyAppliedImmunityAlpha;
    private bool creatingDummy=>dummyCreation is not null;
    private bool dummyRemoving=>effectDummies.Values.Any(d=>d.Removing);

    private static string? DummyModel(CCSPlayerPawn pawn)=>pawn.CBodyComponent?.SceneNode?.GetSkeletonInstance().ModelState.ModelName;
    private static readonly Color[] DummyColors=Enumerable.Range(0,MaxPlayerDummies).Select(index=>Color.FromArgb(unchecked((int)DummyRoster.ColorArgb(index)))).ToArray();
    private static string DummyLabel(EffectDummy target)=>DummyRoster.Label(target.LabelIndex);
    private static void MarkDummyField(CCSPlayerPawn pawn,string type,string field)
    {
        if(Schema.IsSchemaFieldNetworked(type,field)) Utilities.SetStateChanged(pawn,type,field);
    }
    // Verified with the installed Valve resourceinfo tool. SAS bit 0 only means
    // "defusekit_hidden": mask 1 draws NO body, while Phoenix mask 1 is correct.
    // Unknown models retain their own native mask instead of inheriting either.
    private static (ulong BodyBits,ulong Default) KnownDummyMesh(string? model)=>model switch
    {
        "agents/models/ctm_sas/ctm_sas.vmdl" => (12,5),
        "agents/models/tm_phoenix/tm_phoenix.vmdl" => (3,1),
        _ => (0,0)
    };
    private static bool RememberDummyModel(EffectDummy target,CCSPlayerPawn pawn,bool spawned=false)
    {
        string? name=DummyModel(pawn);
        if(spawned || target.ModelPawn!=pawn.EntityHandle.Raw || target.PlayerModel!=name)
        {target.ModelPawn=pawn.EntityHandle.Raw;target.PlayerModel=name;target.PlayerMeshMask=0;}
        if(target.PlayerMeshMask!=0) return true;
        var model=pawn.CBodyComponent?.SceneNode?.GetSkeletonInstance().ModelState;
        if(string.IsNullOrWhiteSpace(name) || model is null || model.MeshGroupMask==0) return false;
        var known=KnownDummyMesh(name);
        target.PlayerMeshMask=known.BodyBits!=0 && (model.MeshGroupMask&known.BodyBits)==0 ? known.Default : model.MeshGroupMask;
        return true;
    }
    private static bool DummyMeshReady(EffectDummy target,CCSPlayerPawn pawn)=>
        target.ModelPawn==pawn.EntityHandle.Raw && target.PlayerModel==DummyModel(pawn) && target.PlayerMeshMask!=0 &&
        pawn.CBodyComponent?.SceneNode?.GetSkeletonInstance().ModelState.MeshGroupMask==target.PlayerMeshMask;
    private static void SetDummyMesh(CCSPlayerPawn pawn,ulong mask)
    {
        if(pawn.CBodyComponent is not { } body || body.SceneNode?.GetSkeletonInstance().ModelState is not { } model || model.MeshGroupMask==mask) return;
        model.MeshGroupMask=mask;
        // MeshGroupMask is nested inside the body's skeleton/model state. Mark
        // its real component-relative field, not just the pawn's body pointer.
        uint offset=checked((uint)((long)model.Handle-(long)body.Handle+Schema.GetSchemaOffset("CModelState","m_MeshGroupMask")));
        NativeAPI.SchemaNetworkStateChanged(body.__m_pChainEntity.Handle,offset,uint.MaxValue,uint.MaxValue);
    }
    private static bool DummyOpaque(EffectDummy target,CCSPlayerPawn pawn)=>
        pawn.RenderMode==RenderMode_t.kRenderNormal && pawn.RenderFX==RenderFx_t.kRenderFxNone && pawn.Render.ToArgb()==DummyColors[target.LabelIndex].ToArgb() &&
        !pawn.AllowFadeInView && pawn.FadeMaxDist==0 && pawn.FadeScale==1 && !pawn.GunGameImmunity && pawn.ImmuneToGunGameDamageTime==0 &&
        pawn.GunGameImmunityColor.A==255 && (pawn.Effects&BodyHiddenBits)==0 &&
        DummyMeshReady(target,pawn);
    private static void ShowDummyModel(EffectDummy target,CCSPlayerPawn pawn)
    {
        // Alpha alone cannot cancel a client fade or the spawn-immunity tint.
        // Clear each independent channel and publish every networked field.
        pawn.RenderMode=RenderMode_t.kRenderNormal;pawn.RenderFX=RenderFx_t.kRenderFxNone;pawn.Render=DummyColors[target.LabelIndex];
        pawn.AllowFadeInView=false;pawn.FadeMinDist=-1;pawn.FadeMaxDist=0;pawn.FadeScale=1;
        pawn.GunGameImmunity=false;pawn.ImmuneToGunGameDamageTime=0;pawn.GunGameImmunityColor=Color.White;
        pawn.Effects&=~BodyHiddenBits;
        if(RememberDummyModel(target,pawn)) SetDummyMesh(pawn,target.PlayerMeshMask);
        MarkBodyRendering(pawn);
        foreach(string field in new[]{"m_nRenderFX","m_bAllowFadeInView","m_fadeMinDist","m_fadeMaxDist","m_flFadeScale"}) MarkDummyField(pawn,"CBaseModelEntity",field);
        foreach(string field in new[]{"m_bGunGameImmunity","m_fImmuneToGunGameDamageTime","m_GunGameImmunityColor"}) MarkDummyField(pawn,"CCSPlayerPawn",field);
    }
    private void FreezeDummy(CCSPlayerController bot,CCSPlayerPawn pawn,bool stripWeapons=true,bool animateFlash=false)
    {
        pawn.TakesDamage=true;
        if(pawn.ImmuneToGunGameDamageTime!=0)
        {pawn.ImmuneToGunGameDamageTime=0;MarkDummyField(pawn,"CCSPlayerPawn","m_fImmuneToGunGameDamageTime");}
        if(pawn.GunGameImmunity) {pawn.GunGameImmunity=false;Utilities.SetStateChanged(pawn,"CCSPlayerPawn","m_bGunGameImmunity");}
        // Native spawn completion runs through bot processing, not merely the
        // player_spawn event. Hold input/position while it completes; disabling
        // both bot update gates immediately leaves IsSpawning stuck forever.
        bool spawning=pawn.IsSpawning || pawn.Respawning;
        // Both native update gates are required for the animation to advance.
        // Prediction never enables them. Frozen input, disarming and the
        // post-think pose lock constrain the temporary real-flash update.
        pawn.BotAllowActive=spawning || animateFlash;
        if(pawn.Bot is { } ai)
        {
            ai.AllowActive=spawning || animateFlash;ai.IsAttacking=false;ai.BlindFire=false;
            ai.ButtonFlags=0;ai.ForwardSpeed=0;ai.LeftSpeed=0;ai.VerticalSpeed=0;
        }
        uint frozen=(uint)PlayerFlags.FL_FROZEN;
        bool animationWalk=animateFlash;
        uint flags=spawning || animationWalk ? pawn.Flags|frozen : pawn.Flags&~frozen;
        if(pawn.Flags!=flags) {pawn.Flags=flags;Utilities.SetStateChanged(pawn,"CBaseEntity","m_fFlags");}
        if(pawn.WeaponServices is { } weapons)
        {
            weapons.As<CCSPlayer_WeaponServices>().NextAttack=Server.CurrentTime+2;
            if(stripWeapons && weapons.MyWeapons.Any(w=>w.Value is {IsValid:true}))
            {
                // Native RemoveWeapons also removes armor. Keep its current
                // measured value, not the initial value: real damage survives.
                int armor=pawn.ArmorValue;
                bot.RemoveWeapons();
                if(pawn.ArmorValue!=armor) {pawn.ArmorValue=armor;MarkDummyField(pawn,"CCSPlayerPawn","m_ArmorValue");}
            }
        }
        if(!spawning && bot.InGameMoneyServices is { } money) money.Account=0;
        var move=spawning || animationWalk ? MoveType_t.MOVETYPE_WALK : MoveType_t.MOVETYPE_NONE;
        if(pawn.MoveType!=move || pawn.ActualMoveType!=move) SetMovement(pawn,move,move);
        if(pawn.AbsVelocity.X!=0 || pawn.AbsVelocity.Y!=0 || pawn.AbsVelocity.Z!=0)
            pawn.Teleport(velocity:Vector3.Zero);
        pawn.BaseVelocity.X=0;pawn.BaseVelocity.Y=0;pawn.BaseVelocity.Z=0;
    }
    private static void HoldDummyView(CCSPlayerPawn pawn,EffectPose pose)
    {
        // A frozen target's eye height is part of its saved pose. Native
        // movement updates used by the flash animation can otherwise retain
        // spawn/ground camera smoothing and change the observer after a flash.
        // Keep this in the existing pose lock; verification still samples raw
        // state before repair and at the actual player_blind callback.
        SetBodyEyeOffset(pawn,new Vector3(0,0,pose.EyeHeight));
        float pitch=pose.View.Pitch,yaw=Geometry.NormalizeYaw(pose.View.Yaw);
        static bool Different(float a,float b)=>MathF.Abs(Geometry.NormalizeYaw(a-b))>.01f;
        var body=pawn.AbsRotation;
        bool nativeChanged=Different(pawn.V_angle.X,pitch) || Different(pawn.V_angle.Y,yaw) || Different(pawn.V_angle.Z,0);
        bool bodyChanged=body is not null && (Different(body.X,0) || Different(body.Y,yaw) || Different(body.Z,0));
        bool eyeChanged=Different(pawn.EyeAngles.X,pitch) || Different(pawn.EyeAngles.Y,yaw) || Different(pawn.EyeAngles.Z,0);
        // Bot processing writes native view and body angles independently of
        // EyeAngles. Correct those through the engine without resetting the
        // animation graph; only teleport angles when they actually diverged.
        if(nativeChanged || bodyChanged) AlignView(pawn,pose.View);
        pawn.EyeAngles.X=pitch;pawn.EyeAngles.Y=yaw;pawn.EyeAngles.Z=0;
        if(eyeChanged || nativeChanged || bodyChanged) MarkDummyField(pawn,"CCSPlayerPawn","m_angEyeAngles");
        pawn.AngVelocity.X=0;pawn.AngVelocity.Y=0;pawn.AngVelocity.Z=0;
        if(pawn.Bot is { } ai)
        {ai.LookPitch=pitch;ai.LookYaw=yaw;ai.LookPitchVel=0;ai.LookYawVel=0;ai.InhibitLookAroundTimestamp=Server.CurrentTime+2;}
    }
    private bool PositionEffectDummy(EffectDummy target,out string reason)
    {
        reason="";
        if(!target.Live || target.Bot.PlayerPawn.Value is not {IsValid:true,LifeState:0} pawn || !RememberDummyModel(target,pawn))
        {reason="等待假人出生和模型加载";return false;}
        FreezeDummy(target.Bot,pawn);
        if(pawn.IsSpawning || pawn.Respawning)
        {reason="等待假人完成出生初始化";return false;}
        if(!TryStandingPosition(pawn,target.Pose.Feet,out var safe) || Vector3.Distance(safe,target.Pose.Feet)>3.1f)
        {reason="记录的位置被占用或无法站立，请换一处记录";return false;}
        // Keep the native player model/animation setup. Reapplying the same
        // model here is unnecessary and can reset bodygroup initialization.
        ShowDummyModel(target,pawn);
        target.PositionedFeet=safe;target.Pawn=pawn.EntityHandle.Raw;
        pawn.Health=100;pawn.ArmorValue=target.Pose.Armor;
        // Reset both the measured history and the engine's active blind state.
        // Keep FlashMaxAlpha untouched so later real flashes remain effective.
        pawn.FlashDuration=0;pawn.BlindUntilTime=0;pawn.BlindStartTime=0;
        MarkDummyField(pawn,"CCSPlayerPawnBase","m_flFlashDuration");
        MarkDummyField(pawn,"CCSPlayerPawnBase","m_blindUntilTime");
        MarkDummyField(pawn,"CCSPlayerPawnBase","m_blindStartTime");
        target.DamageTotal=0;target.LastDamage=0;target.LastBlindDuration=0;target.BlindUntil=0;target.FlashAnimationUntil=0;target.HasBlindMeasurement=false;target.LastHealth=100;target.LastArmor=target.Pose.Armor;target.Feedback=null;target.NextFeedback=0;
        pawn.Teleport(position:safe,angles:new Vector3(0,target.Pose.View.Yaw,0),velocity:Vector3.Zero);
        Utilities.SetStateChanged(pawn,"CBaseEntity","m_iHealth");Utilities.SetStateChanged(pawn,"CCSPlayerPawn","m_ArmorValue");
        HoldDummyView(pawn,target.Pose);target.Ready=true;target.ResetRequested=false;target.NextRepair=0;return true;
    }
    private EffectDummy? SelectedDummy(CCSPlayerController owner)
    {
        uint handle=owner.EntityHandle.Raw;
        if(selectedDummies.TryGetValue(handle,out uint selected) && effectDummies.TryGetValue(selected,out var target) && target.Owner==handle && target.OwnerSteam==owner.SteamID && target.Live && !target.Removing) return target;
        var first=effectDummies.Values.Where(d=>d.Owner==handle && d.OwnerSteam==owner.SteamID && d.Live && !d.Removing).OrderBy(d=>d.LabelIndex).FirstOrDefault();
        if(first is not null) selectedDummies[handle]=first.Handle;else selectedDummies.Remove(handle);
        return first;
    }
    private EffectDummy? FindDummy(CCSPlayerController? bot)=>bot is {IsValid:true,IsBot:true} && effectDummies.TryGetValue(bot.EntityHandle.Raw,out var d) && d.Live && !d.Removing ? d : null;
    private bool IsEffectDummy(CCSPlayerController? bot)=>FindDummy(bot) is not null;
    private CCSPlayerController? DummyOwner(EffectDummy d)
    {
        var p=Utilities.GetPlayerFromSlot(d.OwnerSlot);
        return p is {IsValid:true} && p.EntityHandle.Raw==d.Owner && p.SteamID==d.OwnerSteam ? p : null;
    }
    private void AcquireDummyRendering()
    {
        if(dummyOriginalImmunityAlpha is not null) return;
        var alpha=ConVar.Find("sv_disable_immunity_alpha");
        if(alpha is null) {Logger.LogWarning("Dummy visibility override unavailable: sv_disable_immunity_alpha is missing");return;}
        // The official client/server cvar help explicitly says clients otherwise
        // slam player model rendering every frame. Server alpha/model checks
        // alone cannot validate what a connected client finally draws.
        dummyOriginalImmunityAlpha=alpha.StringValue;
        alpha.SetValue(true);dummyAppliedImmunityAlpha=alpha.StringValue;
    }
    private void ReleaseDummyRendering()
    {
        string? original=dummyOriginalImmunityAlpha,applied=dummyAppliedImmunityAlpha;
        dummyOriginalImmunityAlpha=null;dummyAppliedImmunityAlpha=null;
        // Another administrator's later change wins over our saved baseline.
        if(original is not null && ConVar.Find("sv_disable_immunity_alpha") is { } alpha && alpha.StringValue==applied)
            alpha.StringValue=original;
    }
    private void AcquireDummyQuota()
    {
        if(!dummyQuotaLease)
        {
            dummyOriginalJoin=ConVar.Find("bot_join_after_player")?.StringValue;
            dummyOriginalVacate=ConVar.Find("bot_auto_vacate")?.StringValue;dummyQuotaLease=true;
        }
        AcquireDummyRendering();
        // One mechanism owns creation: quota only. bot_add also changes quota,
        // and mixing the two produced a named target plus an uncontrolled bot.
        if(ConVar.Find("bot_quota_mode") is { } mode) mode.StringValue="normal";
        if(ConVar.Find("bot_join_after_player") is { } join) join.StringValue="0";
        if(ConVar.Find("bot_auto_vacate") is { } vacate) vacate.StringValue="0";
    }
    private void SetDummyQuota(int? extra=null)
    {
        // Reset runs on every map even when training was never enabled. Only
        // an acquired dummy lease may write another server's bot population.
        if(!dummyQuotaLease) return;
        int count=Utilities.GetPlayers().Count(p=>p.IsValid && p.IsBot && (!effectDummies.TryGetValue(p.EntityHandle.Raw,out var d) || !d.Removing));
        int reservation=extra ?? (dummyCreation is {Requested:true,Cancelled:false,Claimed:null} ? 1 : 0);
        ConVar.Find("bot_quota")?.SetValue(Math.Max(0,count+reservation));
    }
    private void ReleaseDummyQuota()
    {
        if(!dummyQuotaLease || creatingDummy || effectDummies.Count>0) return;
        SetDummyQuota();
        if(dummyOriginalJoin is not null && ConVar.Find("bot_join_after_player") is { } join) join.StringValue=dummyOriginalJoin;
        if(dummyOriginalVacate is not null && ConVar.Find("bot_auto_vacate") is { } vacate) vacate.StringValue=dummyOriginalVacate;
        dummyQuotaLease=false;dummyOriginalJoin=null;dummyOriginalVacate=null;ReleaseDummyRendering();
    }
    private void HoldEffectPose()
    {
        try {TickDummyCreation();}
        catch(Exception ex)
        {
            Logger.LogWarning(ex,"Dummy creation failed; cancelling quota request");
            if(dummyCreation is { } request) CompleteDummyBatchCreation(request,false);
            if(dummyCreation?.Claimed is { } failed) RemoveDummy(failed);
            dummyCreation=null;SetDummyQuota();ReleaseDummyQuota();
        }
        foreach(var target in effectDummies.Values.ToArray())
        {
            if(!target.Live) {RemoveDummyMarker(target);effectDummies.Remove(target.Handle);continue;}
            if(target.Removing) continue;
            if(DummyOwner(target) is null) {RemoveDummy(target);continue;}
            var pawn=target.Bot.PlayerPawn.Value;
            if(pawn is not {IsValid:true}) continue;
            UpdateDummyMarker(target);
            if(pawn.LifeState!=0)
            {
                // Keep a dead target dead until the owner explicitly resets it.
                if(target.ResetRequested && Server.CurrentTime>=target.NextRepair)
                {target.NextRepair=Server.CurrentTime+.5f;target.Bot.Respawn();}
                continue;
            }
            FreezeDummy(target.Bot,pawn,Server.CurrentTime>=target.NextRepair,Server.CurrentTime<target.FlashAnimationUntil);
            bool newPawn=target.Pawn!=pawn.EntityHandle.Raw;
            if(target.ResetRequested || newPawn)
            {
                if(newPawn && !target.ResetRequested) {target.ResetRequested=true;target.ResetDeadline=Server.CurrentTime+5;}
                if(!PositionEffectDummy(target,out var reason) && Server.CurrentTime>target.ResetDeadline)
                {if(DummyOwner(target) is { } owner) Say(owner,$"假人{DummyLabel(target)}未能放置：{reason}；已移除。");RemoveDummy(target);}
                continue;
            }
            HoldDummyView(pawn,target.Pose);
            if(Server.CurrentTime<target.NextRepair) continue;
            target.NextRepair=Server.CurrentTime+.2f;
            if(!DummyOpaque(target,pawn)) ShowDummyModel(target,pawn);
            if(pawn.AbsOrigin is { } origin && (Vector3.DistanceSquared(NativeWorld.FromNative(origin),target.PositionedFeet)>1 || MathF.Abs(pawn.AbsRotation!.X)>.1f))
                pawn.Teleport(position:target.PositionedFeet,velocity:Vector3.Zero);
        }
        ReleaseDummyQuota();
        // Do not make the isolated probe a member of the player's target pool.
        if(measurementPawn is { } handle && measurementPose is { } pose)
        {
            var pawn=Utilities.GetPlayers().Where(p=>p.IsBot).Select(p=>p.PlayerPawn.Value).FirstOrDefault(p=>p is {IsValid:true,LifeState:0} && p.EntityHandle.Raw==handle);
            if(pawn is not null) HoldDummyView(pawn,pose);
        }
    }
    private void HoldAnimatingDummyPoses()
    {
        if(!loaded || !Practice) return;
        foreach(var target in effectDummies.Values)
        {
            if(!target.Ready || target.Removing || !target.Live || target.FlashAnimationUntil<=0 ||
                Server.CurrentTime>target.FlashAnimationUntil+.1f ||
                target.Bot.PlayerPawn.Value is not {IsValid:true,LifeState:0,AbsOrigin:not null} pawn || pawn.EntityHandle.Raw!=target.Pawn) continue;
            // Native bot updates advance the player's animation but may also
            // write motion after OnTick. Reassert the recorded pose before the
            // server publishes its frame, without restarting the model/graph.
            FreezeDummy(target.Bot,pawn,false,Server.CurrentTime<target.FlashAnimationUntil);
            if(Vector3.DistanceSquared(NativeWorld.FromNative(pawn.AbsOrigin),target.PositionedFeet)>.0001f)
                pawn.Teleport(position:target.PositionedFeet,velocity:Vector3.Zero);
            HoldDummyView(pawn,target.Pose);
        }
    }
    private void PlaceEffectDummy(CCSPlayerController? player)
    {
        if(!Check(player,out _) || Get(player!).EffectPose is not { } pose) return;
        PlaceEffectDummyCore(player!,pose);
    }
    private bool PlaceEffectDummyCore(CCSPlayerController owner,EffectPose pose,int? preferredLabel=null,FlashRole searchRole=FlashRole.Ignore,bool fromLayout=false,
        DummyBatchState<EffectPose>? batch=null,long batchRequest=0)
    {
        if(DummyLayoutLoadActive && !fromLayout) {Say(owner,"正在加载假人组合，完成后再新增。");return false;}
        if(creatingDummy || dummyRemoving) {Say(owner,"假人正在就位，请稍候再放下一个。");return false;}
        if(!DummyRoster.CanAdd(effectDummies.Values.Count(d=>d.Owner==owner.EntityHandle.Raw),effectDummies.Count))
        {Say(owner,$"最多放置{MaxPlayerDummies}个自己的假人，全服最多{MaxServerDummies}个；先移除不需要的目标。");return false;}
        if(effectDummies.Values.Any(d=>d.Live && !d.Removing && Vector3.Distance(d.Pose.Feet,pose.Feet)<36))
        {Say(owner,"这里已有假人；换一处记录，或选择已有假人复位。");return false;}
        AcquireDummyQuota();SetDummyQuota();dummyCreation=new(owner,pose,grenadeEpoch) {PreferredLabel=preferredLabel,SearchRole=searchRole,Batch=batch,BatchRequest=batchRequest};
        Say(owner,"正在新增假人；已有假人会保留。");return true;
    }
    private void ClaimPendingDummy(DummyCreation request,CCSPlayerController bot)
    {
        if(request.Claimed is not null || request.Known.Contains(bot.EntityHandle.Raw)) return;
        int label=request.PreferredLabel is >=0 and <MaxPlayerDummies && !effectDummies.Values.Any(d=>d.Owner==request.Owner && d.LabelIndex==request.PreferredLabel)
            ? request.PreferredLabel.Value : Enumerable.Range(0,MaxPlayerDummies).First(i=>!effectDummies.Values.Any(d=>d.Owner==request.Owner && d.LabelIndex==i));
        var target=new EffectDummy(bot,request,++nextDummyNumber,label);
        if(bot.PlayerPawn.Value is {IsValid:true} original)
            Logger.LogInformation("Dummy spawn opacity before repair: model={Model}; mode={Mode}; fx={Fx}; alpha={Alpha}; fade={AllowFade}/{Min}/{Max}/{Scale}; immunity={Immune}/{Until}/{Color}; mesh={Mesh}",DummyModel(original),original.RenderMode,original.RenderFX,original.Render.A,original.AllowFadeInView,original.FadeMinDist,original.FadeMaxDist,original.FadeScale,original.GunGameImmunity,original.ImmuneToGunGameDamageTime,original.GunGameImmunityColor,original.CBodyComponent?.SceneNode?.GetSkeletonInstance().ModelState.MeshGroupMask);
        effectDummies[target.Handle]=target;request.Claimed=target;
        selectedDummies[request.Owner]=target.Handle;
        target.Bot.PlayerName=$"NadeLab {DummyLabel(target)}";
        Utilities.SetStateChanged(target.Bot,"CBasePlayerController","m_iszPlayerName");
        if(target.Bot.PlayerPawn.Value is {IsValid:true} pawn) {RememberDummyModel(target,pawn);FreezeDummy(target.Bot,pawn);}
        SetDummyQuota();
    }
    private void RegisterDummySpawnHook()
    {
        RegisterDummyManagement();
        RegisterEventHandler<EventPlayerSpawn>((e,info)=>
        {
            if(!loaded || !Practice || e.Userid is not {IsValid:true,IsBot:true} bot) return HookResult.Continue;
            if(dummyCreation is {Requested:true,Claimed:null} request && request.Epoch==grenadeEpoch && !request.Known.Contains(bot.EntityHandle.Raw))
            {
                var added=Utilities.GetPlayers().Where(p=>p.IsValid && p.IsBot && !request.Known.Contains(p.EntityHandle.Raw)).ToArray();
                if(added.Length==1 && added[0].EntityHandle.Raw==bot.EntityHandle.Raw) ClaimPendingDummy(request,bot);
            }
            if(FindDummy(bot) is { } target && bot.PlayerPawn.Value is {IsValid:true} pawn)
            {RememberDummyModel(target,pawn,spawned:true);FreezeDummy(bot,pawn);}
            return HookResult.Continue;
        },HookMode.Post);
    }
    private void TickDummyCreation()
    {
        if(dummyCreation is not { } request) return;
        if(request.Epoch!=grenadeEpoch) {CompleteDummyBatchCreation(request,false);dummyCreation=null;SetDummyQuota();return;}
        var owner=Utilities.GetPlayerFromSlot(request.Slot);
        if(!loaded || !Practice || owner is not {IsValid:true} || owner.EntityHandle.Raw!=request.Owner || owner.SteamID!=request.Steam) request.Cancelled=true;
        if(request.Batch is { } batch && (owner is not {IsValid:true} || owner.PlayerPawn.Value is not {IsValid:true,LifeState:0} ||
            !batch.Matches(DummyBatchIdentityFor(owner)) || batch.Request!=request.BatchRequest || batch.Phase!=DummyBatchPhase.Creating))
        {request.Cancelled=true;SetDummyQuota(0);}
        if(!request.Requested)
        {
            if(request.Cancelled) {CompleteDummyBatchCreation(request,false);dummyCreation=null;ReleaseDummyQuota();return;}
            if(Server.CurrentTime<request.ReadyAt) return;
            // Wait for mode/join settings to settle, then snapshot full handles.
            request.Known=Utilities.GetPlayers().Where(p=>p.IsValid && p.IsBot).Select(p=>p.EntityHandle.Raw).ToHashSet();
            request.Requested=true;SetDummyQuota(1);return;
        }
        if(request.Claimed is null)
        {
            var added=Utilities.GetPlayers().Where(p=>p.IsValid && p.IsBot && !request.Known.Contains(p.EntityHandle.Raw)).ToArray();
            if(added.Length==1)
            {
                ClaimPendingDummy(request,added[0]);
            }
            else if(added.Length>1)
            {
                // Never adopt an ambiguous count delta: another administrator
                // may have added bots concurrently. No unrelated handle is kicked.
                Logger.LogWarning("Dummy quota creation saw {Count} new bots; refusing ambiguous ownership",added.Length);
                if(owner is {IsValid:true}) Say(owner,"同时检测到其他机器人加入，无法安全认领；请稍后重试。");
                CompleteDummyBatchCreation(request,false);dummyCreation=null;SetDummyQuota();ReleaseDummyQuota();return;
            }
        }
        if(request.Claimed is { } claimed)
        {
            if(request.Cancelled || !claimed.Live || claimed.Removing)
            {CompleteDummyBatchCreation(request,false);dummyCreation=null;RemoveDummy(claimed);return;}
            if(!claimed.TeamPrepared)
            {
                // Team/respawn operations run on Tick, never recursively from
                // the player_spawn hook which performs only ownership/freeze.
                claimed.TeamPrepared=true;
                var team=owner!.TeamNum==2 ? CsTeam.CounterTerrorist : CsTeam.Terrorist;
                if(claimed.Bot.Team!=team) {claimed.Bot.SwitchTeam(team);claimed.Bot.Respawn();}
            }
            if(claimed.Ready)
            {
                CompleteDummyBatchCreation(request,true);
                dummyCreation=null;Say(owner,$"假人{DummyLabel(claimed)}已就位：100HP / {claimed.Pose.Armor}甲。可继续记录其他位置并新增。");
                if(request.Batch is null && owner is {IsBot:false} && !ThrowInProgress(owner) && panels.TryGetValue(owner.Slot,out var panel) && panel.Menu.Title.StartsWith("测试假人 ·",StringComparison.Ordinal)) OpenEffectTarget(owner);
                return;
            }
        }
        if(Server.CurrentTime>request.Deadline)
        {
            CompleteDummyBatchCreation(request,false);
            dummyCreation=null;SetDummyQuota();
            if(request.Claimed is { } failed) RemoveDummy(failed);
            if(!request.Cancelled && owner is {IsValid:true}) Say(owner,"假人未能就位，已取消；检查记录处是否被占用后重试。");
            ReleaseDummyQuota();
        }
    }
    private void ResetDummy(EffectDummy target)
    {
        if(!target.Live || target.Removing) return;
        ClearFlashVerification(target.OwnerSlot,"假人复位，本次观测已清除");
        target.ResetRequested=true;target.ResetDeadline=Server.CurrentTime+6;target.NextRepair=0;target.Ready=false;
        if(target.Bot.PlayerPawn.Value is {IsValid:true,LifeState:0}) PositionEffectDummy(target,out _);
        else target.Bot.Respawn();
    }
    private void RemoveDummy(EffectDummy target)
    {
        if(target.Removing) return;
        target.Removing=true;RemoveDummyMarker(target);SetDummyQuota();
        void Verify(int attempt)
        {
            if(!target.Live) {RemoveDummyMarker(target);effectDummies.Remove(target.Handle);ReleaseDummyQuota();return;}
            if(target.Bot.PlayerPawn.Value is {IsValid:true,LifeState:0} pawn) FreezeDummy(target.Bot,pawn);
            target.Bot.Disconnect(NetworkDisconnectionReason.NETWORK_DISCONNECT_KICKED);
            if(attempt<10) AddTimer(.15f,()=>Verify(attempt+1));
            else {target.Removing=false;Logger.LogError("Dummy {Handle} failed to disconnect; ownership retained",target.Handle);}
        }
        Verify(0);
    }
    private void RemovePlayerEffectDummies(int slot)
    {
        // This hook also receives a managed bot's disconnect. Drop its quota
        // reservation before the engine can refill the departed target's slot.
        var departed=effectDummies.Values.Where(d=>d.BotSlot==slot).ToArray();
        foreach(var target in departed) {target.Removing=true;RemoveDummyMarker(target);}
        if(departed.Length>0) SetDummyQuota();
        // Keep the full-handle tombstone until the native controller is gone.
        // During OnClientDisconnect GetPlayers can still return this bot; early
        // removal would count it as an unmanaged bot and restore quota=1.
        if(dummyCreation?.Slot==slot)
        {dummyCreation.Cancelled=true;dummyCreation.Deadline=MathF.Min(dummyCreation.Deadline,Server.CurrentTime+1);SetDummyQuota();}
        foreach(var target in effectDummies.Values.Where(d=>d.OwnerSlot==slot).ToArray())
        {selectedDummies.Remove(target.Owner);RemoveDummy(target);}
        ReleaseDummyQuota();
    }
    private void RemoveEffectDummy()
    {
        if(dummyCreation is { } request)
        {
            request.Cancelled=true;
            // Claim only the single bot from this request before cancelling;
            // a frame can separate native spawn and our normal Tick callback.
            if(request.Requested && request.Claimed is null && request.Epoch==grenadeEpoch)
            {
                var added=Utilities.GetPlayers().Where(p=>p.IsValid && p.IsBot && !request.Known.Contains(p.EntityHandle.Raw)).ToArray();
                if(added.Length==1)
                {ClaimPendingDummy(request,added[0]);}
            }
            dummyCreation=null;
        }
        foreach(var target in effectDummies.Values.ToArray()) RemoveDummy(target);
        SetDummyQuota();selectedDummies.Clear();measurementPawn=null;measurementPose=null;ReleaseDummyQuota();
        // Unload/map cleanup may dispose timers before native Disconnect finishes.
        // All owned targets are already being removed, so release this global
        // rendering lease synchronously instead of relying on a future Tick.
        ReleaseDummyRendering();
    }
    private void OpenEffectTarget(CCSPlayerController? player)
    {
        if(!Check(player,out _,allowPlacement:true)) return;
        if(DummyLayoutLoadActive) {OpenDummyLayouts(player);return;}
        var s=Get(player!);int revision=s.Revision,epoch=grenadeEpoch;var kind=s.Kind;var savedPose=s.EffectPose;
        var mine=OwnedDummies(player!);
        var selected=SelectedDummy(player!);
        var menu=NewPanel($"测试假人 · 我的{mine.Length}/{MaxPlayerDummies}");menu.ExitButton=true;menu.AllowPlacement=true;
        void Add(string label,Action<CCSPlayerController?> action)=>menu.AddMenuOption(label,(who,opt)=>QueueAction(who,current=>
        {
            if(!Check(current,out _,allowPlacement:true) || grenadeEpoch!=epoch || !sessions.TryGetValue(current!.Slot,out var active) || !ReferenceEquals(active,s) || active.Revision!=revision || active.Kind!=kind || active.EffectPose!=savedPose) return;
            action(current);
        }));
        // A grenade centre is not a player standing pose. Keep only target
        // management visible while positioning an airborne effect.
        if(s.Placement is null)
        {
            Add(HasDummyBatch(player!) ? "继续连续放置假人" : "连续放置假人 · E 记录 / 放置",BeginDummyBatch);
            if(s.EffectPose is not null)
            {
                Add($"下次记录护甲：{s.EffectPose.Armor}（切换）",who=>{var current=Get(who!);current.EffectPose=current.EffectPose! with {Armor=current.EffectPose!.Armor==0 ? 100 : 0};OpenEffectTarget(who);});
            }
        }
        if(selected is not null)
        {
            Add("准星选择我的假人",who=>SelectDummyByAim(who));
            Add($"选择列表 · 当前{DummyLabel(selected)}",OpenDummySelection);
            Add($"复位{DummyLabel(selected)} · 原位置/100HP",who=>ResetSelectedDummy(who,selected.Handle));
            Add($"删除{DummyLabel(selected)}",who=>ConfirmDummyRemoval(who!,selected.Handle));
            Add("复位我的全部假人",who=>ResetOwnedDummies(who!));
            Add("移除我的全部假人",who=>ConfirmDummyRemoval(who!,null));
        }
        if(s.Placement is null) Add("假人组合：保存 / 加载",OpenDummyLayouts);
        if(s.Placement is null) Add("按假人找闪：要闪 / 不闪",OpenFlashMenu);
        Add("查看各假人血量与实测结果",who=>ShowDetails(who!,"假人实测",()=>
        {
            if(mine.Length==0) Say(who,"记录站位→离开位置→新增；可放多个。假人正常受伤，真人无敌。");
            foreach(var d in mine) Say(who,DummyStatus(d)+(d.HasBlindMeasurement ? $" · 最近致盲{d.LastBlindDuration:0.0}秒" : " · 尚无致盲事件")+(d.Feedback is null ? "" : " · "+d.Feedback));
        }));
        menu.Open(player!);
    }
    private static string DummyStatus(EffectDummy d)
    {
        var pawn=d.Live ? d.Bot.PlayerPawn.Value : null;
        if(!d.Ready || d.ResetRequested || pawn is not {IsValid:true}) return $"{DummyLabel(d)} · 准备中";
        return $"{DummyLabel(d)} {(pawn is {IsValid:true} ? Math.Max(0,pawn.Health) : 0)}HP / {(pawn is {IsValid:true} ? pawn.ArmorValue : 0)}甲"+(pawn?.LifeState!=0 ? " · 已死亡，可复位" : $" · 累计损失{d.DamageTotal}HP");
    }
    private void NotifyDummyResult(EffectDummy target,string text)
    {
        target.Feedback=text;
        if(DummyOwner(target) is not { } owner) return;
        Get(owner).LastEffect=$"假人{DummyLabel(target)}实测：{text}";
        if(Server.CurrentTime<target.NextFeedback) return;
        target.NextFeedback=Server.CurrentTime+.35f;Say(owner,Get(owner).LastEffect!);
    }
    private string? DummyHud(CCSPlayerController owner)
    {
        var selected=SelectedDummy(owner);if(selected is null) return null;
        int count=effectDummies.Values.Count(d=>d.Owner==owner.EntityHandle.Raw && !d.Removing);
        return $"假人{count}个 · 当前{DummyStatus(selected)}";
    }
    private void NotifyEffect(GrenadeEffect? effect,string text)
    {
        if(effect is null) return;
        var p=Utilities.GetPlayerFromSlot(effect.Slot);
        if(p?.IsValid==true && p.SteamID==effect.Steam && sessions.TryGetValue(effect.Slot,out var s) && s.Revision==effect.Revision && s.ThrowSequence==effect.Sequence)
        {s.LastEffect=text;Say(p,text);}
    }
}
