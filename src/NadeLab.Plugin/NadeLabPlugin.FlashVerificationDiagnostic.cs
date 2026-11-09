using System.Diagnostics;
using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using NadeLab.Core;
using CsTrace=CounterStrikeSharp.API.Modules.Utils.Trace;
using TraceOptions=CounterStrikeSharp.API.Modules.Utils.TraceOptions;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private bool flashVerificationCheckPending;
    private Action? stopFlashVerificationCheck;
    private void StopFlashVerificationDiagnostic()=>stopFlashVerificationCheck?.Invoke();
    private void RegisterFlashVerificationDiagnostic()=>AddCommand("css_nl_flashverifycheck",
        "Empty-server real flash verification event-chain and reset diagnostic",(caller,command)=>
    {
        if(caller is not null || !loaded || !Practice || !BenchmarkIdle || flashVerificationCheckPending ||
            Utilities.GetPlayers().Any(p=>!p.IsBot))
        {Say(caller,"FLASHVERIFY refused: console, zero humans and idle practice server required.");return;}
        string fixture=command.ArgCount==1 ? "auto" : command.ArgByIndex(1).ToLowerInvariant();
        if(command.ArgCount>2 || fixture is not ("auto" or "regression1" or "regression2" or "occlusion-predicted" or "occlusion-actual" or "occlusion-visible" or "occlusion-cache") ||
            (fixture!="auto" && Server.MapName!="de_mirage"))
        {Say(null,"FLASHVERIFY refused: regression1/regression2 or occlusion-predicted/actual/visible/cache require de_mirage.");return;}
        Vector3? fixedBurst=fixture switch
        {
            "occlusion-predicted"=>new(-1634.4878f,-2887.9739f,7.3498716f),
            "occlusion-actual" or "occlusion-cache"=>new(-1638.2234f,-2858.6084f,.03867817f),
            // One quarter of the recorded eye-to-predicted-burst segment,
            // before the measured reverse-ray wall hit at fraction ~.434.
            // Native hull and all five direct rays must still validate it.
            "occlusion-visible"=>Vector3.Lerp(new(-1859.6028f,-1895.3135f,-205.98596f),new(-1634.4878f,-2887.9739f,7.3498716f),.25f),
            _=>null
        };
        var recordedFeet=new Vector3(-1859.6028f,-1895.3135f,-268.54846f);
        var recordedPlacementFeet=new Vector3(-1859.6028f,-1895.3135f,-269.04846f);
        var recordedEye=new Vector3(-1859.6028f,-1895.3135f,-205.98596f);
        var recordedView=new Aim(-3.6044781f,-39.11609f,ThrowMode.Left);
        var role=fixedBurst is null ? FlashRole.MustFlash : FlashRole.Protect;
        // Replay the two native support scans from their recorded seed origins
        // (trial IDs 4f5e3e08... and bfd23eab...). Target feet are measured anew
        // by the real hull scan, never inferred from an assumed eye height.
        // The current seed bot remains at its own backed-up position.
        Vector3? regressionOrigin=fixture switch
        {
            "regression1"=>new(-1776,-1976,-263.96875f),
            "regression2"=>new(-1656,-1800,-267.20642f),
            _=>null
        };
        var owner=Utilities.GetPlayers().FirstOrDefault(p=>p.IsValid && p.IsBot && !IsEffectDummy(p) &&
            p.PlayerPawn.Value is {IsValid:true,LifeState:0,AbsOrigin:not null} && !ThrowInProgress(p));
        if(owner?.PlayerPawn.Value is not {IsValid:true,LifeState:0,AbsOrigin:not null} source || ConVar.Find("bot_stop") is not { } botStop)
        {Say(null,"FLASHVERIFY refused: existing living idle seed bot and bot_stop are required.");return;}
        if(source.WeaponServices is null || source.WeaponServices.MyWeapons.Any(w=>w.Value is {IsValid:true,DesignerName:"weapon_flashbang"}) ||
            Utilities.FindAllEntitiesByDesignerName<CBaseCSGrenadeProjectile>("flashbang_projectile").Any(g=>g.IsValid))
        {Say(null,"FLASHVERIFY refused: no prior held/flying flash may overlap this isolated diagnostic.");return;}

        int epoch=grenadeEpoch,phase=0,passed=0,siteIndex=0,sourceIndex=0;bool stopped=false,cleaning=false;
        uint ownerHandle=owner.EntityHandle.Raw,pawnHandle=source.EntityHandle.Raw;
        string map=Server.MapName,oldStop=botStop.StringValue;
        var oldFeet=NativeWorld.FromNative(source.AbsOrigin);var oldVelocity=NativeWorld.FromNative(source.AbsVelocity);
        var oldView=new Vector3(source.EyeAngles.X,source.EyeAngles.Y,source.EyeAngles.Z);
        var oldNativeView=new Vector3(source.V_angle.X,source.V_angle.Y,source.V_angle.Z);
        var oldRotation=source.AbsRotation is { } rotation ? new Vector3(rotation.X,rotation.Y,rotation.Z) : oldView;
        var oldMove=source.MoveType;var oldActualMove=source.ActualMoveType;
        bool oldActive=source.BotAllowActive,oldAi=source.Bot?.AllowActive ?? false;
        float oldFlash=source.FlashDuration,oldBlindUntil=source.BlindUntilTime,oldBlindStart=source.BlindStartTime,oldAlpha=source.FlashMaxAlpha;
        uint oldWeapon=source.WeaponServices.ActiveWeapon.Raw;
        var oldWeapons=source.WeaponServices.MyWeapons.Select(w=>w.Raw).ToHashSet();
        bool hadSession=sessions.TryGetValue(owner.Slot,out var oldSession);
        var originalControllers=Utilities.GetPlayers().Select(p=>p.EntityHandle.Raw).ToHashSet();
        var profile=Grenades.Bootstrap(GrenadeKind.Flash,physics);
        var aim=new Aim(-60,source.EyeAngles.Y,ThrowMode.Right);
        var start=new ThrowStart(oldFeet,source.ViewOffset.Z);
        var temporary=new Session {SteamId=owner.SteamID,Kind=GrenadeKind.Flash,Revision=(oldSession?.Revision ?? 0)+1,
            FlashMode=true,Start=start,RequestedStart=start,SearchPhysics=profile,ReferenceView=aim,HudEnabled=false,GuideEnabled=false};
        EffectDummy? target=null;CBaseCSGrenadeProjectile? projectile=null;SmokeSimulation? simulation=null;
        FlashVerificationRound? round=null;FlashPlanState? plan=null;Candidate? candidate=null;
        Vector3 targetFeet=default;float releaseAt=0,readyAt=0;uint? projectileIndex=null;
        long began=Stopwatch.GetTimestamp();float serverBegan=Server.CurrentTime;
        void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException(message);passed++;Say(null,"FLASHVERIFY PASS: "+message);}
        bool SameWorld()=>loaded && Practice && epoch==grenadeEpoch && map==Server.MapName &&
            owner.IsValid && owner.EntityHandle.Raw==ownerHandle && owner.PlayerPawn.Value is {IsValid:true,LifeState:0} current &&
            current.EntityHandle.Raw==pawnHandle && !Utilities.GetPlayers().Any(p=>!p.IsBot) && job is null && flashJob is null;

        void CleanupOwned()
        {
            if(cleaning)return;cleaning=true;
            var errors=new List<string>();
            void Attempt(string stage,Action action){try{action();}catch(Exception ex){errors.Add(stage+": "+ex.Message);}}
            Attempt("verification",()=>
            {
                if(flashVerification.TryGetValue(owner.Slot,out var active) && ReferenceEquals(active.Session,temporary))
                    ClearFlashVerification(owner.Slot,"无人诊断清理");
            });
            Attempt("projectile",()=>{if(projectile is {IsValid:true})projectile.Remove();});
            Attempt("remaining projectile",()=>
            {
                foreach(var released in Utilities.FindAllEntitiesByDesignerName<CBaseCSGrenadeProjectile>("flashbang_projectile")
                    .Where(g=>g.IsValid && g.Thrower.Value?.EntityHandle.Raw==pawnHandle && g.CreateTime>=serverBegan).ToArray())released.Remove();
            });
            Attempt("event ledger",()=>
            {
                if(projectileIndex is { } id){grenadeFlights.Remove(id);grenadeEffects.Remove(id);}
                recentGrenadeReleases.RemoveAll(r=>r.Slot==owner.Slot && r.Steam==temporary.SteamId && r.At>=serverBegan);
            });
            Attempt("pending dummy",()=>
            {
                if(dummyCreation is { } pending && pending.Owner==ownerHandle && pending.Epoch==epoch)
                {
                    pending.Cancelled=true;
                    try
                    {
                        SetDummyQuota();
                        if(pending.Requested && pending.Claimed is null)
                        {
                            var added=Utilities.GetPlayers().Where(p=>p.IsValid && p.IsBot && !pending.Known.Contains(p.EntityHandle.Raw)).ToArray();
                            if(added.Length==1)ClaimPendingDummy(pending,added[0]);
                        }
                    }
                    finally {dummyCreation=null;SetDummyQuota();}
                }
            });
            // Idle entry requires no pre-existing effect dummies. These handles
            // therefore belong only to this diagnostic's one creation request.
            foreach(var owned in effectDummies.Values.Where(d=>d.Owner==ownerHandle && d.OwnerSteam==temporary.SteamId).ToArray())
                Attempt("owned dummy",()=>RemoveDummy(owned));
            Attempt("dummy state",()=>{selectedDummies.Remove(ownerHandle);RemoveDummyPredictions(owner.Slot);ReleaseDummyQuota();});
            Attempt("session",()=>
            {
                if(sessions.TryGetValue(owner.Slot,out var current) && ReferenceEquals(current,temporary))
                {if(hadSession)sessions[owner.Slot]=oldSession!;else sessions.Remove(owner.Slot);}
            });
            if(errors.Count>0)throw new InvalidOperationException(string.Join("; ",errors));
        }
        void RestoreSeed()
        {
            if(epoch!=grenadeEpoch)return;
            if(ConVar.Find("bot_stop") is { } stop)stop.StringValue=oldStop;
            if(!source.IsValid || source.EntityHandle.Raw!=pawnHandle ||
                !owner.IsValid || owner.EntityHandle.Raw!=ownerHandle)return;
            foreach(var weapon in source.WeaponServices?.MyWeapons.Select(w=>w.Value).Where(w=>w is {IsValid:true}).ToArray() ?? [])
                if(weapon!.DesignerName=="weapon_flashbang" && !oldWeapons.Contains(weapon.EntityHandle.Raw))weapon.Remove();
            if(source.WeaponServices is { } weapons && weapons.MyWeapons.Any(w=>w.Raw==oldWeapon))weapons.ActiveWeapon.Raw=oldWeapon;
            source.FlashDuration=oldFlash;source.BlindUntilTime=oldBlindUntil;source.BlindStartTime=oldBlindStart;source.FlashMaxAlpha=oldAlpha;
            source.Teleport(position:oldFeet,angles:oldRotation,velocity:oldVelocity);
            source.EyeAngles.X=oldView.X;source.EyeAngles.Y=oldView.Y;source.EyeAngles.Z=oldView.Z;
            source.V_angle.X=oldNativeView.X;source.V_angle.Y=oldNativeView.Y;source.V_angle.Z=oldNativeView.Z;
            SetMovement(source,oldMove,oldActualMove);
            source.BotAllowActive=oldActive;if(source.Bot is { } ai)ai.AllowActive=oldAi;
        }
        void Finish(string? error=null)
        {
            if(stopped)return;stopped=true;
            try {if(epoch==grenadeEpoch)CleanupOwned();}
            catch(Exception cleanupError){error=(error is null ? "" : error+"; ")+"cleanup: "+cleanupError.Message;}
            try {RestoreSeed();}
            catch(Exception restoreError){error=(error is null ? "" : error+"; ")+"seed restore: "+restoreError.Message;}
            flashVerificationCheckPending=false;stopFlashVerificationCheck=null;
            Say(null,$"FLASHVERIFY DONE {(error is null ? "PASS" : "FAIL")}: passed={passed}; error={error ?? "none"}; throwId={round?.ThrowId ?? "none"}; engine event attribution/reset only, not a manual throw, route validation, full-white duration or client animation test.");
        }
        Vector3? NextSite()
        {
            if(fixedBurst is not null)
            {
                if(siteIndex++!=0 || !TryStandingPosition(null,recordedPlacementFeet,out var fixedFeet) || Vector3.Distance(fixedFeet,recordedFeet)>.02f ||
                    Vector3.Distance(fixedFeet,oldFeet)<64)
                    throw new InvalidOperationException("recorded B support unavailable or overlaps seed bot; fixture not relocated");
                Say(null,$"FLASHVERIFY FIXTURE: case={fixture}; recordedFeet={recordedFeet}; supportedFeet={fixedFeet}; recordedEye={recordedEye}; map={map}; fixedPointEffectOnly=true.");
                // The production dummy placement performs its own support
                // correction once. Passing the already-raised value raises
                // the fixture twice and no longer reproduces the saved pose.
                return recordedPlacementFeet;
            }
            if(siteIndex>=64)throw new InvalidOperationException("no supported diagnostic target site near the seed bot");
            int index=siteIndex++;float radius=new[]{320f,384f,512f,256f}[index/16],yaw=(index%16)*MathF.Tau/16;
            var anchor=regressionOrigin ?? oldFeet;
            var point=anchor+new Vector3(MathF.Cos(yaw)*radius,MathF.Sin(yaw)*radius,0);
            var floor=CsTrace.TraceHullShape(NativeWorld.ToNative(point+Vector3.UnitZ*128),NativeWorld.ToNative(point-Vector3.UnitZ*384),
                new(-16,-16,0),new(16,16,72),null,new TraceOptions {InteractsAs=Contents.Player,InteractsWith=Masks.PlayerSolid|Contents.PhysicsProp});
            if(!floor.DidHit() || floor.IsAllSolid || floor.Normal.Z<.7f ||
                !TryStandingPosition(null,NativeWorld.FromNative(floor.EndPos),out var safe) || Vector3.Distance(safe,anchor)<250)return null;
            if(regressionOrigin is not null && Vector3.Distance(safe,oldFeet)<64)
                throw new InvalidOperationException($"{fixture} supported regression site overlaps the current seed bot; move the seed before retrying");
            Say(null,$"FLASHVERIFY FIXTURE: case={fixture}; scanOrigin={anchor}; supportedFeet={safe}; sourceFeet={oldFeet}; sourceEyeHeight={start.EyeHeight}; map={map}.");
            return safe;
        }
        void Step()
        {
            if(stopped)return;
            try
            {
                if(!SameWorld())throw new InvalidOperationException("world, owner, zero-human or exclusive diagnostic guard changed");
                if(Stopwatch.GetElapsedTime(began).TotalSeconds>25)throw new InvalidOperationException($"strict 25-second deadline exceeded at phase {phase}");
                if(phase==0)
                {
                    if(NextSite() is { } feet)
                    {
                        targetFeet=feet;PlaceEffectDummyCore(owner,new(feet,fixedBurst is null ? 64 : 62.5625f,
                            fixedBurst is null ? new(0,0,ThrowMode.Left) : recordedView,100));
                        Require(dummyCreation is not null,"one owned dummy creation requested");phase=1;
                    }
                }
                else if(phase==1 && !creatingDummy && SelectedDummy(owner) is {Ready:true,ResetRequested:false} ready)
                {
                    target=ready;target.SearchRole=role;
                    Require(OwnedDummies(owner).Length==1 && ready.Owner==ownerHandle && ready.OwnerSteam==owner.SteamID,
                        "one ready owned dummy with complete controller identity");
                    var pawn=ready.Bot.PlayerPawn.Value!;
                    Say(null,$"FLASHVERIFY TARGET: case={fixture}; requestedFeet={targetFeet}; positionedFeet={ready.PositionedFeet}; nativeFeet={NativeWorld.FromNative(pawn.AbsOrigin!)}; viewOffset={pawn.ViewOffset.X}/{pawn.ViewOffset.Y}/{pawn.ViewOffset.Z}.");
                    var observers=new[]{new FlashObserver(ready.Handle,DummyLabel(ready),NativeWorld.FromNative(pawn.AbsOrigin!)+
                        new Vector3(pawn.ViewOffset.X,pawn.ViewOffset.Y,pawn.ViewOffset.Z),new(pawn.EyeAngles.X,pawn.EyeAngles.Y,ThrowMode.Left),role)};
                    if(fixedBurst is not null)
                    {
                        Say(null,$"FLASHVERIFY RECORDED POSE: eye={observers[0].Eye}; recordedEyeDelta={Vector3.Distance(observers[0].Eye,recordedEye):F6}; eyeView={pawn.EyeAngles}; nativeView={pawn.V_angle}; body={pawn.AbsRotation}; botEyeCache={pawn.Bot?.EyePosition}; exactHistoricalPose={Vector3.Distance(observers[0].Eye,recordedEye)<.02f}.");
                        Require(Vector3.Distance(observers[0].Eye,recordedEye)<.02f &&
                            MathF.Abs(pawn.EyeAngles.X-recordedView.Pitch)<.01f && MathF.Abs(Geometry.NormalizeYaw(pawn.EyeAngles.Y-recordedView.Yaw))<.01f,
                            "recorded observer eye and view reproduced without another support correction");
                    }
                    plan=new() {Observers=observers,Pawns=new() {[ready.Handle]=pawn.EntityHandle.Raw},Epoch=epoch,Revision=temporary.Revision,
                        Owner=ownerHandle,OwnerPawn=pawnHandle};
                    temporary.FlashPlan=plan;sessions[owner.Slot]=temporary;BindPreparation(owner,temporary);
                    simulation=new(new NativeWorld(source),profile,start,aim);phase=2;
                }
                else if(phase==2)
                {
                    var slice=Stopwatch.StartNew();int units=0;
                    while(!simulation!.Done && units++<32 && slice.Elapsed.TotalMilliseconds<.5)simulation.Step();
                    if(simulation.Done)
                    {
                        var trajectory=simulation.Result();Require(trajectory.Status==SimulationStatus.Detonated,"diagnostic fixture has a complete native-collision trajectory");
                        candidate=new(aim,trajectory,0);temporary.Selected=1;
                        temporary.Result=new(profile.Id,false,1,false,[candidate],[]);
                        plan!.Trials=new(1,plan.Observers);
                        temporary.Arm=new(candidate,start,null,temporary.Revision,Server.CurrentTime+20,Physics:profile,EffectObjective:true);
                        Require(BeginFlashVerification(owner,plan!.Observers,plan.Pawns),"production verifier starts from a clean native dummy baseline");
                        round=CurrentFlashVerification(owner);
                        Require(round is not null && round.Trial.BaselineClean && temporary.Target is null && temporary.Arm.Target is null,
                            "observer objective has no invented detonation target");
                        owner.GiveNamedItem("weapon_flashbang");phase=3;
                    }
                }
                else if(phase==3 && source.WeaponServices?.MyWeapons.Select(w=>w.Value).FirstOrDefault(w=>w is {IsValid:true,DesignerName:"weapon_flashbang"})?.As<CBaseCSGrenade>() is { } weapon)
                {
                    source.WeaponServices.ActiveWeapon.Raw=weapon.EntityHandle.Raw;
                    botStop.StringValue="1";source.BotAllowActive=true;if(source.Bot is { } ai)ai.AllowActive=true;
                    readyAt=Server.CurrentTime+1;phase=4;
                }
                else if(phase==4 && Server.CurrentTime>=readyAt)
                {
                    var held=source.WeaponServices!.ActiveWeapon.Value!.As<CBaseCSGrenade>();
                    held.PinPulled=false;held.Redraw=false;held.ThrowStrength=0;held.IsHeldByPlayer=true;
                    releaseAt=Server.CurrentTime;held.ThrowTime=releaseAt+.1f;phase=5;
                }
                else if(phase==5)
                {
                    projectile=Utilities.FindAllEntitiesByDesignerName<CBaseCSGrenadeProjectile>("flashbang_projectile")
                        .FirstOrDefault(g=>g.IsValid && g.Thrower.Value?.EntityHandle.Raw==pawnHandle && g.CreateTime>=releaseAt-.1f);
                    if(projectile is not null && round?.ThrowId is not null && grenadeFlights.TryGetValue(projectile.Index,out var flight))
                    {
                        Require(round.Trial.ReleasedAt is not null && round.Sequence==temporary.ThrowSequence &&
                            round.Projectile==projectile.Index && round.ProjectileHandle==projectile.EntityHandle.Raw && flight.Id==round.ThrowId,
                            "production OnThrown and Bind uniquely link release, sequence and projectile handle");
                        projectileIndex=projectile.Index;
                        // This real grenade will be moved to isolate event handling.
                        // Prevent both trial and telemetry from calling it a matched
                        // candidate, even if the original launch snapshot matched.
                        round.ExecutionMatched=false;flight.PoseMatched=false;flight.LaunchMatched=null;
                        int selectedBefore=temporary.Selected;
                        NextCandidate(owner,null!);
                        Require(CurrentFlashVerification(owner)==round && !round.Trial.Complete && temporary.Selected==selectedBefore,
                            "next candidate preserves the pending native flash observation");
                        botStop.StringValue="0";source.BotAllowActive=false;if(source.Bot is { } ai)ai.AllowActive=false;
                        phase=6;
                    }
                }
                else if(phase==6)
                {
                    if(projectile is not {IsValid:true} || target?.Bot.PlayerPawn.Value is not {IsValid:true} pawn)
                        throw new InvalidOperationException("native flash disappeared before diagnostic placement");
                    var eye=plan!.Observers[0].Eye;
                    if(fixedBurst is { } fixedPoint)
                    {
                        Require(EffectPlacement.Resolve(new NativeWorld(source),GrenadeKind.Flash,fixedPoint).Valid,
                            "recorded burst is clear for the grenade hull; no silent position correction");
                        if(fixture=="occlusion-cache")
                        {
                            var cache=pawn.Bot?.EyePosition ?? throw new InvalidOperationException("owned diagnostic bot eye cache unavailable");
                            var before=NativeWorld.FromNative(cache);
                            // Isolated experiment on this diagnostic's owned
                            // target only; production dummy code is unchanged.
                            cache.X=eye.X;cache.Y=eye.Y;cache.Z=eye.Z;
                            Require(Vector3.Distance(NativeWorld.FromNative(pawn.Bot!.EyePosition),eye)<.01f,"diagnostic-only eye-cache intervention applied");
                            Say(null,$"FLASHVERIFY CACHE INTERVENTION: before={before}; after={pawn.Bot.EyePosition}; actualEye={eye}; otherPoseFieldsUnchanged=true.");
                        }
                        LogFlashOcclusionProbes(source,fixedPoint,plan.Observers[0]);
                        if(fixture=="occlusion-visible")
                        {
                            var controlSight=FlashSearchSight(source,fixedPoint,plan.Observers[0]);
                            Require(controlSight.Usable && controlSight.Clear==controlSight.Total,"visible positive control has five unobstructed eye rays");
                        }
                        projectile.MoveType=MoveType_t.MOVETYPE_NONE;projectile.ActualMoveType=MoveType_t.MOVETYPE_NONE;
                        projectile.Teleport(position:fixedPoint,velocity:Vector3.Zero);
                        Say(null,$"FLASHVERIFY SOURCE: case={fixture}; requestedBurst={fixedPoint}; nativeBurst={projectile.AbsOrigin}; targetBotEyeCacheBefore={pawn.Bot?.EyePosition}; targetEye={eye}; targetUpdate={pawn.BotAllowActive}/{pawn.Bot?.AllowActive}; executionMatched=false; fixed-point effect measurement only.");
                        phase=7;AddTimer(.02f,Step);return;
                    }
                    var offset=new[]{new Vector3(64,0,0),new Vector3(40,0,48),new Vector3(0,0,64)};
                    if(sourceIndex>=offset.Length)throw new InvalidOperationException("no unobstructed source outside the target hull");
                    var point=eye+offset[sourceIndex++];
                    if(!new NativeWorld(pawn).Sweep(eye,point,2).Hit)
                    {
                        projectile.MoveType=MoveType_t.MOVETYPE_NONE;projectile.ActualMoveType=MoveType_t.MOVETYPE_NONE;
                        projectile.Teleport(position:point,velocity:Vector3.Zero);
                        Say(null,$"FLASHVERIFY SOURCE: real projectile moved {Vector3.Distance(eye,point):F1}u from dummy eyes; executionMatched=false; event chain only.");phase=7;
                    }
                }
                else if(phase==7 && round is {Published:true} && round.Trial.Complete)
                {
                    var measurements=round.Trial.Results();var row=measurements.Single();
                    if(target?.Bot.PlayerPawn.Value is {IsValid:true,AbsOrigin:not null} observed)
                    {
                        var actualEye=NativeWorld.FromNative(observed.AbsOrigin)+new Vector3(observed.ViewOffset.X,observed.ViewOffset.Y,observed.ViewOffset.Z);
                        var expected=plan!.Observers[0];
                        Say(null,$"FLASHVERIFY OBSERVATION: invalid={round.Trial.InvalidReason ?? "none"}; state={row.State}; reason={row.Reason ?? "none"}; expectedEye={expected.Eye}; actualEye={actualEye}; eyeDelta={Vector3.Distance(actualEye,expected.Eye):F3}; expectedView={expected.View.Pitch}/{expected.View.Yaw}; actualView={observed.EyeAngles.X}/{observed.EyeAngles.Y}; nativeView={observed.V_angle.X}/{observed.V_angle.Y}; botEyeCache={observed.Bot?.EyePosition}; nativeDuration={observed.FlashDuration}; alpha={observed.FlashMaxAlpha}; blindStart={observed.BlindStartTime}; blindUntil={observed.BlindUntilTime}; now={Server.CurrentTime}; ready={target.Ready}; resetting={target.ResetRequested}; samples={row.SampleCount}.");
                    }
                    bool blind=row.State==FlashMeasurementState.ObservedBlind && row.EventDuration is >0 && row.EventAt is not null;
                    bool quiet=fixedBurst is not null && row.State==FlashMeasurementState.NoBlindObserved && !row.NativeIncreased &&
                        (!target!.HasBlindMeasurement || target.LastBlindDuration==0);
                    Require(round.Trial.InvalidReason is null && (blind || quiet),
                        $"production callbacks complete native observation: {row.State}, event={row.EventDuration:F2}s, not Unknown");
                    Require(round.Trial.DetonatedAt is not null && row.SampleCount>=8 && (quiet || (row.NativeIncreased && target!.HasBlindMeasurement)),
                        "continuous production Tick samples and native dummy state corroborate the event");
                    Require(CurrentFlashVerification(owner)==round && row.Id==target!.Handle && plan!.Pawns[row.Id]==target.Bot.PlayerPawn.Raw &&
                        round.Trial.Baselines.Single().Pawn==target.Bot.PlayerPawn.Raw && FlashPlanCurrent(owner,plan,out _),
                        "observer controller, pawn, pose and owner remain the original snapshot");
                    Require(projectileIndex is { } id && grenadeEffects.TryGetValue(id,out var effect) && effect.Id==round.ThrowId &&
                        effect.Sequence==round.Sequence && effect.Revision==temporary.Revision,
                        "production effect ledger retains the same throw, sequence and revision");
                    if(fixedBurst is { } desired)
                    {
                        var observedEffect=grenadeEffects[projectileIndex!.Value];
                        Say(null,$"FLASHVERIFY BURST CHECK: requested={desired}; actual={observedEffect.Position}; error={Vector3.Distance(desired,observedEffect.Position):F6}.");
                        Require(Vector3.Distance(desired,observedEffect.Position)<.25f,"native detonation retains the fixed diagnostic centre");
                        var sight=FlashSearchSight(source,desired,plan!.Observers[0]);
                        Say(null,$"FLASHVERIFY OCCLUSION RESULT: case={fixture}; screening={sight.Clear}/{sight.Total}; usable={sight.Usable}; state={row.State}; eventDuration={row.EventDuration?.ToString("F6",System.Globalization.CultureInfo.InvariantCulture) ?? "none"}; nativeIncreased={row.NativeIncreased}; fixedPointOnly=true; protectionCandidateWithObservedBlind={sight.Usable && sight.Clear==0 && blind}.");
                    }
                    Require(!round.ExecutionMatched && temporary.LastVerified is null && temporary.Target is null && temporary.Arm is null,
                        "moved diagnostic grenade never becomes a verified route or a landing success");
                    Require(plan!.Trials?.Outcome(temporary.Selected) is null,
                        "unmatched diagnostic effect never enters matched candidate history");
                    string liveRoster=DummyRosterText(owner) ?? "";
                    var liveTarget=target!.Bot.PlayerPawn.Value!;
                    float liveBlind=Math.Clamp(Math.Max(target.BlindUntil,liveTarget.BlindUntilTime)-Server.CurrentTime,0,99.9f);
                    Require(liveRoster.Contains($"{liveTarget.Health}HP 甲{liveTarget.ArmorValue}") && (quiet || liveRoster.Contains("记录闪")) &&
                        (liveBlind>.05f ? liveRoster.Contains("致盲"+liveBlind.ToString("0.0",System.Globalization.CultureInfo.InvariantCulture)+"s") : liveRoster.Contains("未致盲")),
                        "flash search roster keeps live native HP/armor and blindness alongside the attributed event record");
                    Say(null,$"FLASHVERIFY ACTUAL: throwId={round.ThrowId}; owner={ownerHandle}; observer={row.Id}; pawn={plan!.Pawns[row.Id]}; duration={row.EventDuration:F3}; samples={row.SampleCount}; not full-white duration.");
                    var savedResult=temporary.Result;var savedPlan=temporary.FlashPlan;int savedRevision=temporary.Revision;
                    ResetDummy(target!);
                    Require(CurrentFlashVerification(owner) is null && temporary.FlashMode && temporary.Revision==savedRevision &&
                        ReferenceEquals(temporary.Result,savedResult) && ReferenceEquals(temporary.FlashPlan,savedPlan) && temporary.Start==start &&
                        target!.SearchRole==role,"ResetDummy clears actual verification while preserving objective, candidate and origin");
                    phase=8;
                }
                else if(phase==8 && target is {Ready:true,ResetRequested:false} && target.Bot.PlayerPawn.Value is {IsValid:true} resetPawn)
                {
                    Require(!target.HasBlindMeasurement && resetPawn.FlashDuration==0 && resetPawn.BlindUntilTime==0 && resetPawn.BlindStartTime==0 &&
                        Vector3.Distance(NativeWorld.FromNative(resetPawn.AbsOrigin!),targetFeet)<4,"dummy reset clears real blindness and preserves its recorded position");
                    string resetRoster=DummyRosterText(owner) ?? "";
                    Require(resetRoster.Contains("100HP") && resetRoster.Contains("未致盲") && !resetRoster.Contains("记录闪"),
                        "flash search roster immediately reflects reset health and recovery without retaining the old event result");
                    CleanupOwned();phase=9;
                }
                else if(phase==9 && !creatingDummy && !effectDummies.Values.Any(d=>d.Owner==ownerHandle))
                {
                    Require(Utilities.GetPlayers().All(p=>originalControllers.Contains(p.EntityHandle.Raw)),"owned dummy removed without leaving a roaming replacement");
                    Require(hadSession ? sessions.TryGetValue(owner.Slot,out var restored) && ReferenceEquals(restored,oldSession) : !sessions.ContainsKey(owner.Slot),
                        "temporary verification session removed and original session restored");
                    RestoreSeed();
                    Require(Vector3.Distance(NativeWorld.FromNative(source.AbsOrigin!),oldFeet)<.1f &&
                        MathF.Abs(source.EyeAngles.X-oldView.X)<.1f && MathF.Abs(Geometry.NormalizeYaw(source.EyeAngles.Y-oldView.Y))<.1f &&
                        source.MoveType==oldMove && source.ActualMoveType==oldActualMove && source.BotAllowActive==oldActive &&
                        (source.Bot?.AllowActive ?? false)==oldAi && botStop.StringValue==oldStop,
                        "seed bot position, view, movement modes, AI flags and bot_stop restored");
                    Finish();return;
                }
                AddTimer(.02f,Step);
            }
            catch(Exception ex){Finish(ex.GetType().Name+": "+ex.Message);}
        }
        flashVerificationCheckPending=true;stopFlashVerificationCheck=()=>Finish("diagnostic cancelled by lifecycle cleanup");
        try
        {
            source.BotAllowActive=false;if(source.Bot is { } ai)ai.AllowActive=false;
            SetMovement(source,MoveType_t.MOVETYPE_NONE,MoveType_t.MOVETYPE_NONE);source.Teleport(velocity:Vector3.Zero);
            botStop.StringValue="0";
            Say(null,$"FLASHVERIFY START: fixture={fixture}; isolated native weapon release and one owned dummy; no synthetic events, no route or manual-throw validation.");
            AddTimer(.02f,Step);
        }
        catch(Exception ex){Finish(ex.GetType().Name+": "+ex.Message);}
    });

    private void LogFlashOcclusionProbes(CCSPlayerPawn source,Vector3 center,FlashObserver observer)
    {
        var screening=FlashSearchSight(source,center,observer);
        Say(null,$"FLASHVERIFY OCCLUSION SCREEN: burst={center}; eye={observer.Eye}; view={observer.View.Pitch}/{observer.View.Yaw}; clear={screening.Clear}/{screening.Total}; usable={screening.Usable}; diagnosticOffsetsOnly=true.");
        void Probe(string name,Vector3 start,Vector3 end,TraceOptions options)
        {
            var ray=EffectRay(source,start,end,options);
            Say(null,$"FLASHVERIFY RAY: name={name}; start={start}; target={end}; fraction={ray.Fraction:F7}; allSolid={ray.IsAllSolid}; hit={ray.DidHit()}; contents={ray.Contents}; end={ray.EndPos}; normal={ray.Normal}.");
        }
        Probe("center",center,observer.Eye,DummyFlashSight);
        Probe("reverse",observer.Eye,center,DummyFlashSight);
        Probe("reverse-solid",observer.Eye,center,new TraceOptions {InteractsWith=Contents.Solid,InteractsExclude=DummyFlashSight.InteractsExclude});
        foreach(float radius in new[]{3f,16f})
            foreach(var axis in new[]{Vector3.UnitX,Vector3.UnitY,Vector3.UnitZ})
                foreach(float sign in new[]{-1f,1f})
                    Probe($"eye-{radius}-{axis}-{sign}",center,observer.Eye+axis*(radius*sign),DummyFlashSight);
        foreach(var axis in new[]{Vector3.UnitX,Vector3.UnitY,Vector3.UnitZ})
            foreach(float sign in new[]{-1f,1f})
                Probe($"burst-16-{axis}-{sign}",center+axis*(16*sign),observer.Eye,DummyFlashSight);
        foreach(var mask in new[]{Contents.Solid,Contents.Window,Contents.BlockLos,Contents.BlockLight,Contents.CsgoOpaque,Contents.PhysicsProp})
            Probe("mask-"+mask,center,observer.Eye,new TraceOptions {InteractsWith=mask,InteractsExclude=DummyFlashSight.InteractsExclude});
        float pitch=observer.View.Pitch*MathF.PI/180,yaw=observer.View.Yaw*MathF.PI/180;
        var rotatedOffset=62.5625f*new Vector3(MathF.Sin(pitch)*MathF.Cos(yaw),MathF.Sin(pitch)*MathF.Sin(yaw),MathF.Cos(pitch)-1);
        Probe("hypothetical-old-body-eye",center,observer.Eye+rotatedOffset,DummyFlashSight);
    }
}
