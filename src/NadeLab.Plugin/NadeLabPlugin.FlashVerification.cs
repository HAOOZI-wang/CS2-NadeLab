using System.Numerics;
using System.Text.Json.Nodes;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private sealed class FlashVerificationRound
    {
        public required int Slot,Epoch,Revision,Selected;
        public required uint Owner,Pawn;
        public required ulong Steam;
        public required string Map;
        public required Session Session;
        public required FlashPlanState Plan;
        public required FlashObserver[] Observers;
        public required Dictionary<uint,uint> Pawns;
        public required FlashVerificationTrial Trial;
        public int Sequence;
        public uint? Projectile,ProjectileHandle;
        public string? ThrowId;
        public bool ExecutionMatched,Published;
    }
    private readonly Dictionary<int,FlashVerificationRound> flashVerification=[];

    private bool BeginFlashVerification(CCSPlayerController owner,IReadOnlyList<FlashObserver> observers,
        IReadOnlyDictionary<uint,uint> expectedPawns)
    {
        ClearFlashVerification(owner.Slot,"重新准备，前次观测已终止");
        if(!Practice || owner.PlayerPawn.Value is not {IsValid:true,LifeState:0} pawn ||
            !sessions.TryGetValue(owner.Slot,out var session) || session.Kind!=GrenadeKind.Flash || session.Arm is null ||
            !session.FlashMode || session.FlashPlan is not { } plan) return false;
        if(Utilities.FindAllEntitiesByDesignerName<CBaseCSGrenadeProjectile>(GrenadeKind.Flash.Projectile()).Any(x=>x.IsValid))
        {Say(owner,"场上还有闪光弹飞行，请等它生效并复位假人后再准备。");return false;}
        var round=new FlashVerificationRound
        {
            Slot=owner.Slot,Owner=owner.EntityHandle.Raw,Pawn=pawn.EntityHandle.Raw,Steam=owner.SteamID,
            Epoch=grenadeEpoch,Revision=session.Revision,Selected=session.Selected,Session=session,Plan=plan,Map=Server.MapName,
            Observers=observers.ToArray(),Pawns=expectedPawns.ToDictionary(x=>x.Key,x=>x.Value),Trial=null!
        };
        round.Trial=new(round.Observers,ReadFlashVerificationDummies(round),Server.CurrentTime);
        if(!round.Trial.BaselineClean)
        {Say(owner,"假人位置或致盲基线未准备好；复位假人后重新搜索 / 准备。");return false;}
        flashVerification[owner.Slot]=round;
        return true;
    }
    private bool FlashVerificationIdentity(FlashVerificationRound round,CCSPlayerController? owner)=>loaded && Practice &&
        grenadeEpoch==round.Epoch && Server.MapName==round.Map && owner is {IsValid:true} &&
        owner.EntityHandle.Raw==round.Owner && owner.SteamID==round.Steam &&
        owner.PlayerPawn.Value is {IsValid:true,LifeState:0} pawn && pawn.EntityHandle.Raw==round.Pawn &&
        sessions.TryGetValue(round.Slot,out var session) && ReferenceEquals(session,round.Session) && session.Revision==round.Revision &&
        session.FlashMode && ReferenceEquals(session.FlashPlan,round.Plan) && session.Selected==round.Selected;

    private FlashNativeObservation[] ReadFlashVerificationDummies(FlashVerificationRound round)
    {
        var observations=new List<FlashNativeObservation>();
        foreach(var observer in round.Observers)
        {
            if(!effectDummies.TryGetValue(observer.Id,out var dummy) || !dummy.Live || dummy.Removing || !dummy.Ready ||
                dummy.ResetRequested || dummy.Owner!=round.Owner || dummy.OwnerSteam!=round.Steam ||
                !round.Pawns.TryGetValue(observer.Id,out uint expectedPawn) ||
                dummy.Bot.PlayerPawn.Value is not {IsValid:true,LifeState:0,AbsOrigin:not null} pawn || pawn.EntityHandle.Raw!=expectedPawn) continue;
            observations.Add(new(observer.Id,pawn.EntityHandle.Raw,NativeWorld.FromNative(pawn.AbsOrigin)+new Vector3(pawn.ViewOffset.X,pawn.ViewOffset.Y,pawn.ViewOffset.Z),
                new(pawn.EyeAngles.X,pawn.EyeAngles.Y,ThrowMode.Left),true,pawn.BlindUntilTime,pawn.FlashDuration,pawn.BlindStartTime));
        }
        return observations.ToArray();
    }
    private void ObserveFlashVerificationRelease(CCSPlayerController thrower,GrenadeKind kind)
    {
        if(kind!=GrenadeKind.Flash) return;
        foreach(var round in flashVerification.Values.Where(x=>!x.Trial.Complete))
        {
            if(thrower.EntityHandle.Raw!=round.Owner || thrower.SteamID!=round.Steam || round.Trial.ReleasedAt is not null)
            {round.Trial.Invalidate("同期有另一颗闪光");continue;}
            round.Trial.Observe(ReadFlashVerificationDummies(round),Server.CurrentTime);
            round.Trial.Release(Server.CurrentTime);
            round.Sequence=round.Session.ThrowSequence+1;
        }
    }
    private void BindFlashVerification(GrenadeFlight flight)
    {
        if(flight.Profile.Kind!=GrenadeKind.Flash || !flashVerification.TryGetValue(flight.Slot,out var round) || round.Trial.Complete) return;
        if(round.Steam!=flight.Steam || round.Revision!=flight.Arm.Revision || round.Sequence!=flight.Sequence ||
            round.Trial.ReleasedAt is not { } release || MathF.Abs(release-flight.ReleasedAt)>.05f || round.ThrowId is not null ||
            !FlashVerificationIdentity(round,Utilities.GetPlayerFromSlot(round.Slot)))
        {round.Trial.Invalidate("无法唯一关联本次手投");return;}
        round.ThrowId=flight.Id;round.Projectile=flight.Projectile.Index;round.ProjectileHandle=flight.Projectile.EntityHandle.Raw;
        round.ExecutionMatched=flight.PoseMatched && flight.LaunchMatched!=false;
    }
    private void ObserveFlashVerificationDetonation(uint entity)
    {
        foreach(var round in flashVerification.Values.Where(x=>!x.Trial.Complete))
        {
            if(round.Projectile!=entity || round.ThrowId is null || !grenadeFlights.TryGetValue(entity,out var flight) ||
                flight.Id!=round.ThrowId || !flight.Projectile.IsValid || flight.Projectile.EntityHandle.Raw!=round.ProjectileHandle)
            {round.Trial.Invalidate("爆炸归属不明或同期有另一颗闪光");continue;}
            round.Trial.Observe(ReadFlashVerificationDummies(round),Server.CurrentTime);
            round.Trial.Detonate(Server.CurrentTime);
        }
    }
    private void ObserveFlashVerificationBlind(EventPlayerBlind e)
    {
        var victim=e.Userid;
        if(victim?.IsValid!=true || victim.PlayerPawn.Value is not {IsValid:true,AbsOrigin:not null} pawn) return;
        uint controller=victim.EntityHandle.Raw,pawnHandle=pawn.EntityHandle.Raw,entity=(uint)e.Entityid;
        float at=Server.CurrentTime,duration=e.BlindDuration;
        var eye=NativeWorld.FromNative(pawn.AbsOrigin)+new Vector3(pawn.ViewOffset.X,pawn.ViewOffset.Y,pawn.ViewOffset.Z);
        var view=new Aim(pawn.EyeAngles.X,pawn.EyeAngles.Y,ThrowMode.Left);
        foreach(var round in flashVerification.Values.Where(x=>!x.Trial.Complete && x.Observers.Any(o=>o.Id==controller)).ToArray())
        {
            if(round.Projectile!=entity || round.ThrowId is null)
            {round.Trial.Invalidate("致盲来自另一颗或无法关联的闪光");continue;}
            // Copy event fields now: the native event wrapper must not outlive
            // its callback. Detonation and player_blind ordering is not assumed.
            Server.NextFrame(()=>
            {
                if(!flashVerification.TryGetValue(round.Slot,out var active) || !ReferenceEquals(active,round) ||
                    !FlashVerificationIdentity(round,Utilities.GetPlayerFromSlot(round.Slot))) return;
                var effect=grenadeEffects.GetValueOrDefault(entity);
                bool attributed=effect is {Kind:GrenadeKind.Flash} && effect.Id==round.ThrowId &&
                    effect.Steam==round.Steam && effect.Sequence==round.Sequence && effect.Revision==round.Revision;
                round.Trial.Blind(controller,pawnHandle,eye,view,duration,at,attributed);
            });
        }
    }
    private void TickFlashVerification()
    {
        foreach(var (slot,round) in flashVerification.ToArray())
        {
            var owner=Utilities.GetPlayerFromSlot(slot);
            if(!FlashVerificationIdentity(round,owner))
            {
                round.Trial.Invalidate("拥有者、地图或任务已变化");PublishFlashVerification(round,null);
                flashVerification.Remove(slot);continue;
            }
            if(!round.Trial.Complete)
            {
                if(round.Trial.ReleasedAt is null && round.Session.Arm is null) round.Trial.Invalidate("手投记录已取消");
                else round.Trial.Observe(ReadFlashVerificationDummies(round),Server.CurrentTime);
            }
            if(round.Trial.Complete) PublishFlashVerification(round,owner);
        }
    }
    private void ClearFlashVerification()
    {
        foreach(var round in flashVerification.Values)
        {round.Trial.Invalidate("地图或插件状态已重置");PublishFlashVerification(round,null);}
        flashVerification.Clear();
    }
    private void ClearFlashVerification(int slot,string reason="已更换候选或取消本次观测")
    {
        if(!flashVerification.Remove(slot,out var round)) return;
        round.Trial.Invalidate(reason);PublishFlashVerification(round,null);
    }
    private void ClearFlashVerification(CCSPlayerController owner)=>ClearFlashVerification(owner.Slot);
    private void PublishFlashVerification(FlashVerificationRound round,CCSPlayerController? owner)
    {
        if(round.Published) return;
        round.Published=true;
        // Copy only this plan's first normal publication. Clearing a trial later
        // invalidates the live object and must not erase its previous evidence.
        if(owner is not null && FlashVerificationIdentity(round,owner))
            round.Plan.Trials?.Record(round.Selected,round.Trial.Complete,round.ExecutionMatched,
                round.Trial.InvalidReason,round.Trial.Results());
        var measurements=new JsonArray();
        foreach(var row in round.Trial.Results())
        {
            var expected=round.Observers.First(x=>x.Id==row.Id);
            var baseline=round.Trial.Baselines.First(x=>x.Id==row.Id);
            measurements.Add(new JsonObject {["ControllerHandle"]=row.Id,["PawnHandle"]=round.Pawns.GetValueOrDefault(row.Id),
                ["Label"]=row.Label,["Role"]=row.Role.ToString(),["Eye"]=VectorNode(expected.Eye),
                ["ViewPitch"]=expected.View.Pitch,["ViewYaw"]=expected.View.Yaw,["State"]=row.State.ToString(),
                ["EventDuration"]=row.EventDuration,["EventAt"]=row.EventAt,["UnknownReason"]=row.Reason,
                ["Requirement"]=FlashVerificationRequirements.Evaluate(row).ToString(),
                ["BaselineBlindUntil"]=baseline.BlindUntil,["BaselineFlashDuration"]=baseline.FlashDuration,
                ["BaselineBlindStart"]=baseline.BlindStart,["NativeStateIncreased"]=row.NativeIncreased,["SampleCount"]=row.SampleCount});
        }
        WriteGrenade(new() {["Event"]="flash-effect-trial",["ThrowId"]=round.ThrowId,["ProjectileEntity"]=round.Projectile,
            ["ProjectileHandle"]=round.ProjectileHandle,["OwnerHandle"]=round.Owner,["OwnerPawnHandle"]=round.Pawn,
            ["PlayerSteamId"]=round.Steam.ToString(),["TrialMap"]=round.Map,["Revision"]=round.Revision,["Sequence"]=round.Sequence,
            ["PlayerIsBot"]=Utilities.GetPlayerFromSlot(round.Slot)?.IsBot,
            ["StartedAt"]=round.Trial.StartedAt,["ReleasedAt"]=round.Trial.ReleasedAt,["DetonatedAt"]=round.Trial.DetonatedAt,
            ["LastSampleAt"]=round.Trial.LastSampleAt,["ExecutionMatched"]=round.ExecutionMatched,
            ["Requirement"]=FlashVerificationRequirements.Overall(round.Trial.Results()).ToString(),["MinimumEventSeconds"]=FlashVerificationRequirements.MinimumEventSeconds,
            ["InvalidReason"]=round.Trial.InvalidReason,["Measurements"]=measurements,
            ["Meaning"]="event duration is not full-white duration; no event is not measured zero"},"flash-effect-trials.jsonl");
        if(owner is not null) Say(owner,FlashVerificationSummary(owner)!);
    }
    private FlashVerificationRound? CurrentFlashVerification(CCSPlayerController owner)=>
        flashVerification.TryGetValue(owner.Slot,out var round) && FlashVerificationIdentity(round,owner) ? round : null;
    private string? FlashVerificationSummary(CCSPlayerController owner)
    {
        var round=CurrentFlashVerification(owner);if(round is null) return null;
        if(!round.Trial.Complete) return round.Trial.ReleasedAt is null ? "等待手投 · 全体假人独立记录" : "本次实投观测中 · 尚无结论";
        var rows=round.Trial.Results();
        string requirement=FlashVerificationRequirements.Overall(rows) switch
        {
            FlashRequirementState.Satisfied=>round.ExecutionMatched ? "本候选本次达标" : "本次效果满足要求",
            FlashRequirementState.Failed=>"本次要求未满足",_=>"本次要求未知"
        };
        int exposed=rows.Count(x=>x.Role==FlashRole.Protect && x.State==FlashMeasurementState.ObservedBlind);
        int missed=rows.Count(x=>x.Role==FlashRole.MustFlash && x.State==FlashMeasurementState.NoBlindObserved);
        return requirement+$"：{rows.Count(x=>x.State==FlashMeasurementState.ObservedBlind)}个致盲 / {rows.Count(x=>x.State==FlashMeasurementState.NoBlindObserved)}个未观察致盲 / {rows.Count(x=>x.State==FlashMeasurementState.Unknown)}个未知"+
            (exposed>0 ? $"；保护对象{exposed}个被闪" : "")+(missed>0 ? $"；要闪对象{missed}个未观察到" : "")+
            (round.ExecutionMatched ? "；动作匹配。事件秒数不等于全白时长。" : "；动作未匹配，不能验证候选。事件秒数不等于全白时长。");
    }
    private static string FlashMeasurementText(FlashMeasurement row)=>FlashVerificationRequirements.Text(row);
    private string? FlashVerificationDummy(CCSPlayerController owner,uint dummyHandle)=>
        CurrentFlashVerification(owner)?.Trial.Results().FirstOrDefault(x=>x.Id==dummyHandle) is { } row ? FlashMeasurementText(row) : null;
    private string[] FlashVerificationLines(CCSPlayerController owner)
    {
        var round=CurrentFlashVerification(owner);if(round is null) return [];
        return new[]{FlashVerificationSummary(owner)!}.Concat(round.Trial.Results().Select(row=>$"{row.Label}（{FlashRoleLabel(row.Role)}）：{FlashMeasurementText(row)}")).ToArray();
    }
}
