using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private void RegisterLifetimeDiagnostic()=>AddCommand("css_nl_lifetimecheck",
        "Empty-server preparation lifetime and armed-preview guards",LifetimeDiagnostic);

    private void LifetimeDiagnostic(CCSPlayerController? caller,CommandInfo command)
    {
        if(caller is not null || !Practice || Utilities.GetPlayers().Any(p=>!p.IsBot))
        {Say(caller,"LIFETIMECHECK refused: server console, Mirage practice and zero humans required.");return;}
        if(job is not null || flashJob is not null || flashSearchCheckPending || flashVerificationCheckPending || DummyLayoutLoadActive || placementCheck is not null || effectStressCheck is not null || flights.Count>0 || grenadeFlights.Count>0 || infernos.Count>0 ||
            effectPreviewScenes.Count>0 || previewScenes.Count>0 || targetRangePreviewUntil.Count>0 ||
            flashVerification.Values.Any(round=>!round.Trial.Complete) ||
            sessions.Values.Any(s=>s.Placement is not null || s.Arm is not null || s.Pending is not null || s.EffectEnabled))
        {Say(null,"LIFETIMECHECK refused: wait for active searches, diagnostics, throws and previews to finish.");return;}
        var owner=Utilities.GetPlayers().FirstOrDefault(p=>p.IsBot && p.IsValid &&
            p.PlayerPawn.Value is {IsValid:true,LifeState:0} && !ThrowInProgress(p));
        if(owner?.PlayerPawn.Value is not {IsValid:true,LifeState:0} pawn)
        {Say(null,"LIFETIMECHECK refused: one existing living idle bot is required.");return;}

        // Borrow identity only. No kill, respawn, teleport, inventory or pawn state writes.
        sessions.TryGetValue(owner.Slot,out var original);
        flashVerification.TryGetValue(owner.Slot,out var originalFlash);
        var session=new Session {SteamId=owner.SteamID,HudEnabled=false,GuideEnabled=false};
        var start=new ThrowStart(NativeWorld.FromNative(pawn.AbsOrigin!),pawn.ViewOffset.Z);
        var target=new Target(start.Feet+new Vector3(64,0,2),48);
        var aim=new Aim(0,0,ThrowMode.Left);
        var candidate=new Candidate(aim,new(target.Position,SimulationStatus.Rested,1,0,[]),0);
        var result=new SearchResult("lifetime-diagnostic",false,1,false,[candidate],[]);
        session.Start=start;session.RequestedStart=start;session.Target=target;session.Result=result;session.Selected=1;
        int passed=0,failed=0;
        void Check(bool ok,string message)
        {if(ok) passed++;else failed++;Say(null,$"LIFETIMECHECK {(ok ? "PASS" : "FAIL")}: {message}");}
        void Seed(float expires)
        {
            BindPreparation(owner,session);
            session.Result=result;session.Selected=1;
            session.Arm=new(candidate,start,session.Target,session.Revision,expires,EffectObjective:session.FlashMode);
            session.Pending=new(1,session.Revision,start.Feet,Server.CurrentTime);
            session.Snapshot=new(start.Feet,start.EyeHeight,aim,1,Vector3.Zero,true,Server.CurrentTime,true,true);
            session.CueStage=ReleaseStage.Window;session.CueUntil=Server.CurrentTime+1;
            session.PreparationIssue="diagnostic sentinel";
        }
        bool Cleared()=>session.Arm is null && session.Pending is null && session.Snapshot is null &&
            session.CueStage==ReleaseStage.None && session.CueUntil==0 && session.PreparationIssue is null;
        bool Preserved()=>session.Start==start && session.RequestedStart==start && session.Target==target && ReferenceEquals(session.Result,result);
        try
        {
            sessions[owner.Slot]=session;
            flashVerification.Remove(owner.Slot);
            Seed(Server.CurrentTime-.01f);int revision=session.PreparationRevision;
            SyncPreparationLifetime(owner,session);
            Check(Cleared() && session.PreparationRevision>revision,"expired Arm clears transient state and invalidates delayed preparation");
            Check(Preserved(),"expiry preserves target/start/result");

            Seed(Server.CurrentTime+90);revision=session.PreparationRevision;
            SyncPreparationLifetime(null,session);
            Check(Cleared() && session.PreparationRevision>revision,"unavailable player clears preparation (simulated death guard; no bot killed)");
            Check(Preserved(),"unavailable player preserves target/start/result");

            Seed(Server.CurrentTime+90);
            session.PreparationLifetime.Observe(grenadeEpoch,pawn.EntityHandle.Raw^1u,true);
            SyncPreparationLifetime(owner,session);
            Check(Cleared() && session.Result is null && session.Selected==0 && session.Target==target && session.Start==start,
                "old pawn binding clears stale results and preparation while preserving the request");

            Seed(Server.CurrentTime+90);
            session.PreparationLifetime.Observe(grenadeEpoch-1,pawn.EntityHandle.Raw,true);
            SyncPreparationLifetime(owner,session);
            Check(Cleared() && session.Result is null && session.Selected==0 && session.Target==target && session.Start==start,
                "old map binding clears stale results without changing the native world");

            foreach(bool flash in new[]{false,true})
            {
                session.FlashMode=flash;session.Kind=flash ? GrenadeKind.Flash : GrenadeKind.Smoke;session.Target=flash ? null : target;
                Seed(Server.CurrentTime+90);
                SelectCandidate(owner,1);
                Check(session.Arm is not null && ReferenceEquals(session.Arm.Candidate,candidate),
                    $"{(flash ? "flash" : "smoke")}: same-candidate retry reproduces the existing armed state before alignment");
                session.Pending=new(1,session.Revision,start.Feet,Server.CurrentTime);
                session.TimingNotice="old timing";session.TimingNoticeUntil=Server.CurrentTime+5;
                session.ReleaseDelay=.125f;session.LastThrowTime=1;
                var previousTarget=session.Target;revision=session.PreparationRevision;int sequence=session.ThrowSequence;
                FlashVerificationTrial? trial=null;
                if(flash)
                {
                    var observer=new FlashObserver(owner.EntityHandle.Raw,"lifetime guard fixture",Vector3.Zero,aim,FlashRole.MustFlash);
                    trial=new([observer],[new(observer.Id,pawn.EntityHandle.Raw,observer.Eye,observer.View,true,0,0,0)],Server.CurrentTime);
                    var plan=new FlashPlanState {Observers=[observer],Pawns=new() {[observer.Id]=pawn.EntityHandle.Raw},
                        Epoch=grenadeEpoch,Revision=session.Revision,Owner=owner.EntityHandle.Raw,OwnerPawn=pawn.EntityHandle.Raw};
                    session.FlashPlan=plan;
                    // Managed guard fixture only: never serialize a diagnostic
                    // trial into real throw/effect evidence.
                    flashVerification[owner.Slot]=new() {Slot=owner.Slot,Epoch=grenadeEpoch,Revision=session.Revision,Selected=1,
                        Owner=owner.EntityHandle.Raw,Pawn=pawn.EntityHandle.Raw,Steam=owner.SteamID,Map=Server.MapName,Session=session,
                        Plan=plan,Observers=[observer],Pawns=plan.Pawns,Trial=trial,Published=true};
                }
                bool aligned=TryAlignPreparation(owner,pawn,session,aim,(_,_)=>throw new InvalidOperationException("diagnostic owned-correction failure"));
                Check(!aligned && session.Arm is null && session.Pending is null && session.Snapshot is null &&
                    session.CueStage==ReleaseStage.None && session.CueUntil==0 && session.TimingNotice is null && session.TimingNoticeUntil==0 &&
                    session.ReleaseDelay is null && session.LastThrowTime==0,"alignment failure cancels arm, observation, pending and timing state");
                Check(session.PreparationRevision>revision && session.ThrowSequence>sequence,"alignment failure invalidates delayed preparation and throw UI callbacks");
                Check(session.Target==previousTarget && session.Start==start && session.RequestedStart==start &&
                    ReferenceEquals(session.Result,result) && ReferenceEquals(session.Result.Matches[0],candidate) && session.Selected==1,
                    "alignment failure preserves target, origin and exact candidate selection");
                Check(session.PreparationIssue is not null && WorkflowFeedback(owner,session,Server.CurrentTime)==session.PreparationIssue,
                    "failed re-preparation displays its blocker instead of old readiness");
                if(flash) Check(!flashVerification.ContainsKey(owner.Slot) && trial is {Complete:true,InvalidReason:not null},
                    "failed same-candidate flash preparation removes and invalidates the old observation without publishing diagnostic evidence");
            }
            session.FlashMode=false;session.Kind=GrenadeKind.Smoke;session.FlashPlan=null;session.Target=target;

            Seed(Server.CurrentTime+90);session.Pending=null;session.CueStage=ReleaseStage.None;session.CueUntil=0;
            session.EffectEnabled=true;session.NextEffectAt=0;
            TickPlacementAndEffects();
            Check(session.Arm is not null && effectPreviewScenes.ContainsKey(owner.Slot),
                "armed, non-charging F4 request reaches preview scene creation (not client visibility proof)");
        }
        catch(Exception ex) {failed++;Say(null,"LIFETIMECHECK FAIL: "+ex.Message);}
        finally
        {
            ClearEffectPreview(owner.Slot);
            if(originalFlash is null) flashVerification.Remove(owner.Slot);else flashVerification[owner.Slot]=originalFlash;
            if(original is null) sessions.Remove(owner.Slot);else sessions[owner.Slot]=original;
        }
        Say(null,$"LIFETIMECHECK DONE: {passed} passed, {failed} failed; native clock/pawn read only; death, pawn/map replacement are controlled identity inputs, not engine-event tests.");
    }
}
