using System.Numerics;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API.Modules.Memory;
using System.Text.Json;
using System.Text.Json.Nodes;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private sealed class GrenadeFlight(CBaseCSGrenadeProjectile projectile,int slot,ulong steam,int sequence,Arm arm,ThrowObservation? snapshot,float releasedAt,PhysicsProfile profile)
    {
        public readonly string Id=Guid.NewGuid().ToString("N");
        public readonly CBaseCSGrenadeProjectile Projectile=projectile;
        public readonly int Slot=slot,Sequence=sequence;
        public readonly ulong Steam=steam;
        public readonly Arm Arm=arm;
        public readonly ThrowObservation? Snapshot=snapshot;
        public readonly float ReleasedAt=releasedAt;
        public readonly PhysicsProfile Profile=profile;
        public readonly float SpawnDelay=projectile.CreateTime-releasedAt;
        public readonly float? ScheduledFuse=projectile.DetonateTime>projectile.CreateTime ? projectile.DetonateTime-projectile.CreateTime : null;
        public readonly Vector3 InitialVelocity=NativeWorld.FromNative(projectile.InitialVelocity);
        public readonly Vector3 FirstObservedPosition=NativeWorld.FromNative(projectile.AbsOrigin!);
        public readonly Vector3 FirstObservedVelocity=NativeWorld.FromNative(projectile.AbsVelocity);
        public readonly float FirstObservedAge=Server.CurrentTime-projectile.CreateTime;
        public bool OwnerIsBot,PoseMatched;
        public bool? LaunchMatched;
        public readonly List<Sample> Samples=[];
        public JsonNode? World;
    }
    private sealed record GrenadeEffect(string Id,GrenadeKind Kind,int Slot,ulong Steam,int Revision,int Sequence,float At,Vector3 Position);
    private sealed record GrenadeRelease(int Slot,ulong Steam,GrenadeKind Kind,float At);
    private readonly List<GrenadeRelease> recentGrenadeReleases=[];
    private readonly Dictionary<uint,GrenadeFlight> grenadeFlights=[];
    private readonly Dictionary<uint,GrenadeEffect> grenadeEffects=[];
    private readonly Dictionary<uint,(CInferno Entity,GrenadeEffect? Source,float Since,float Next)> infernos=[];
    private void ClearGrenadeState() {ClearFlightPreviews();ClearFlashVerification();RemoveEffectDummy();grenadeEpoch++;grenadeFlights.Clear();grenadeEffects.Clear();infernos.Clear();recentGrenadeReleases.Clear();}
    private Func<string> SnapshotGrenade(Func<JsonObject> build)
    {
        string utc=DateTimeOffset.UtcNow.ToString("O"),map=Server.MapName;
        return ()=>{var node=build();node["SchemaVersion"]=1;node["Utc"]=utc;node["Map"]=map;return node.ToJsonString(Json);};
    }
    private void WriteGrenade(JsonObject node,string file="grenade-throws.jsonl")
        =>telemetry?.Write(Path.Combine(DataPath,file),SnapshotGrenade(()=>node));
    private static JsonNode? VectorNode(Vector3 v)=>JsonSerializer.SerializeToNode(v,NadeLabJsonContext.Default.Vector3);

    private HookResult OnOtherThrown(EventGrenadeThrown e)
    {
        var p=e.Userid;
        if(!loaded || !Practice || p?.IsValid!=true || !sessions.TryGetValue(p.Slot,out var s) || s.Kind==GrenadeKind.Smoke ||
            (e.Weapon!=s.Kind.Weapon() && "weapon_"+e.Weapon!=s.Kind.Weapon())) return HookResult.Continue;
        int sequence=++s.ThrowSequence;
        s.Pending=null;s.LastVerified=null;s.SmokeReview=null;ClearPreview(p.Slot);
        if(s.Arm is not { } arm) {s.Snapshot=null;return HookResult.Continue;}
        s.Arm=null;
        var profile=arm.Physics ?? ProfileFor(s);var snapshot=s.Snapshot;
        int slot=p.Slot,epoch=grenadeEpoch;ulong steam=p.SteamID;float at=Server.CurrentTime;
        if(at>arm.Expires || arm.Revision!=s.Revision || p.PlayerPawn.Value?.MoveType==MoveType_t.MOVETYPE_NOCLIP) return HookResult.Continue;
        ClearPreview(slot);
        Server.NextFrame(()=>
        {
            if(!loaded || !Practice || grenadeEpoch!=epoch || grenadeFlights.Count>=16) return;
            var owner=Utilities.GetPlayerFromSlot(slot);
            if(owner?.IsValid!=true || owner.SteamID!=steam || owner.PlayerPawn.Value is not {IsValid:true} pawn) return;
            var candidates=Utilities.FindAllEntitiesByDesignerName<CBaseCSGrenadeProjectile>(profile.Kind.Projectile())
                .Where(g=>g.IsValid && g.Thrower.Value?.Index==pawn.Index && g.CreateTime>=at-.1f && !grenadeFlights.ContainsKey(g.Index)).ToArray();
            if(candidates.Length!=1 || candidates[0].AbsOrigin is null) {Say(owner,"无法唯一关联投掷物，本次不生成验证结论。");return;}
            var flight=new GrenadeFlight(candidates[0],slot,steam,sequence,arm,snapshot,at,profile)
                {World=worldVersion?.DeepClone(),OwnerIsBot=owner.IsBot,PoseMatched=!arm.ObservationOnly && Verification.Matches(arm.Start,arm.Candidate.Aim,snapshot,at,profile)};
            if(flight.PoseMatched && arm.Candidate.Aim.Airborne)
            {
                // Check at binding, with the throwing pawn excluded, just as
                // the smoke recorder does. Detonation-time geometry can differ.
                var launch=new SmokeSimulation(new NativeWorld(pawn),profile,arm.Start,arm.Candidate.Aim,false);
                flight.LaunchMatched=Verification.MatchesJumpLaunch(launch.Position,launch.Velocity,flight.InitialVelocity,
                    flight.FirstObservedPosition,1f/64,profile.Gravity);
            }
            grenadeFlights[candidates[0].Index]=flight;
            if(arm.EffectObjective) BindFlashVerification(flight);
            Say(owner,$"正在记录{profile.Kind.Label()}实际飞行与生效事件。");
        });
        return HookResult.Continue;
    }
    private void TickGrenadeFlights()
    {
        foreach(var (id,f) in grenadeFlights.ToArray())
        {
            if(Server.CurrentTime-f.ReleasedAt>25)
            {
                grenadeFlights.Remove(id);
                WriteGrenade(new() {["ThrowId"]=f.Id,["Kind"]=f.Profile.Kind.ToString(),["Outcome"]="effect-event-missing",["EffectVerified"]=false,["WorldVersion"]=f.World?.DeepClone()});
            }
            else if(f.Projectile.IsValid && f.Projectile.AbsOrigin is { } pos && f.Samples.Count<1280)
                f.Samples.Add(new(Server.CurrentTime-f.ReleasedAt,NativeWorld.FromNative(pos)));
        }
        foreach(var id in grenadeEffects.Where(x=>Server.CurrentTime-x.Value.At>25).Select(x=>x.Key).ToArray()) grenadeEffects.Remove(id);
        foreach(var (id,item) in infernos.ToArray())
        {
            if(!item.Entity.IsValid || Server.CurrentTime-item.Since>15) {infernos.Remove(id);continue;}
            if(Server.CurrentTime<item.Next) continue;
            infernos[id]=item with {Next=Server.CurrentTime+.5f};
            var cells=new JsonArray();var positions=new List<Vector3>();int count=Math.Clamp(item.Entity.FireCount,0,Math.Min(64,item.Entity.FireIsBurning.Length));
            int offset=(int)Schema.GetSchemaOffset("CInferno","m_firePositions");
            for(int n=0;n<count;n++) if(item.Entity.FireIsBurning[n])
            {var point=Marshal.PtrToStructure<Vector3>(item.Entity.Handle+offset+n*12);if(Geometry.Finite(point)) {cells.Add(VectorNode(point));positions.Add(point);}}
            RememberFirePreview(item.Source,positions.ToArray());
            WriteGrenade(new() {["ThrowId"]=item.Source?.Id,["Event"]="fire-cells",["InfernoEntity"]=id,["Age"]=Server.CurrentTime-item.Since,
                ["Cells"]=cells,["CoverageVerified"]=false,["Attribution"]=item.Source is null ? "unknown" : "unique-spatial-time-match"},"grenade-effects.jsonl");
        }
    }
    private void FinishFireProjectile(int? slot,Vector3 position)
    {
        var owner=slot is { } playerSlot ? Utilities.GetPlayerFromSlot(playerSlot) : null;
        if(owner?.IsValid!=true) return;
        var releases=recentGrenadeReleases.Where(r=>r.Slot==slot && r.Steam==owner.SteamID && r.Kind.Fire() && Server.CurrentTime-r.At is >=0 and <12).ToArray();
        if(releases.Length!=1) return;
        var release=releases[0];
        var possible=grenadeFlights.Where(pair=>pair.Value.Slot==slot && pair.Value.Steam==release.Steam && pair.Value.Profile.Kind==release.Kind && pair.Value.ReleasedAt==release.At && pair.Value.Samples.LastOrDefault() is { } last && Vector3.Distance(last.Position,position)<64).ToArray();
        if(possible.Length==1) FinishGrenade(possible[0].Key,position,possible[0].Value.Profile.Kind,"burst-ignition-unconfirmed");
    }
    private void FinishGrenade(uint id,Vector3 actual,GrenadeKind kind,string outcome)
    {
        if(!grenadeFlights.TryGetValue(id,out var f) || f.Profile.Kind!=kind)
        {if(Practice && grenadeFlights.Count>0) WriteGrenade(new() {["Event"]="unmatched-detonation",["Kind"]=kind.ToString(),["Entity"]=id,["Position"]=VectorNode(actual)},"grenade-probes.jsonl");return;}
        grenadeFlights.Remove(id);
        bool poseMatched=f.PoseMatched;bool? launchMatched=f.LaunchMatched;
        bool matched=poseMatched && launchMatched!=false;
        var executionIssues=f.Arm.ObservationOnly ? new[]{"自由实投，不核对指定方案"} :
            Verification.ExplainMismatch(f.Arm.Start,f.Arm.Candidate.Aim,f.Snapshot,f.ReleasedAt,f.Profile)
                .Concat(launchMatched==false ? new[]{"首次投掷物位置/初速度未吻合跳投锁定状态；未验证"} : []).ToArray();
        bool? hit=f.Arm.EffectObjective ? null : f.Arm.Target?.Accepts(new NativeWorld(null),actual);
        float elapsed=Server.CurrentTime-f.ReleasedAt;var samples=f.Samples.ToArray();
        var serialize=SnapshotGrenade(()=>new JsonObject {["ThrowId"]=f.Id,["Kind"]=kind.ToString(),["ProjectileEntity"]=id,["PlayerSteamId"]=f.Steam.ToString(),["Outcome"]=outcome,
            ["PlayerIsBot"]=f.OwnerIsBot,["CapturePhase"]="next-frame-bind",
            ["WorldVersion"]=f.World?.DeepClone(),["Physics"]=JsonSerializer.SerializeToNode(f.Profile,NadeLabJsonContext.Default.PhysicsProfile),
            ["Start"]=JsonSerializer.SerializeToNode(f.Arm.Start,NadeLabJsonContext.Default.ThrowStart),["Target"]=f.Arm.Target is { } target ? JsonSerializer.SerializeToNode(target,NadeLabJsonContext.Default.Target) : null,
            ["ObjectiveMode"]=f.Arm.EffectObjective ? "flash-observers" : "position",
            ["ActualThrow"]=f.Snapshot is null ? null : JsonSerializer.SerializeToNode(f.Snapshot,NadeLabJsonContext.Default.ThrowObservation),
            ["Predicted"]=f.Arm.ObservationOnly ? null : JsonSerializer.SerializeToNode(f.Arm.Candidate,NadeLabJsonContext.Default.Candidate),
            ["InitialVelocity"]=VectorNode(f.InitialVelocity),["SpawnDelay"]=f.SpawnDelay,["EngineScheduledFuse"]=f.ScheduledFuse,["Actual"]=VectorNode(actual),["FlightSeconds"]=elapsed,
            ["FirstObservedPosition"]=VectorNode(f.FirstObservedPosition),["FirstObservedVelocity"]=VectorNode(f.FirstObservedVelocity),["FirstObservedAge"]=f.FirstObservedAge,
            ["PoseMatched"]=poseMatched,["LaunchMatched"]=launchMatched,["ExecutionIssues"]=new JsonArray(executionIssues.Select(x=>(JsonNode?)JsonValue.Create(x)).ToArray()),
            ["ObservationOnly"]=f.Arm.ObservationOnly,["ExecutionMatched"]=matched,["PositionHit"]=hit,["EffectVerified"]=false,
            ["Samples"]=new JsonArray(samples.Select(s=>(JsonNode)new JsonObject {["Time"]=s.Time,["Position"]=VectorNode(s.Position)}).ToArray())});
        bool queued=telemetry?.Write(Path.Combine(DataPath,"grenade-throws.jsonl"),serialize)==true;
        if(queued && !f.Arm.EffectObjective) RememberGrenadeActual(f,matched,hit==true);
        grenadeEffects[id]=new(f.Id,kind,f.Slot,f.Steam,f.Arm.Revision,f.Sequence,Server.CurrentTime,actual);
        var p=Utilities.GetPlayerFromSlot(f.Slot);
        if(p?.IsValid==true && p.SteamID==f.Steam && sessions.TryGetValue(f.Slot,out var s) && s.Revision==f.Arm.Revision && s.ThrowSequence==f.Sequence)
        {
            Say(p,f.Arm.EffectObjective ? "闪光已爆炸，正在汇总本次所有假人的效果；不以爆点判定成功。" : $"{kind.Label()}实投：生效位置{(hit==true ? "在区域内" : "在区域外")}，飞行{Server.CurrentTime-f.ReleasedAt:F2}秒。伤害、致盲和火区单独记录，不由落点推定。");
            s.LastVerified=!f.Arm.EffectObjective && matched && hit==true && !kind.Fire() ? serialize : null;
        }
    }
    private void RecordBlind(EventPlayerBlind e)
    {
        if(!Practice) return;
        ObserveFlashVerificationBlind(e);
        var source=grenadeEffects.GetValueOrDefault((uint)e.Entityid);
        int entity=e.Entityid,epoch=grenadeEpoch;float duration=e.BlindDuration;var victim=e.Userid;int? slot=victim?.Slot;
        if(FindDummy(victim) is { } dummy)
        {
            dummy.HasBlindMeasurement=true;dummy.LastBlindDuration=float.IsFinite(duration) ? Math.Max(0,duration) : 0;
            dummy.BlindUntil=Server.CurrentTime+dummy.LastBlindDuration;
            if(dummy.LastBlindDuration>0 && victim?.PlayerPawn.Value is {IsValid:true,LifeState:0} flashed)
            {
                // Let the game express flash strength and its recovery pose.
                // Do not synthesize a full blind from the duration estimate.
                dummy.FlashAnimationUntil=Math.Max(dummy.FlashAnimationUntil,Server.CurrentTime+Math.Min(dummy.LastBlindDuration,15)+.5f);
                flashed.AnimGraphUpdateEnabled=true;
                uint flashedHandle=flashed.EntityHandle.Raw;
                // The engine can finish assigning the blind/fade timestamps
                // after firing player_blind. Read them on the following frame.
                Server.NextFrame(()=>
                {
                    if(!loaded || !Practice || grenadeEpoch!=epoch || !dummy.Live || dummy.Removing || dummy.FlashAnimationUntil==0 ||
                        dummy.Bot.PlayerPawn.Value is not {IsValid:true,LifeState:0} current || current.EntityHandle.Raw!=flashedHandle) return;
                    if(float.IsFinite(current.BlindUntilTime))
                        dummy.FlashAnimationUntil=Math.Max(dummy.FlashAnimationUntil,Math.Clamp(current.BlindUntilTime,Server.CurrentTime,Server.CurrentTime+15)+.5f);
                    MarkDummyField(current,"CCSPlayerPawnBase","m_flFlashDuration");
                    MarkDummyField(current,"CCSPlayerPawnBase","m_flFlashMaxAlpha");
                });
            }
            NotifyDummyResult(dummy,$"致盲{duration:F2}秒（引擎事件时长）。");
        }
        var pawn=victim?.PlayerPawn.Value;
        var eye=pawn?.AbsOrigin is { } pos ? VectorNode(NativeWorld.FromNative(pos)+new Vector3(0,0,pawn.ViewOffset.Z)) : null;
        var view=pawn is null ? null : VectorNode(new(pawn.EyeAngles.X,pawn.EyeAngles.Y,0));
        var expected=FindDummy(victim) is { } measured ? measured.Pose with {Feet=measured.PositionedFeet} : (pawn?.IsValid==true && measurementPawn==pawn.EntityHandle.Raw ? measurementPose : null);
        bool? poseMatched=expected is null || pawn?.AbsOrigin is null ? null : Vector3.Distance(NativeWorld.FromNative(pawn.AbsOrigin),expected.Feet)<2 && MathF.Abs(pawn.EyeAngles.X-expected.View.Pitch)<1 && MathF.Abs(Geometry.NormalizeYaw(pawn.EyeAngles.Y-expected.View.Yaw))<1;
        Server.NextFrame(()=>
        {
            if(!loaded || !Practice || grenadeEpoch!=epoch) return;
            var effect=source ?? grenadeEffects.GetValueOrDefault((uint)entity);
            if(effect?.Kind!=GrenadeKind.Flash || MathF.Abs(Server.CurrentTime-effect.At)>.25f) effect=null;
            NotifyEffect(effect,$"闪光实测：对象{slot}致盲{duration:F2}秒（{(poseMatched==false ? "假人偏离预设朝向，需重测" : "实际站位/朝向")}）。");
            WriteGrenade(new() {["ThrowId"]=effect?.Id,["Event"]="player-blind",
            ["ProjectileEntity"]=entity,["VictimSlot"]=slot,["BlindDuration"]=duration,["ObserverEye"]=eye,["ObserverView"]=view,
            ["ObserverPoseMatched"]=poseMatched,
            ["Attribution"]=effect is null ? "unattributed-event" : "event-entity-id"},"grenade-effects.jsonl");
        });
    }
    private void RecordGrenadeDamage(EventPlayerHurt e)
    {
        if(!Practice) return;
        // Always report the target's measured result, including free throws and
        // bullets. This does not assign an ambiguous event to a saved solution.
        if(FindDummy(e.Userid) is { } dummy)
        {
            // Report actual resource loss, not overkill/raw event damage. Some
            // native death paths carry stale damage fields with current HP=0.
            int hp=Math.Max(0,e.Health),ap=Math.Max(0,e.Armor);
            int hpLost=Math.Max(0,dummy.LastHealth-hp),apLost=Math.Max(0,dummy.LastArmor-ap);
            dummy.LastHealth=hp;dummy.LastArmor=ap;dummy.LastDamage=hpLost;dummy.DamageTotal+=hpLost;
            NotifyDummyResult(dummy,$"本次损失{hpLost}HP / {apLost}甲，剩余{hp}HP / {ap}甲。");
        }
        if(e.Weapon is not ("hegrenade" or "inferno" or "molotov" or "incgrenade" or "decoy")) return;
        int epoch=grenadeEpoch;
        int? attacker=e.Attacker?.Slot,victim=e.Userid?.Slot;ulong? attackerSteam=e.Attacker?.SteamID;string weapon=e.Weapon;float at=Server.CurrentTime;
        int? victimTeam=e.Userid?.TeamNum,remainingHealth=e.Userid?.PlayerPawn.Value?.Health,remainingArmor=e.Userid?.PlayerPawn.Value?.ArmorValue;
        int health=e.DmgHealth,armor=e.DmgArmor;bool? protectedVictim=e.Userid?.PlayerPawn.Value is {IsValid:true} pawn ? !pawn.TakesDamage : null;
        Server.NextFrame(()=>
        {
            if(!loaded || !Practice || grenadeEpoch!=epoch) return;
            // Hurt events have no projectile identity. Never infer fire/decoy ownership
            // from a single armed record: unrecorded throws may also be active.
            bool singleHe=weapon=="hegrenade" && recentGrenadeReleases.Count(r=>r.Slot==attacker && r.Steam==attackerSteam && r.Kind==GrenadeKind.HighExplosive && at-r.At is >=0 and <3)==1;
            var possible=grenadeEffects.Values.Where(g=>singleHe && g.Slot==attacker && g.Steam==attackerSteam && g.Kind==GrenadeKind.HighExplosive && MathF.Abs(at-g.At)<.25f).ToArray();
            if(weapon=="hegrenade" && possible.Length==1) NotifyEffect(possible[0],$"手雷实测：对象{victim}损失{health}HP / {armor}护甲；归属为唯一投掷者时间窗。");
            WriteGrenade(new() {["ThrowId"]=possible.Length==1 ? possible[0].Id : null,["Event"]="player-hurt",["VictimSlot"]=victim,["AttackerSlot"]=attacker,
                ["Weapon"]=weapon,["DamageHealth"]=health,["DamageArmor"]=armor,["ProtectedVictim"]=protectedVictim,
                ["VictimTeam"]=victimTeam,["RemainingHealth"]=remainingHealth,["RemainingArmor"]=remainingArmor,
                ["Attribution"]=possible.Length==1 ? "unique-attacker-time-window" : "unknown-or-ambiguous"},"grenade-effects.jsonl");
        });
    }
    private void RecordInferno(uint id)
    {
        if(!Practice || infernos.Count>=16) return;
        var inferno=Utilities.GetEntityFromIndex<CInferno>((int)id);if(inferno?.IsValid!=true) return;
        var position=NativeWorld.FromNative(inferno.StartPos);float at=Server.CurrentTime;int epoch=grenadeEpoch;
        Server.NextFrame(()=>
        {
            if(!loaded || !Practice || grenadeEpoch!=epoch || !inferno.IsValid || (infernos.Count>=16 && !infernos.ContainsKey(id))) return;
            bool singleFire=recentGrenadeReleases.Count(r=>r.Kind.Fire() && at-r.At is >=0 and <12)==1;
            var possible=grenadeEffects.Values.Where(g=>singleFire && g.Kind.Fire() && MathF.Abs(at-g.At)<.25f && Vector3.Distance(g.Position,position)<64).ToArray();
            infernos[id]=(inferno,possible.Length==1 ? possible[0] : null,at,at);
            if(possible.Length==1) NotifyEffect(possible[0],"已观测到实际火区；正在采样火点位置，未据此判定烧满。");
        });
    }
}
