using System.Drawing;
using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using CsTrace=CounterStrikeSharp.API.Modules.Utils.Trace;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private bool dummyCheckPending;
    private uint? dummyDiagnosticGrenade;
    private bool dummyDiagnosticDetonated;
    private static Vector3? FindDummyDiagnosticSite(Vector3 first)
    {
        var mins=new CounterStrikeSharp.API.Modules.Utils.Vector(-16,-16,0);
        var maxs=new CounterStrikeSharp.API.Modules.Utils.Vector(16,16,72);
        var options=new TraceOptions {InteractsAs=Contents.Player,InteractsWith=Masks.PlayerSolid|Contents.PhysicsProp};
        // A seed bot can stop on a ledge or a ramp. Search real supported floor,
        // rather than requiring four distant points to share its exact Z.
        foreach(float radius in new[]{320f,384f,512f,256f})
            for(int i=0;i<16;i++)
            {
                float yaw=i*MathF.Tau/16;
                var candidate=first+new Vector3(MathF.Cos(yaw)*radius,MathF.Sin(yaw)*radius,0);
                var floor=CsTrace.TraceHullShape(NativeWorld.ToNative(candidate+Vector3.UnitZ*128),NativeWorld.ToNative(candidate-Vector3.UnitZ*384),mins,maxs,null,options);
                if(!floor.DidHit() || floor.IsAllSolid || floor.Normal.Z<.7f) continue;
                var feet=NativeWorld.FromNative(floor.EndPos);
                if(TryStandingPosition(null,feet,out var safe) && Vector3.Distance(safe,first)>=250) return safe;
            }
        return null;
    }
    private void RegisterDummyDiagnostic()
    {
        RegisterDummyCapacityDiagnostic();
        RegisterDummyFlashDiagnostic();
        RegisterDummySpawnHook();
        RegisterEventHandler<EventHegrenadeDetonate>((ev,info)=>
        {
            if(dummyCheckPending && ev.Entityid==dummyDiagnosticGrenade)
            {dummyDiagnosticDetonated=true;Say(null,"DUMMYCHECK real HE detonation event received.");}
            return HookResult.Continue;
        });
        AddCommand("css_nl_dummystatus","Read-only native bot/model/ownership state",(caller,command)=>
        {
            if(caller is not null) return;
            Say(null,$"DUMMYSTATUS creating={creatingDummy}; removing={dummyRemoving}; owned={string.Join(",",effectDummies.Keys)}; quotaLease={dummyQuotaLease}; quota={ConVar.Find("bot_quota")?.StringValue}; clientImmunityOverride={ConVar.Find("sv_disable_immunity_alpha")?.StringValue}; renderLease={dummyOriginalImmunityAlpha is not null}");
            foreach(var bot in Utilities.GetPlayers().Where(p=>p.IsBot && p.IsValid))
                if(bot.PlayerPawn.Value is {IsValid:true} pawn)
                {
                    Say(null,$"DUMMYSTATUS slot={bot.Slot}; name={bot.PlayerName}; controller={bot.EntityHandle.Raw}; pawn={pawn.EntityHandle.Raw}; alive={pawn.LifeState==0}; model={DummyModel(pawn)}; render={pawn.RenderMode}/{pawn.Render.A}; fx={pawn.RenderFX}; fade={pawn.AllowFadeInView}/{pawn.FadeMinDist}/{pawn.FadeMaxDist}/{pawn.FadeScale}; immunity={pawn.GunGameImmunity}/{pawn.ImmuneToGunGameDamageTime}/{pawn.GunGameImmunityColor.A}; controllerAlive={bot.PawnIsAlive}; playerState={pawn.PlayerState}; spawning={pawn.IsSpawning}/{pawn.Respawning}; movedSinceSpawn={pawn.HasMovedSinceSpawn}; mesh={pawn.CBodyComponent?.SceneNode?.GetSkeletonInstance().ModelState.MeshGroupMask}; effects={pawn.Effects}; feet={pawn.AbsOrigin}; hp={pawn.Health}; damage={pawn.TakesDamage}; ai={pawn.BotAllowActive}; move={pawn.MoveType}; weapons={pawn.WeaponServices?.MyWeapons.Count}");
                    var scene=pawn.CBodyComponent?.SceneNode;var render=pawn.CRenderComponent;var target=FindDummy(bot);
                    Say(null,$"DUMMYRENDER slot={bot.Slot}; team={bot.TeamNum}/{pawn.TeamNum}; scale={scene?.Scale}/{scene?.AbsScale}; dormant={scene?.Dormant}; renderEnabled={render?.EnableRendering}; interpolationReady={render?.InterpolationReadyToDraw}; withViewModels={render?.IsRenderingWithViewModels}; anim={pawn.AnimGraphUpdateEnabled}; observer={pawn.ObserverServices?.ObserverMode}; expectedModel={target?.PlayerModel}; expectedMesh={target?.PlayerMeshMask}; modelPawn={target?.ModelPawn}; ownerSnapshotIncluded={target?.OwnerTransmitIncluded}/{target?.OwnerTransmitSamples}; snapshotAge={(target?.OwnerTransmitSamples>0 ? Server.CurrentTime-target.OwnerTransmitSampleTime : -1)}; clientPixels=unverified");
                }
        });
        AddCommand("css_nl_dummycheck","Empty-server dummy spawn/model/reset/remove/cancel checks",(caller,command)=>
        {
            if(caller is not null || !loaded || !Practice || Utilities.GetPlayers().Any(p=>!p.IsBot) || dummyCheckPending ||
                creatingDummy || dummyRemoving || effectDummies.Count>0 || measurementPawn is not null || job is not null || flashJob is not null || flashSearchCheckPending || flashVerificationCheckPending || DummyLayoutLoadActive || placementCheck is not null || effectStressCheck is not null)
            {Say(caller,"DUMMYCHECK refused: empty practice server, no existing dummy/operation required.");return;}
            var owner=Utilities.GetPlayers().FirstOrDefault(p=>p.IsValid && p.IsBot && p.PlayerPawn.Value is {IsValid:true,LifeState:0});
            if(owner is null) {Say(null,"DUMMYCHECK refused: existing idle bot required; only diagnostic-owned added bot will be removed.");return;}
            if(ConVar.Find("bot_stop")?.StringValue is not ("0" or "false"))
            {Say(null,"DUMMYCHECK refused: bot_stop must be 0 so global AI stopping cannot hide failures.");return;}
            var ownerPawn=owner.PlayerPawn.Value!;var ownerMove=ownerPawn.MoveType;var ownerActual=ownerPawn.ActualMoveType;
            bool ownerActive=ownerPawn.BotAllowActive,ownerAi=ownerPawn.Bot?.AllowActive ?? false;
            var original=Utilities.GetPlayers().Select(p=>p.EntityHandle.Raw).ToHashSet();
            int quota=original.Count,epoch=grenadeEpoch,passed=0;
            string? originalAlpha=ConVar.Find("sv_disable_immunity_alpha")?.StringValue;
            var feet=NativeWorld.FromNative(owner.PlayerPawn.Value!.AbsOrigin!);Vector3? site=null;
            foreach(var offset in new[]{new Vector3(64,0,0),new Vector3(-64,0,0),new Vector3(0,64,0),new Vector3(0,-64,0)})
                // The roaming seed bot can be crouched; use a full standing
                // hull so its shorter hull cannot pick an invalid dummy site.
                if(TryStandingPosition(null,feet+offset,out var supported)) {site=supported;break;}
            if(site is null) {Say(null,"DUMMYCHECK refused: no nearby safe test floor.");return;}
            ownerPawn.BotAllowActive=false;if(ownerPawn.Bot is { } ownerBot) ownerBot.AllowActive=false;
            SetMovement(ownerPawn,MoveType_t.MOVETYPE_NONE,MoveType_t.MOVETYPE_NONE);
            uint controller=owner.EntityHandle.Raw;var pose=new EffectPose(site.Value,64,new(75,90,NadeLab.Core.ThrowMode.Left),100);
            int phase=0;float deadline=Server.CurrentTime+14,testReleaseAt=0;CBaseCSGrenadeProjectile? testGrenade=null;EffectDummy? target=null,second=null;uint? added=null;
            void Require(bool good,string detail)
            {if(!good) throw new InvalidOperationException(detail);passed++;Say(null,"DUMMYCHECK PASS: "+detail);}
            void Done(string? error=null)
            {
                dummyCheckPending=false;dummyDiagnosticGrenade=null;dummyDiagnosticDetonated=false;
                ConVar.Find("bot_stop")!.StringValue="0";
                if(testGrenade is {IsValid:true}) testGrenade.Remove();RemoveEffectDummy();
                if(ownerPawn.IsValid) {ownerPawn.BotAllowActive=ownerActive;if(ownerPawn.Bot is { } ai) ai.AllowActive=ownerAi;SetMovement(ownerPawn,ownerMove,ownerActual);}
                Say(null,$"DUMMYCHECK DONE {(error is null ? "PASS" : "FAIL")}: passed={passed}; error={error ?? "none"}; native model/network state only, client pixels unverified.");
            }
            void Step()
            {
                if(!dummyCheckPending) return;
                try
                {
                    if(!loaded || !Practice || epoch!=grenadeEpoch || Utilities.GetPlayers().Any(p=>!p.IsBot) || !owner.IsValid || owner.EntityHandle.Raw!=controller)
                        throw new InvalidOperationException("zero-human/world/owner guard changed");
                    if(Server.CurrentTime>deadline) throw new InvalidOperationException($"phase {phase} timed out; creating={creatingDummy}; removing={dummyRemoving}");
                    if(phase is 0 or 2)
                    {
                        if(!creatingDummy && effectDummies.Count>0 && (target ?? SelectedDummy(owner)) is {Ready:true})
                        {
                            target ??= SelectedDummy(owner)!;var pawn=target.Bot.PlayerPawn.Value!;
                            Say(null,$"DUMMYCHECK INFO: pawn state={pawn.PlayerState}; spawning={pawn.IsSpawning}/{pawn.Respawning}; movedSinceSpawn={pawn.HasMovedSinceSpawn}; model={DummyModel(pawn)}; render={pawn.RenderMode}/{pawn.Render.A}; clientImmunityOverride={ConVar.Find("sv_disable_immunity_alpha")?.StringValue}");
                            Require(!string.IsNullOrWhiteSpace(DummyModel(pawn)) && pawn.Render.A==255 && pawn.RenderMode==RenderMode_t.kRenderNormal && (pawn.Effects&BodyHiddenBits)==0,"spawn/reset has normal native model/render flags; client pixels still unverified");
                            Require(DummyMeshReady(target,pawn),$"model-specific third-person mesh restored: model={target.PlayerModel}; mask={target.PlayerMeshMask}");
                            Require(DummyOpaque(target,pawn),"all independent opacity/fade/immunity channels restored");
                            Require(ConVar.Find("sv_disable_immunity_alpha")?.GetPrimitiveValue<bool>()==true && dummyOriginalImmunityAlpha is not null,"active targets lease the client rendering override");
                            Require(DummyMarker(target) is {IsValid:true},"owned world letter marker exists");
                            Require(pawn.Health==100 && pawn.ArmorValue==100 && pawn.TakesDamage && pawn.MoveType==MoveType_t.MOVETYPE_NONE,"100HP/armor/damage/freeze state correct");
                            Require(!pawn.IsSpawning && !pawn.Respawning,"native spawn has completed before reporting the target ready");
                            Require(!pawn.BotAllowActive && pawn.Bot?.AllowActive==false && (pawn.Flags&(uint)PlayerFlags.FL_FROZEN)==0 && !pawn.WeaponServices!.MyWeapons.Any(w=>w.Value is {IsValid:true}),"AI disabled and unarmed; temporary spawn freeze released with bot_stop 0");
                            Require(Vector3.Distance(NativeWorld.FromNative(pawn.AbsOrigin!),pose.Feet)<4,$"dummy is at recorded feet: actual={pawn.AbsOrigin}, saved={pose.Feet}, safe={target.PositionedFeet}");
                            Require(MathF.Abs(pawn.AbsRotation!.X)<.1f && MathF.Abs(pawn.EyeAngles.X-pose.View.Pitch)<.1f,"steep recorded eye pitch does not rotate player model into ground");
                            Require(Utilities.GetPlayers().Count==original.Count+(phase==0 ? 1 : 2) && original.All(h=>Utilities.GetPlayers().Any(p=>p.EntityHandle.Raw==h)),"exact quota-only target count; preexisting bots untouched");
                            if(phase==0)
                            {
                                // Reproduce the removed production call only in
                                // this isolated test, then restore its baseline.
                                ulong beforeModel=pawn.CBodyComponent!.SceneNode!.GetSkeletonInstance().ModelState.MeshGroupMask;
                                pawn.SetModel(target.PlayerModel!);
                                ulong afterModel=pawn.CBodyComponent!.SceneNode!.GetSkeletonInstance().ModelState.MeshGroupMask;
                                Say(null,$"DUMMYCHECK INFO: same-model SetModel mesh before={beforeModel}; after={afterModel}; expected={target.PlayerMeshMask}; model={target.PlayerModel}");
                                ShowDummyModel(target,pawn);
                                Require(DummyMeshReady(target,pawn),"same-model diagnostic mutation restores the saved native body mask");
                                added=target.Handle;pawn.RenderMode=RenderMode_t.kRenderNone;pawn.Render=Color.Transparent;pawn.Effects|=BodyHiddenBits;
                                pawn.RenderFX=RenderFx_t.kRenderFxFadeOut;pawn.AllowFadeInView=true;pawn.FadeMaxDist=32;pawn.FadeScale=.1f;
                                pawn.GunGameImmunityColor=Color.FromArgb(32,255,255,255);pawn.GunGameImmunity=true;pawn.ImmuneToGunGameDamageTime=Server.CurrentTime+20;
                                // SAS mask 1 is nonzero but has no body meshes.
                                // Phoenix legitimately uses 1, so corrupt it to 0.
                                ulong brokenMask=KnownDummyMesh(target.PlayerModel).BodyBits==12 ? 1UL : 0UL;
                                SetDummyMesh(pawn,brokenMask);
                                Require(!DummyMeshReady(target,pawn),$"body-less mesh mask {brokenMask} is rejected for {target.PlayerModel}");
                                var secondSite=FindDummyDiagnosticSite(pose.Feet);
                                if(secondSite is null) throw new InvalidOperationException("no second grounded standing hull in diagnostic scan");
                                PlaceEffectDummyCore(owner,pose with {Feet=secondSite.Value,Armor=0});phase=9;deadline=Server.CurrentTime+10;AddTimer(.1f,Step);return;
                            }
                            Require(target.Handle==added,"death/reset reuses original owned controller");
                            var stableFeet=NativeWorld.FromNative(pawn.AbsOrigin!);
                            pawn.FlashDuration=5;pawn.BlindStartTime=Server.CurrentTime;pawn.BlindUntilTime=Server.CurrentTime+5;
                            target.HasBlindMeasurement=true;target.LastBlindDuration=5;target.BlindUntil=Server.CurrentTime+5;
                            Require(DummyRosterText(owner)?.Contains("致盲5.0s")==true,"compact roster shows the active blind remaining time");
                            for(int reset=0;reset<3;reset++) ResetDummy(target);
                            Require(Vector3.Distance(stableFeet,NativeWorld.FromNative(pawn.AbsOrigin!))<.01f && target.Pose.Feet==pose.Feet,"repeated reset preserves recorded and placed feet without height drift");
                            Require(pawn.FlashDuration==0 && pawn.BlindUntilTime==0 && pawn.BlindStartTime==0 && !target.HasBlindMeasurement,"reset clears native active flash and per-target measurement history");
                            RemoveDummy(target);phase=3;AddTimer(1.5f,Step);return;
                        }
                    }
                    else if(phase==9 && !creatingDummy && effectDummies.Count==2)
                    {
                        second=effectDummies.Values.Single(d=>d.Handle!=target!.Handle);
                        Require(target!.Live && second.Ready && target.Handle!=second.Handle,"two independently owned targets coexist");
                        Require(second.Bot.PlayerPawn.Value!.ArmorValue==0 && target.Bot.PlayerPawn.Value!.ArmorValue==100,"per-target armor remains independent");
                        Require(second.Bot.PlayerPawn.Value!.Bot?.AllowActive==false && target.Bot.PlayerPawn.Value!.Bot?.AllowActive==false,"both targets frozen with global AI enabled");
                        Require(Utilities.GetPlayers().Count==original.Count+2,"second placement has no roaming companion bot");
                        Require(measurementPawn is null,"player targets do not overwrite isolated probe state");
                        Require(target.LabelIndex==0 && second.LabelIndex==1 && DummyLabel(target)=="A红" && DummyLabel(second)=="B蓝","stable owner-local letters and colors assigned");
                        SelectDummy(owner,target);
                        Require(SelectedDummy(owner)==target && target.MarkerText=="[A]" && second.MarkerText=="B","selection maps roster identity to world marker");
                        string roster=DummyRosterText(owner) ?? "";
                        Require(roster.Contains("A红") && roster.Contains("B蓝") && System.Text.Encoding.UTF8.GetByteCount(roster)<=900 && roster.Split('\n').Length<=9,"persistent roster includes every target within byte/line budgets");
                        Require(RunPredictionDiagnostic(owner),"live two-target prediction diagnostic passes without changing measured health/armor/blindness");
                                // Let the engine's weapon logic create a fully
                                // initialized grenade. A frozen bot never ticks
                                // its weapon, so enable only the test thrower.
                                owner.RemoveWeapons();owner.GiveNamedItem("weapon_hegrenade");
                        phase=5;deadline=Server.CurrentTime+14;AddTimer(.25f,Step);return;
                    }
                    else if(phase==1)
                    {
                        target ??= SelectedDummy(owner)!;var pawn=target.Bot.PlayerPawn.Value!;
                        Require(pawn.Health is >0 and <100,"real HE lowered HP and protection/hold did not heal it");
                        Require(dummyDiagnosticDetonated && pawn.ArmorValue<100,$"HE blast confirmed beyond contact damage: hp={pawn.Health}; armor={pawn.ArmorValue}");
                        Require(target.DamageTotal==100-pawn.Health && target.LastArmor==pawn.ArmorValue,"feedback agrees with actual HP/armor loss");
                        Require(target.DamageTotal>0 && target.Feedback is not null && Get(owner).LastEffect?.Contains("实测")==true,"unarmed/unrecorded native hurt reaches owner feedback");
                        Say(null,$"DUMMYCHECK INFO: late spawning={pawn.IsSpawning}; movedSinceSpawn={pawn.HasMovedSinceSpawn}; botSpawned={pawn.Bot?.HasSpawned}");
                        Require(Vector3.Distance(NativeWorld.FromNative(pawn.AbsOrigin!),pose.Feet)<4 && pawn.Bot?.AllowActive==false && ConVar.Find("bot_stop")!.StringValue=="0","dummy stays put with normal server AI enabled");
                        Require(pawn.RenderMode==RenderMode_t.kRenderNormal && pawn.Render.A==255 && (pawn.Effects&BodyHiddenBits)==0,"late hidden rendering repaired");
                        Require(DummyOpaque(target,pawn),"late fade, immunity and model-specific body mesh corruption repaired");
                        Require(DummyRosterText(owner)?.Contains($"掉血{target.LastDamage}")==true,"native damage reaches persistent per-target roster");
                        pawn.CommitSuicide(false,true);phase=2;deadline=Server.CurrentTime+7;
                        AddTimer(.3f,()=>{ResetDummy(target);Step();});return;
                    }
                    else if(phase==5)
                    {
                        var weapon=ownerPawn.WeaponServices!.MyWeapons.Select(h=>h.Value).FirstOrDefault(w=>w is {IsValid:true} && w.DesignerName=="weapon_hegrenade")?.As<CBaseCSGrenade>();
                        if(weapon is not null)
                        {
                            ownerPawn.WeaponServices.ActiveWeapon.Raw=weapon.EntityHandle.Raw;
                            // Pause AI decisions only while deploying the test
                            // weapon; keep native weapon processing active.
                            // Placement and post-blast holding both use stop=0.
                            ConVar.Find("bot_stop")!.StringValue="1";
                            ownerPawn.BotAllowActive=true;if(ownerPawn.Bot is { } ai) ai.AllowActive=true;
                            testReleaseAt=Server.CurrentTime+1;phase=8;
                        }
                    }
                    else if(phase==8 && Server.CurrentTime>=testReleaseAt)
                    {
                        var weapon=ownerPawn.WeaponServices!.ActiveWeapon.Value?.As<CBaseCSGrenade>() ?? throw new InvalidOperationException("test HE was not equipped");
                        weapon.PinPulled=false;weapon.Redraw=false;weapon.ThrowStrength=0;weapon.IsHeldByPlayer=true;
                        testReleaseAt=Server.CurrentTime;weapon.ThrowTime=testReleaseAt+.1f;phase=6;
                    }
                    else if(phase==6)
                    {
                        var grenade=Utilities.FindAllEntitiesByDesignerName<CBaseCSGrenadeProjectile>("hegrenade_projectile")
                            .FirstOrDefault(g=>g.IsValid && g.Thrower.Value?.EntityHandle.Raw==ownerPawn.EntityHandle.Raw && g.CreateTime>=testReleaseAt-.1f);
                        if(grenade is not null)
                        {
                            testGrenade=grenade;dummyDiagnosticGrenade=grenade.Index;dummyDiagnosticDetonated=false;
                            ConVar.Find("bot_stop")!.StringValue="0";
                            ownerPawn.BotAllowActive=false;if(ownerPawn.Bot is { } ai) ai.AllowActive=false;
                            grenade.Teleport(position:pose.Feet+new Vector3(0,0,80),velocity:Vector3.Zero);
                            phase=7;Say(null,"DUMMYCHECK real engine weapon projectile captured for blast test.");
                        }
                    }
                    else if(phase==7 && dummyDiagnosticDetonated && target?.Bot.PlayerPawn.Value is {IsValid:true,Health:<100})
                    {phase=1;AddTimer(1.2f,Step);return;}
                    else if(phase==3)
                    {
                        Require(effectDummies.Count==1 && second is {Live:true} && !creatingDummy && !dummyRemoving,"removing selected target preserves the other target");
                        Require(second!.Bot.PlayerPawn.Value is {IsValid:true,LifeState:0} remaining && remaining.Bot?.AllowActive==false,"remaining target stays frozen");
                        Require(DummyActionTarget(owner,added) is null && !second.Removing && DummyActionTarget(owner)?.Handle==second.Handle,
                            "stale A menu action is rejected after selection falls back to B");
                        // Issue a third quota request, then delete a different
                        // target in the same frame before the engine can spawn.
                        PlaceEffectDummyCore(owner,pose);
                        if(dummyCreation is not { } pending) throw new InvalidOperationException("third target request not created");
                        pending.ReadyAt=Server.CurrentTime;TickDummyCreation();
                        Require(pending.Requested && pending.Claimed is null,"third quota request is pending before native spawn");
                        RemoveDummy(second);
                        Require((ConVar.Find("bot_quota")?.GetPrimitiveValue<int>() ?? -1)==original.Count+1,"deleting another target retains pending quota reservation");
                        phase=11;deadline=Server.CurrentTime+9;AddTimer(.2f,Step);return;
                    }
                    else if(phase==11 && !creatingDummy && !dummyRemoving && effectDummies.Count==1)
                    {
                        Require(effectDummies.Values.Single().Ready,"pending target completes after deleting another target");
                        Require(effectDummies.Values.Single().LabelIndex==0,"removed owner-local A identity is reused without renumbering survivors");
                        Require(Utilities.GetPlayers().Count==original.Count+1,"concurrent target removal has no orphan/replacement");
                        RemovePlayerEffectDummies(owner.Slot);phase=10;AddTimer(1.5f,Step);return;
                    }
                    else if(phase==10)
                    {
                        Require(effectDummies.Count==0 && !creatingDummy && !dummyRemoving && measurementPawn is null,"owner disconnect path clears owned targets and probe state");
                        Require(Utilities.GetPlayers().Select(p=>p.EntityHandle.Raw).ToHashSet().SetEquals(original),"no official replacement bot after quota tick");
                        Require((ConVar.Find("bot_quota")?.GetPrimitiveValue<int>() ?? -2)==quota,"owner disconnect leaves no quota reservation for departing targets");
                        Require(dummyOriginalImmunityAlpha is null && ConVar.Find("sv_disable_immunity_alpha")?.StringValue==originalAlpha,"last target removal restores the prior client rendering setting");
                        PlaceEffectDummyCore(owner,pose);RemoveEffectDummy();phase=4;deadline=Server.CurrentTime+7;AddTimer(2,Step);return;
                    }
                    else if(phase==4 && !creatingDummy && !dummyRemoving)
                    {
                        Require(effectDummies.Count==0 && measurementPawn is null,"immediate cancel clears in-flight dummy");
                        Require(Utilities.GetPlayers().Select(p=>p.EntityHandle.Raw).ToHashSet().SetEquals(original) && (ConVar.Find("bot_quota")?.GetPrimitiveValue<int>() ?? -2)==quota,"cancel leaves no orphan/replacement and restores quota");
                        PlaceEffectDummyCore(owner,pose);
                        if(dummyCreation is not { } pending) throw new InvalidOperationException("cancelled quota request not created");
                        pending.ReadyAt=Server.CurrentTime;TickDummyCreation();
                        Require(pending.Requested,"cancellation exercises an issued quota request");
                        RemovePlayerEffectDummies(owner.Slot);phase=12;deadline=Server.CurrentTime+5;AddTimer(1.5f,Step);return;
                    }
                    else if(phase==12 && !creatingDummy && !dummyRemoving)
                    {
                        Require(effectDummies.Count==0 && Utilities.GetPlayers().Select(p=>p.EntityHandle.Raw).ToHashSet().SetEquals(original),"owner departure cancels pending creation without orphan bots");
                        Require((ConVar.Find("bot_quota")?.GetPrimitiveValue<int>() ?? -1)==quota,"pending owner departure restores quota");
                        Require(dummyOriginalImmunityAlpha is null && ConVar.Find("sv_disable_immunity_alpha")?.StringValue==originalAlpha,"pending owner departure restores client rendering lease");
                        var alpha=ConVar.Find("sv_disable_immunity_alpha")!;
                        string alphaBefore=alpha.StringValue;
                        try
                        {
                            alpha.SetValue(true);AcquireDummyRendering();alpha.SetValue(false);ReleaseDummyRendering();
                            Require(!alpha.GetPrimitiveValue<bool>(),"rendering cleanup preserves a later external administrator change");
                        }
                        finally {ReleaseDummyRendering();alpha.StringValue=alphaBefore;}
                        Done();return;
                    }
                    AddTimer(.1f,Step);
                }
                catch(Exception ex) {Done(ex.Message);}
            }
            // Regression: failed user attempts left quota=4 with fewer actual
            // bots. The old count-delta claim produced roaming invulnerable bots.
            ConVar.Find("bot_quota")?.SetValue(quota+3);
            dummyCheckPending=true;PlaceEffectDummyCore(owner,pose);AddTimer(.1f,Step);
            Say(null,"DUMMYCHECK START: stale quota, quota-only creation, multiple targets, bot_stop 0, native damage, reset, selected/remove-all and cancellation.");
        });
    }
}
