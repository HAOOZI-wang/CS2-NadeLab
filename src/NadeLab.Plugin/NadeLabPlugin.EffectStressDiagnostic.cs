using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private sealed class EffectStressOwner
    {
        public required int Slot,Revision;
        public required uint Controller,Pawn;
        public required ulong Steam;
        public required GrenadeKind Kind;
        public required Vector3 Base;
        public Session? Session;
        public EffectPreviewScene? Scene;
        public Vector3 Center;
    }
    private sealed class EffectStressRun
    {
        public required int Epoch;
        public required EffectStressOwner[] Owners;
        public int Round,Passed,Failed,MarkersAheadOfRange;
        public bool Cleared;
        public int RetiringEntities;
        public float CleanupDeadline,RoundStarted;
    }
    private EffectStressRun? effectStressCheck;

    private void RegisterEffectStressDiagnostic()
    {
        AddCommand("css_nl_effectstress","Empty-server four-owner moving effect diagnostic",EffectStressDiagnostic);
        RegisterListener<Listeners.OnMapEnd>(()=>StopEffectStressDiagnostic());
        RegisterListener<Listeners.OnClientDisconnect>(slot=>
        {if(effectStressCheck is { } run && run.Owners.Any(o=>o.Slot==slot)) FinishEffectStress(run,"test bot disconnected");});
    }

    // Call before Reset/Unload clears effect scenes. Never writes a pawn or session.
    private void StopEffectStressDiagnostic()
    {if(effectStressCheck is { } run) FinishEffectStress(run,"plugin reset/map ended");}

    private static bool EffectStressIdle(Session? session)=>session is null ||
        (!session.EffectEnabled && !session.PreviewEnabled && session.Placement is null && session.Arm is null &&
         session.Pending is null && session.Continuation is null);

    private void EffectStressDiagnostic(CCSPlayerController? caller,CommandInfo command)
    {
        if(caller is not null || !loaded || !Practice || Utilities.GetPlayers().Any(p=>!p.IsBot))
        {Say(caller,"EFFECTSTRESS refused: server console, practice and zero humans required.");return;}
        if(effectStressCheck is not null || placementCheck is not null || job is not null || flashJob is not null || flashSearchCheckPending || flashVerificationCheckPending || DummyLayoutLoadActive ||
            flights.Count>0 || grenadeFlights.Count>0 || infernos.Count>0 || previewScenes.Count>0 || retiredBeams.Count>0 ||
            effectPreviewScenes.Count>0 || retiredEffectBeams.Count>0 || effectBeamCounts.Values.Any(n=>n>0) ||
            sessions.Values.Any(s=>!EffectStressIdle(s)))
        {Say(null,"EFFECTSTRESS refused: active diagnostic/search/throw/preview/session operation.");return;}
        try
        {
            var owners=new List<EffectStressOwner>();
            foreach(var owner in Utilities.GetPlayers().Where(p=>p.IsValid && p.IsBot).OrderBy(p=>p.Slot))
            {
                if(owner.PlayerPawn.Value is not {IsValid:true,LifeState:0} pawn || ThrowInProgress(owner) ||
                    NativeWorld.FromNative(pawn.AbsVelocity).Length()>1) continue;
                sessions.TryGetValue(owner.Slot,out var session);
                if(!EffectStressIdle(session) || (session is not null && session.SteamId!=owner.SteamID)) continue;
                if(!FindEffectStressCenter(pawn,out var center)) continue;
                owners.Add(new() {Slot=owner.Slot,Controller=owner.EntityHandle.Raw,Pawn=pawn.EntityHandle.Raw,Steam=owner.SteamID,
                    Session=session,Revision=session?.Revision ?? 0,Base=center,
                    Kind=owners.Count%2==0 ? GrenadeKind.Flash : GrenadeKind.HighExplosive});
                if(owners.Count==4) break;
            }
            if(owners.Count<4)
            {Say(null,$"EFFECTSTRESS refused: need four existing living idle bots with clear nearby air (found {owners.Count}); no bots or pawn state are changed.");return;}
            var run=new EffectStressRun {Epoch=grenadeEpoch,Owners=owners.ToArray()};effectStressCheck=run;
            Say(null,$"EFFECTSTRESS START: slots={string.Join(',',run.Owners.Select(o=>o.Slot))}; six moves every .35s; native state only, not client pixels/CheckTransmit proof.");
            EffectStressStep(run,0);
        }
        catch(Exception ex)
        {
            if(effectStressCheck is { } run) FinishEffectStress(run,"start: "+ex.Message);
            else Say(null,"EFFECTSTRESS FAIL: start: "+ex.Message);
        }
    }

    private static Vector3 EffectStressOffset(int round)=>new(MathF.Cos(round*MathF.Tau/6)*8,MathF.Sin(round*MathF.Tau/6)*8,0);

    private static bool FindEffectStressCenter(CCSPlayerPawn pawn,out Vector3 center)
    {
        var world=new NativeWorld(pawn);
        var eye=NativeWorld.FromNative(pawn.AbsOrigin!)+Vector3.UnitZ*pawn.ViewOffset.Z;
        foreach(float height in new[]{24f,64f,112f})
            foreach(var offset in new[]{Vector3.Zero,Vector3.UnitX*64,-Vector3.UnitX*64,Vector3.UnitY*64,-Vector3.UnitY*64})
            {
                var candidate=eye+Vector3.UnitZ*height+offset;
                if(world.Sweep(eye,candidate,2).Hit) continue;
                if(Enumerable.Range(0,6).All(i=>EffectPlacement.Resolve(world,GrenadeKind.Flash,candidate+EffectStressOffset(i)).Valid))
                {center=candidate;return true;}
            }
        center=default;return false;
    }

    private void ScheduleEffectStress(EffectStressRun run,int phase,float seconds)=>
        AddTimer(seconds,()=>EffectStressStep(run,phase),TimerFlags.STOP_ON_MAPCHANGE);

    private void GuardEffectStress(EffectStressRun run)
    {
        if(!loaded || !Practice || grenadeEpoch!=run.Epoch || Utilities.GetPlayers().Any(p=>!p.IsBot) ||
            job is not null || flashJob is not null || flashSearchCheckPending || flashVerificationCheckPending || DummyLayoutLoadActive || placementCheck is not null || flights.Count>0 || grenadeFlights.Count>0 || infernos.Count>0)
            throw new InvalidOperationException("practice/epoch/zero-human/exclusive-operation guard changed");
        foreach(var owner in run.Owners)
        {
            var p=Utilities.GetPlayerFromSlot(owner.Slot);
            sessions.TryGetValue(owner.Slot,out var session);
            if(p is not {IsValid:true,IsBot:true} || p.EntityHandle.Raw!=owner.Controller || p.SteamID!=owner.Steam ||
                p.PlayerPawn.Value is not {IsValid:true,LifeState:0} pawn || pawn.EntityHandle.Raw!=owner.Pawn ||
                !ReferenceEquals(session,owner.Session) || (session?.Revision ?? 0)!=owner.Revision || !EffectStressIdle(session) ||
                ThrowInProgress(p) || NativeWorld.FromNative(pawn.AbsVelocity).Length()>1)
                throw new InvalidOperationException($"slot {owner.Slot}: controller/pawn/session/idle guard changed");
            if(!run.Cleared && owner.Scene is not null &&
                (!effectPreviewScenes.TryGetValue(owner.Slot,out var scene) || !ReferenceEquals(scene,owner.Scene)))
                throw new InvalidOperationException($"slot {owner.Slot}: diagnostic scene was removed or replaced");
        }
    }

    private void EffectStressAssert(EffectStressRun run,bool valid,string detail)
    {
        if(valid) run.Passed++;else run.Failed++;
        Say(null,$"EFFECTSTRESS {(valid ? "PASS" : "FAIL")}: {detail}");
    }

    // 0 = move; 1 = marker deadline; 2 = settled range; 3 = retired entity deadline.
    private void EffectStressStep(EffectStressRun run,int phase)
    {
        if(!ReferenceEquals(effectStressCheck,run)) return;
        try
        {
            GuardEffectStress(run);
            switch(phase)
            {
                case 0:
                    run.RoundStarted=Server.CurrentTime;
                    foreach(var owner in run.Owners)
                    {
                        var player=Utilities.GetPlayerFromSlot(owner.Slot)!;
                        var center=owner.Base+EffectStressOffset(run.Round);
                        if((run.Round>0 && Vector3.Distance(center,owner.Center)<=2) ||
                            !EffectPlacement.Resolve(new NativeWorld(player.PlayerPawn.Value),owner.Kind,center).Valid)
                            throw new InvalidOperationException($"slot {owner.Slot}: invalid movement/air point");
                        owner.Center=center;
                        try {SetEffectPreview(player,owner.Kind,center,true,true,true);}
                        finally
                        {
                            // Capture a newly allocated scene even if later native setup throws.
                            if(owner.Scene is null && effectPreviewScenes.TryGetValue(owner.Slot,out var allocated)) owner.Scene=allocated;
                        }
                        var scene=effectPreviewScenes[owner.Slot];
                        if(owner.Scene is not null && !ReferenceEquals(owner.Scene,scene))
                            throw new InvalidOperationException($"slot {owner.Slot}: scene replaced during movement");
                        owner.Scene=scene;
                    }
                    ScheduleEffectStress(run,1,.25f);break;
                case 1:
                    foreach(var owner in run.Owners)
                    {
                        var scene=owner.Scene!;
                        bool ready=EffectMarkerReady(owner.Slot,owner.Kind,new(true,owner.Center,"stress"));
                        bool building=scene.Area && (scene.Builder is not null || scene.Work.Active is not null || scene.Work.Pending is not null || scene.RangeVersion!=scene.Work.LatestVersion);
                        if(ready && building) run.MarkersAheadOfRange++;
                        EffectStressAssert(run,ready,$"round={run.Round+1} slot={owner.Slot} marker at .25s checkpoint (elapsed={Server.CurrentTime-run.RoundStarted:F3}s); rangeBusy={building}; versions={scene.RangeVersion}/{scene.Work.LatestVersion}");
                    }
                    if(++run.Round<6) ScheduleEffectStress(run,0,.1f);
                    else ScheduleEffectStress(run,2,1.5f);
                    break;
                case 2:
                    // With flash range work removed, the remaining HE builds
                    // may finish before .25s. Fast completion is not a failure;
                    // phase 1 checks every marker against the actual deadline.
                    Say(null,$"EFFECTSTRESS INFO: markers ahead of pending range work: {run.MarkersAheadOfRange} observations");
                    foreach(var owner in run.Owners)
                    {
                        var scene=owner.Scene!;
                        bool present=effectPreviewScenes.TryGetValue(owner.Slot,out var current) && ReferenceEquals(scene,current);
                        bool drained=scene.Builder is null && scene.Work.Active is null && scene.Work.Pending is null;
                        int visible=scene.Beams.Count(b=>b.Visible && !b.Marker && b.Entity.IsValid && b.Version==scene.RangeVersion);
                        bool fullyPublished=scene.Beams.Count>=scene.Desired.Count && Enumerable.Range(0,scene.Desired.Count).All(i=>
                            scene.Beams[i].Entity.IsValid && scene.Beams[i].Visible && scene.Beams[i].Applied==scene.Desired[i] &&
                            EffectDrawRules.Current(scene.Beams[i].Version,scene.DesiredVersions[i]));
                        if(owner.Kind==GrenadeKind.Flash)
                            EffectStressAssert(run,present && drained && !scene.Area && scene.RangeSegments.Count==0 &&
                                scene.Beams.Count==3 && scene.Beams.All(b=>b.Marker) && fullyPublished,
                                $"slot={owner.Slot} flash preview removed: only three placement marker axes, no light beams or range work");
                        else
                            EffectStressAssert(run,present && drained && scene.RangeVersion>0 && scene.RangeVersion==scene.Work.LatestVersion &&
                                scene.RangeSegments.Count>0 && visible>0 && fullyPublished,
                                $"slot={owner.Slot} settled after 1.5s; queueEmpty={drained}; versions={scene.RangeVersion}/{scene.Work.LatestVersion}; range={scene.RangeSegments.Count}; visible={visible}; fullyPublished={fullyPublished}");
                    }
                    Say(null,"EFFECTSTRESS SETTLED: "+EffectPreviewDiagnostics);
                    run.RetiringEntities=effectBeamCounts.Values.Sum();
                    run.CleanupDeadline=Math.Max(1,run.RetiringEntities/(2f*64)+.5f);
                    Say(null,$"EFFECTSTRESS CLEAR: entities={run.RetiringEntities}; deadline={run.CleanupDeadline:F3}s; normal two-removals-per-tick budget retained.");
                    ClearEffectStressScenes(run);
                    run.Cleared=true;
                    ScheduleEffectStress(run,3,run.CleanupDeadline);break;
                case 3:
                    EffectStressAssert(run,effectPreviewScenes.Count==0 && retiredEffectBeams.Count==0 && effectBeamCounts.Values.All(n=>n==0) &&
                        run.Owners.All(o=>o.Scene!.Beams.All(b=>!b.Entity.IsValid)),
                        $"all {run.RetiringEntities} effect entities retired within {run.CleanupDeadline:F3}s; remaining={effectBeamCounts.Values.Sum()}");
                    FinishEffectStress(run,null);break;
            }
        }
        catch(Exception ex) {FinishEffectStress(run,$"phase {phase}: {ex.GetType().Name}: {ex.Message}");}
    }

    private void ClearEffectStressScenes(EffectStressRun run)
    {
        if(grenadeEpoch!=run.Epoch) return; // Reset owns old-world cleanup; never touch a replacement scene.
        foreach(var owner in run.Owners)
            if(owner.Scene is not null && effectPreviewScenes.TryGetValue(owner.Slot,out var scene) && ReferenceEquals(scene,owner.Scene))
                ClearEffectPreview(owner.Slot);
    }

    private void FinishEffectStress(EffectStressRun run,string? failure)
    {
        if(!ReferenceEquals(effectStressCheck,run)) return;
        try
        {
            if(failure is not null) EffectStressAssert(run,false,failure);
        }
        finally
        {
            effectStressCheck=null;
            try {ClearEffectStressScenes(run);}
            catch(Exception ex) {run.Failed++;Say(null,"EFFECTSTRESS FAIL: cleanup: "+ex.Message);}
            Say(null,$"EFFECTSTRESS {(run.Failed==0 ? "PASS" : "FAIL")} DONE: {run.Passed} passed, {run.Failed} failed; sessions/pawns were not modified; client pixels and CheckTransmit are not tested. "+EffectPreviewDiagnostics);
        }
    }
}
