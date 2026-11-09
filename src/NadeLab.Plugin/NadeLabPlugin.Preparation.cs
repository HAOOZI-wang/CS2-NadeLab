using System.Globalization;
using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using Microsoft.Extensions.Logging;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private void TickPreparationAndCues()
    {
        float now=Server.CurrentTime;
        foreach(var (slot,s) in sessions)
        {
            var p=Utilities.GetPlayerFromSlot(slot);
            SyncPreparationLifetime(p,s);
            if(s.Pending is null && s.Arm is null) continue;
            if(p?.IsValid!=true || p.SteamID!=s.SteamId || p.PlayerPawn.Value is not {IsValid:true,LifeState:0} pawn)
            {s.Pending=null; continue;}
            if(pawn.MoveType==MoveType_t.MOVETYPE_NOCLIP)
            {s.Pending=null;s.Arm=null;s.Snapshot=null;s.CueStage=ReleaseStage.None;s.CueUntil=0;s.PreparationIssue=null;continue;}
            var feet=NativeWorld.FromNative(pawn.AbsOrigin!);
            var velocity=NativeWorld.FromNative(pawn.AbsVelocity);
            if(s.Pending is { } pending)
            {
                if(pending.Revision!=s.Revision || pending.Selected!=s.Selected || s.Result?.Matches.ElementAtOrDefault(s.Selected-1) is not { } candidate || s.Start is not { } start)
                {s.Pending=null; continue;}
                if(!ReferenceEquals(s,preparationContinuationSession))
                {
                    if(s.FlightCheck is not { } checking || !CurrentFlightCheck(p,s,checking))
                    {BlockPreparation(p,s,"准备已中断，请重新就位。");continue;}
                    if(!checking.Check.Accepted) continue;
                }
                var aim=candidate.Aim;
                bool eye=aim.NeedsRunUp || aim.Airborne || MathF.Abs(pawn.ViewOffset.Z-aim.EyeHeight(start))<=ExecutionEnvelope.EyeHeight;
                bool pose=aim.EffectiveAction==ThrowAction.Crouch ? (pawn.Flags&1)!=0 && pawn.ViewOffset.Z<50 : GroundedUpright(pawn);
                bool stationary=velocity.Length()<=1;
                float positionError=Vector3.Distance(feet,pending.Destination);
                bool position=positionError<=2;
                bool angle=MathF.Abs(pawn.EyeAngles.X-aim.Pitch)<=ExecutionEnvelope.Angle && MathF.Abs(Geometry.NormalizeYaw(pawn.EyeAngles.Y-aim.Yaw))<=ExecutionEnvelope.Angle;
                string? activeWeapon=pawn.WeaponServices?.ActiveWeapon.Value is {IsValid:true} active ? active.DesignerName : null;
                bool equipped=activeWeapon==s.Kind.Weapon();
                bool ready=eye && pose && stationary && position && angle && equipped;
                // One record per completed attempt, never per tick. Alignment
                // success alone cannot explain a later preparation timeout.
                void LogOutcome(string outcome)
                {
                    if(ReferenceEquals(s,preparationContinuationSession)) preparationContinuationOutcome=outcome;
                    Logger.LogInformation(
                    "Preparation {Outcome} slot={Slot} selected={Selected}/{Count} revision={Revision} since={Since} elapsed={Elapsed:F3}; aim={Aim}; start={Start}; destination={Destination}; feet={Feet}; velocity={Velocity}; eye={Eye:F3}/{ExpectedEye:F3}; view={Pitch:F3}/{Yaw:F3}; weapon={Weapon}/{ExpectedWeapon}; move={Move}/{ActualMove}; flags=0x{Flags:X}; duck={Duck}; botActive={BotActive}/{AiActive}; checks eye={EyeReady}, pose={PoseReady}, stationary={Stationary}, position={PositionReady}, angle={AngleReady}, equipped={Equipped}",
                    outcome,slot,s.Selected,s.Result!.Matches.Count,s.Revision,pending.Since,now-pending.Since,aim,start.Feet,pending.Destination,feet,velocity,
                    pawn.ViewOffset.Z,aim.EyeHeight(start),pawn.EyeAngles.X,pawn.EyeAngles.Y,activeWeapon,s.Kind.Weapon(),
                    pawn.MoveType,pawn.ActualMoveType,pawn.Flags,pawn.MovementServices?.As<CCSPlayer_MovementServices>().DuckAmount,pawn.BotAllowActive,pawn.Bot?.AllowActive,
                    eye,pose,stationary,position,angle,equipped);
                }
                if(pending.Stability.Observe(now,feet,pawn.ViewOffset.Z,ready))
                {
                    s.Pending=null;
                    if(ArmVerification(p,s.Selected.ToString(CultureInfo.InvariantCulture)))
                    {
                        LogOutcome("ready");
                        HudNotice(s,aim.NeedsRunUp ? "助跑起点已就绪。蓄力后按投法节奏移动；出手眼高仍待实投校准。" : "站位、视角和所选道具已就绪，可以蓄力手投。",3);
                    }
                    else
                    {
                        LogOutcome("recording-refused");
                        s.PreparationIssue="未能开启手投记录；完成当前投掷后，重新准备。";
                        HudNotice(s,s.PreparationIssue,8);
                    }
                }
                else if(now-pending.Since>5)
                {
                    LogOutcome("timeout");
                    s.Pending=null;
                    s.PreparationIssue=!eye ? "眼高与求解记录不同；站稳后使用“换站位重搜”，目标保留。" :
                        !pose ? aim.EffectiveAction==ThrowAction.Crouch ? "准备未完成：请完全蹲稳，再重新准备。" : "准备未完成：请落地站直，再重新准备。" :
                        !stationary ? "准备未完成：请松开移动键并停稳，再重新准备。" :
                        !position ? $"准备未完成：落地脚位偏离准备位置 {positionError:F1}u；请重新就位，反复失败可换站位重搜。" :
                        !angle ? "准备未完成：视角已偏离投法；请停止移动视角，再重新准备。" :
                        !equipped ? $"准备未完成：尚未切出{s.Kind.Label()}；请切出所选道具，再重新准备。" :
                        "准备未完成：站位或眼高仍在变化；请站稳或蹲稳，再重新准备。";
                    HudNotice(s,s.PreparationIssue,8);
                    Say(p,s.HudNotice!);
                }
                continue;
            }
            if(s.Arm is not {ObservationOnly:false} arm || arm.Expires<now || !arm.Candidate.Aim.NeedsRunUp || !s.HudEnabled) continue;
            var a=arm.Candidate.Aim;
            if(new Vector2(velocity.X,velocity.Y).Length()<20) continue;
            if(pawn.WeaponServices?.ActiveWeapon.Value is not {IsValid:true} weapon || weapon.DesignerName!=s.Kind.Weapon() ||
                weapon.As<CBaseCSGrenade>().ThrowTime>0) continue;
            var profile=arm.Physics ?? physics;
            var line=ThrowExecution.Resolve(arm.Start,a,profile).ButtonFeet;
            bool aligned=GroundedUpright(pawn) && MathF.Abs(pawn.EyeAngles.X-a.Pitch)<=ExecutionEnvelope.Angle &&
                MathF.Abs(Geometry.NormalizeYaw(pawn.EyeAngles.Y-a.Yaw))<=ExecutionEnvelope.Angle;
            var stage=ReleaseGuidance.Stage(feet,line,velocity,a,aligned,arm.Candidate.ReleaseWindow,
                arm.Start.Origin==ThrowOrigin.Preparation ? 0 : profile.ReleaseDelaySeconds);
            if(stage==s.CueStage || s.CueStage==ReleaseStage.Passed || now<s.CueUntil-.55f) continue;
            s.CueStage=stage;
            if(stage==ReleaseStage.Passed) ClearPreview(slot);
            string? cue=stage switch
            {
                ReleaseStage.Steady=>"速度已稳定，保持方向；即将进入释放区时会提前提示",
                ReleaseStage.Window=>a.Airborne ? "位于采样释放区：按原节奏起跳并松鼠标" : "位于采样释放区：按原节奏松鼠标，继续前进",
                ReleaseStage.Approach=>"接近松键位置：保持速度，准备松鼠标",
                ReleaseStage.Soon=>a.Airborne ? "即将到松键位置：到位起跳并松鼠标" : "即将到松键位置：到位松鼠标，继续W",
                ReleaseStage.Passed=>"已过松键线；若还没松键，请重新准备",
                ReleaseStage.Correct=>"先稳定速度和方向；必要时重新准备",
                _=>null
            };
            if(cue is null) continue;
            if(!HasPanel(p))
            {
                s.TimingNotice=cue;s.TimingNoticeUntil=now+.85f;
                s.FeedbackUrgent=true;RenderNotice(p,s,now);s.CueUntil=now+.65f;
            }
        }
    }

    // Capture input focus before deferring native work. A completed search can
    // publish a replacement panel without changing the session revision.
    private void QueuePanelOrWorkflowAction(CCSPlayerController? player,Action<CCSPlayerController?> panelAction,Action<CCSPlayerController?> workflowAction)
    {
        var expected=CurrentPanel(player,out var panel) ? panel : null;
        int selected=expected?.Selected ?? -1;
        QueueAction(player,current=>
        {
            if(!PanelInputMatches(current,expected,selected)) return;
            if(expected is null) workflowAction(current);else panelAction(current);
        });
    }

    private bool PanelInputMatches(CCSPlayerController? player,PanelState? expected,int selected)
    {
        var active=CurrentPanel(player,out var now) ? now : null;
        return ReferenceEquals(active,expected) && (active is null || active.Selected==selected);
    }

    private void PracticeButtons(CCSPlayerController player,PlayerButtons pressed,PlayerButtons released)
    {
        if(!Practice || player.IsBot || player.PlayerPawn.Value is not {IsValid:true,LifeState:0}) return;
        if(HasCompanion(player)) return; // Physical input is dispatched once by the launcher companion.
        var observed=Get(player);
        var buttons=(player.Buttons|pressed)&~released;
        if(!observed.LegacyControls && !HasPanel(player) && (buttons&(PlayerButtons.Speed|PlayerButtons.Walk))!=0 && (buttons&PlayerButtons.Reload)!=0) return;
        if(HandlePanelButtons(player,pressed,released)) return;
        if(HasDummyBatch(player))
        {
            if((pressed&PlayerButtons.Use)!=0) QueueAction(player,p=>{if(p is not null) DummyBatchPrimary(p);});
            else if((pressed&PlayerButtons.Reload)!=0) QueueAction(player,p=>{if(p is not null) DummyBatchBack(p);});
            return;
        }
        bool next=(pressed&PlayerButtons.Reload)!=0, prepare=(pressed&PlayerButtons.Use)!=0;
        if(!next && !prepare) return;
        var s=Get(player);
        if(Server.CurrentTime<s.NextPracticeButtonAt) return;
        if(ThrowInProgress(player)) {ClosePanel(player);return;}
        if(s.Placement is not null)
        {
            s.NextPracticeButtonAt=Server.CurrentTime+.5f;
            QueueAction(player,current=>{if(current is not null) FinishPlacement(current,prepare);});
            return;
        }
        if(s.RegionDraft is not null)
        {
            if((pressed&(PlayerButtons.Use|PlayerButtons.Reload))!=0)
            {s.NextPracticeButtonAt=Server.CurrentTime+.75f;QueueAction(player,OpenMenu);}
            return;
        }
        // CS2 +sprint (the default Shift slow-walk binding) uses Speed.
        // Retain Walk compatibility, and include this callback's press/release
        // edges so pressing both keys in the same update also works.
        var held=(player.Buttons|pressed)&~released;
        if(next && (held&(PlayerButtons.Speed|PlayerButtons.Walk))!=0)
        {
            s.NextPracticeButtonAt=Server.CurrentTime+.75f;
            QueuePanelOrWorkflowAction(player,_=>{},Reposition);
            return;
        }
        if((pressed&PlayerButtons.Use)!=0 && (held&(PlayerButtons.Speed|PlayerButtons.Walk))!=0 && (s.Target is not null || s.FlashMode))
        {
            s.NextPracticeButtonAt=Server.CurrentTime+.75f;
            QueuePanelOrWorkflowAction(player,_=>{},Retarget);
            return;
        }
        if(prepare)
        {
            s.NextPracticeButtonAt=Server.CurrentTime+.75f;
            QueuePanelOrWorkflowAction(player,_=>{},PrimaryAction);
            return;
        }
        if(s.Result?.Matches.Count is not >0) return;
        s.NextPracticeButtonAt=Server.CurrentTime+.35f;
        QueuePanelOrWorkflowAction(player,_=>{},current=>
        {
            if(current is null || !Check(current,out _)) return;
            if(next) NextCandidate(current,null!);
            else PrepareThrow(current,Math.Max(1,Get(current).Selected).ToString(CultureInfo.InvariantCulture));
        });
    }

    private static bool ThrowInProgress(CCSPlayerController player)
    {
        if(player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value is not {IsValid:true} weapon ||
            weapon.DesignerName is not ("weapon_smokegrenade" or "weapon_hegrenade" or "weapon_flashbang" or "weapon_molotov" or "weapon_incgrenade" or "weapon_decoy")) return false;
        var grenade=weapon.As<CBaseCSGrenade>();
        return grenade.PinPulled || grenade.ThrowTime>0 || (player.Buttons&(PlayerButtons.Attack|PlayerButtons.Attack2))!=0;
    }

    private static ViewAngleServerChange_t ViewCorrection(NetworkedVector<ViewAngleServerChange_t> queue,int index)
    {
        const string type="ViewAngleServerChange_t";
        int size=Schema.GetClassSize(type);
        // CSS 1.0.378's managed indexer accepts CHandle<T> only. Its native
        // element accessor uses a CEntityHandle stride, so ONLY index zero is
        // valid for obtaining this struct vector's storage. Use live schema
        // size for subsequent elements; never assume a vector header offset.
        foreach(var (field,width) in new[]{("nType",1),("qAngle",12),("nIndex",4)})
        {
            int offset=Schema.GetSchemaOffset(type,field);
            if(offset<0 || offset>size-width)
                throw new InvalidOperationException("Unsupported view-correction schema layout.");
        }
        return new(NativeStructVector.Element(queue.Count,index,size,
            ()=>NativeAPI.GetNetworkVectorElementAt(queue.Handle,0)));
    }

    private static ViewCorrectionSnapshot[] ViewCorrectionSnapshotOf(NetworkedVector<ViewAngleServerChange_t> queue)
        =>Enumerable.Range(0,queue.Count).Select(i=>
        {
            var change=ViewCorrection(queue,i);var angle=change.QAngle;
            return new ViewCorrectionSnapshot(change.NIndex,(int)change.NType,angle.X,angle.Y,angle.Z);
        }).ToArray();

    private static void AlignView(CCSPlayerPawn pawn,Aim aim,Action<string>? diagnostic=null)
    {
        float yaw=Geometry.NormalizeYaw(aim.Yaw);
        var owner=pawn.Controller.Value is {IsValid:true} controller ? controller.As<CCSPlayerController>() : null;
        bool confirmedBot=owner is {IsValid:true,IsBot:true} && owner.PlayerPawn.Raw==pawn.EntityHandle.Raw;
        var queue=pawn.ServerViewAngleChanges;
        var before=ViewCorrectionSnapshotOf(queue);
        diagnostic?.Invoke($"queueBefore={before.Length}; indices=[{string.Join(',',before.Select(c=>c.Index))}]; confirmedBot={confirmedBot}; requested=({aim.Pitch:F5},{yaw:F5},0)");
        // Teleport must update the engine's body transform while upright.
        // Clearing scene rotation fields after a pitched teleport leaves the
        // cached transform pitched and rotates the eye offset on real throws.
        pawn.Teleport(angles:new Vector3(0,yaw,0));
        queue=pawn.ServerViewAngleChanges;
        var after=ViewCorrectionSnapshotOf(queue);
        diagnostic?.Invoke($"queueAfterTeleport={after.Length}; indices=[{string.Join(',',after.Select(c=>c.Index))}]; body={pawn.AbsRotation}");
        // Bots have no client to consume an angle correction and the engine
        // can leave their queue unchanged. This exception requires the current
        // typed controller and its complete pawn handle to confirm bot identity.
        bool stillConfirmedBot=confirmedBot && owner is {IsValid:true,IsBot:true} &&
            pawn.Controller.Raw==owner.EntityHandle.Raw && owner.PlayerPawn.Raw==pawn.EntityHandle.Raw;
        int ownedSlot=ViewCorrectionOwnership.Select(before,after,yaw,stillConfirmedBot);
        bool botWithoutCorrection=ownedSlot<0;
        uint? ownedIndex=null;
        string ownedState="ownedCorrection=none(bot-only)";
        if(!botWithoutCorrection)
        {
            var owned=ViewCorrection(queue,ownedSlot);
            ownedIndex=owned.NIndex;
            var corrected=owned.QAngle;
            corrected.X=aim.Pitch;corrected.Y=yaw;corrected.Z=0;
            after[ownedSlot]=after[ownedSlot] with {Pitch=aim.Pitch,Yaw=yaw,Roll=0};
            ownedState=$"ownedQueueSlot={ownedSlot}; ownedIndex={ownedIndex}; ownedType={owned.NType}; ownedAngle={corrected}";
        }
        var nativeView=pawn.V_angle;
        nativeView.X=aim.Pitch;nativeView.Y=yaw;nativeView.Z=0;
        var eyeView=pawn.EyeAngles;
        eyeView.X=aim.Pitch;eyeView.Y=yaw;eyeView.Z=0;
        if(Schema.IsSchemaFieldNetworked("CCSPlayerPawn","m_angEyeAngles"))
            Utilities.SetStateChanged(pawn,"CCSPlayerPawn","m_angEyeAngles");
        var body=pawn.AbsRotation;
        bool upright=body is not null && MathF.Abs(body.X)<=.01f && MathF.Abs(body.Z)<=.01f && MathF.Abs(Geometry.NormalizeYaw(body.Y-yaw))<=.01f;
        bool view=MathF.Abs(eyeView.X-aim.Pitch)<=.01f && MathF.Abs(Geometry.NormalizeYaw(eyeView.Y-yaw))<=.01f && MathF.Abs(eyeView.Z)<=.01f &&
            MathF.Abs(nativeView.X-aim.Pitch)<=.01f && MathF.Abs(Geometry.NormalizeYaw(nativeView.Y-yaw))<=.01f && MathF.Abs(nativeView.Z)<=.01f;
        diagnostic?.Invoke($"{ownedState}; botWithoutCorrection={botWithoutCorrection}; queueAfterPatch={queue.Count}; body={body}; upright={upright}; view={eyeView}; v_angle={nativeView}; viewMatches={view}; engineTransformNeedsRealThrow=true");
        bool queueStable=ViewCorrectionSnapshotOf(queue).SequenceEqual(after);
        if(!queueStable || !upright || !view)
            throw new InvalidOperationException("View alignment did not retain an upright body and the requested view; preparation stopped.");
    }

    private void ClearPreparationAttempt(CCSPlayerController player,Session session)
    {
        session.Arm=null;session.Pending=null;session.FlightCheck=null;session.Snapshot=null;session.PreparationIssue=null;
        session.CueStage=ReleaseStage.None;session.CueUntil=0;session.TimingNotice=null;session.TimingNoticeUntil=0;
        session.ReleaseDelay=null;session.LastThrowTime=0;
        // Equip timers use PreparationRevision; released-throw UI callbacks use
        // ThrowSequence. A failed retry must invalidate both older lifetimes.
        session.PreparationRevision++;session.ThrowSequence++;
        session.HudNotice=null;session.NextHudAt=0;session.LastHudText=null;session.FeedbackUrgent=true;
        ClearFlashVerification(player.Slot,"重新准备或准备失败，前次观测已终止");
        ClearPreview(player.Slot);
    }

    private bool BlockPreparation(CCSPlayerController player,Session session,string issue)
    {
        ClearPreparationAttempt(player,session);
        session.PreparationIssue=issue;
        HudNotice(session,issue,8);Say(player,issue);
        return false;
    }

    // The injectable operation is a narrow native boundary for the existing
    // lifetime diagnostic; production always calls the same AlignView method.
    private bool TryAlignPreparation(CCSPlayerController player,CCSPlayerPawn pawn,Session session,Aim aim,
        Action<CCSPlayerPawn,Aim>? align=null)
    {
        try
        {
            if(align is null) AlignView(pawn,aim,state=>Logger.LogInformation("Preparation alignment slot={Slot}: {State}",player.Slot,state));else align(pawn,aim);
            return true;
        }
        catch(Exception ex) when(ex is InvalidOperationException or NotSupportedException or ArgumentException or OverflowException)
        {
            Logger.LogWarning(ex,"Preparation alignment refused for player slot {Slot}",player.Slot);
            return BlockPreparation(player,session,"视角对准未完成；旧准备和观测已清除，请重新就位。");
        }
    }

    private void EquipSmoke(CCSPlayerController player)
    {
        AllowMenuEquipmentChange(player,Get(player).Kind);
        var s=Get(player); int preparation=++s.PreparationRevision;
        int slot=player.Slot;var identity=ActionIdentity(player);
        // Slot8 is the client's dedicated smoke slot, including inventory already
        // containing other grenades. Do not edit persistent client bindings.
        player.ExecuteClientCommand(s.Kind.Slot());
        AddTimer(.6f,()=>
        {
            if(!loaded) return;
            var current=Utilities.GetPlayerFromSlot(slot);
            if(current?.IsValid!=true || !identity.Matches(ActionIdentity(current)) ||
                !sessions.TryGetValue(slot,out var active) || active.PreparationRevision!=preparation) return;
            if(current.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value is not {IsValid:true} equipped || equipped.DesignerName!=active.Kind.Weapon())
                HudNotice(active,"尚未切出所选道具，请手动切出或重新就位。",4);
        });
    }

    private void PreparationDiagnostic(CCSPlayerController? caller,CommandInfo command)
    {
        if(command.ArgCount>1 && command.ArgByIndex(1)=="continuation")
        {PreparationContinuationDiagnostic(caller,command);return;}
        if(caller is not null || !Practice) return;
        if(!BenchmarkIdle || Utilities.GetPlayers().Any(player=>!player.IsBot))
        {Say(null,"PREPARECHECK refused: empty idle practice server required.");return;}
        foreach(var player in Utilities.GetPlayers().Where(p=>p.IsBot).Take(1))
        {
            if(player.PlayerPawn.Value is not {IsValid:true,LifeState:0} pawn) continue;
            var feet=NativeWorld.FromNative(pawn.AbsOrigin!);
            if(command.ArgCount==4 && float.TryParse(command.ArgByIndex(1),NumberStyles.Float,CultureInfo.InvariantCulture,out float x) &&
                float.TryParse(command.ArgByIndex(2),NumberStyles.Float,CultureInfo.InvariantCulture,out float y) &&
                float.TryParse(command.ArgByIndex(3),NumberStyles.Float,CultureInfo.InvariantCulture,out float z)) feet=new(x,y,z);
            bool placed=ReturnToStart(player,pawn,feet);
            if(placed)
            {
                try {AlignView(pawn,new(-63,179,ThrowMode.Left),state=>Say(null,"PREPARECHECK alignment: "+state));}
                catch(Exception ex) {Say(null,"PREPARECHECK alignment failed: "+ex.Message);continue;}
                if(pawn.WeaponServices?.MyWeapons.Any(h=>h.Value is {IsValid:true,DesignerName:"weapon_smokegrenade"})!=true) player.GiveNamedItem("weapon_smokegrenade");
                EquipSmoke(player);
                Draw(player,[NativeWorld.FromNative(pawn.AbsOrigin!)+new Vector3(0,0,16),NativeWorld.FromNative(pawn.AbsOrigin!)+new Vector3(0,0,96)],1);
            }
            Say(null,$"PREPARECHECK: placed={placed}; feet={pawn.AbsOrigin}; body={pawn.AbsRotation}; view={pawn.EyeAngles}; v_angle={pawn.V_angle}; changes={pawn.ServerViewAngleChanges.Count}.");
            if(!placed && pawn.Collision is { } collision)
            {
                var options=new CounterStrikeSharp.API.Modules.Utils.TraceOptions {InteractsAs=CounterStrikeSharp.API.Modules.Utils.Contents.Player,InteractsWith=CounterStrikeSharp.API.Modules.Utils.Masks.PlayerSolid|CounterStrikeSharp.API.Modules.Utils.Contents.PhysicsProp};
                foreach(float rise in new[]{.5f,1f,3f,6f,12f,18f})
                {
                    var pos=feet+new Vector3(0,0,rise);
                    var occupancy=CounterStrikeSharp.API.Modules.Utils.Trace.TraceHullShape(NativeWorld.ToNative(pos),NativeWorld.ToNative(pos),collision.Mins,collision.Maxs,pawn,options);
                    var floor=CounterStrikeSharp.API.Modules.Utils.Trace.TraceHullShape(NativeWorld.ToNative(pos),NativeWorld.ToNative(pos-new Vector3(0,0,20)),collision.Mins,collision.Maxs,pawn,options);
                    Say(null,$"PREPARECHECK support +{rise}u: occupied={occupancy.IsAllSolid}/{occupancy.Fraction:F3}; floor={floor.DidHit()}/{floor.IsAllSolid} z={floor.EndPos.Z:F3} nz={floor.Normal.Z:F2}; hull={collision.Mins}..{collision.Maxs}");
                }
            }
        }
    }
}
