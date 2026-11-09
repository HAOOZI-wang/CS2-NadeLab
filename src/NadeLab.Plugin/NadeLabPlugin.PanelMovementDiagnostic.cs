using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private void RegisterPanelMovementDiagnostic()=>AddCommand("css_nl_movementcheck","Empty-server menu movement and wheel checks",(caller,command)=>
    {
        if(caller is not null || !loaded || !Practice || Utilities.GetPlayers().Any(p=>!p.IsBot) ||
            placementCheck is not null || panelCheckPending || effectStressCheck is not null || job is not null || flashJob is not null || flashSearchCheckPending || flashVerificationCheckPending || DummyLayoutLoadActive ||
            flights.Count>0 || grenadeFlights.Count>0 || panels.Count>0)
        {Say(null,"MOVEMENTCHECK refused: idle practice server with zero humans required.");return;}
        var owner=Utilities.GetPlayers().FirstOrDefault(p=>p.IsValid && p.IsBot && p.PlayerPawn.Value is {IsValid:true,LifeState:0} && !ThrowInProgress(p));
        if(owner is null) {Say(null,"MOVEMENTCHECK refused: one idle living bot required.");return;}
        var pawn=owner.PlayerPawn.Value!;
        sessions.TryGetValue(owner.Slot,out var previous);
        if(previous?.Placement is not null || previous?.Pending is not null || previous?.Arm is not null)
        {Say(null,"MOVEMENTCHECK refused: bot has active workflow state.");return;}
        var move=pawn.MoveType;var actual=pawn.ActualMoveType;
        var velocity=NativeWorld.FromNative(pawn.AbsVelocity);
        uint controller=owner.EntityHandle.Raw,handle=pawn.EntityHandle.Raw;int epoch=grenadeEpoch;
        int passed=0,failed=0;
        void CheckState(bool ok,string text)
        {if(ok) passed++;else failed++;Say(null,$"MOVEMENTCHECK {(ok ? "PASS" : "FAIL")}: {text}");}
        var sandbox=new Session {SteamId=owner.SteamID,HudEnabled=false};
        try
        {
            sessions[owner.Slot]=sandbox;
            foreach(var mode in new[]{MoveType_t.MOVETYPE_WALK,MoveType_t.MOVETYPE_NOCLIP})
            {
                SetMovement(pawn,mode,mode);var impulse=new Vector3(140,25,32);pawn.Teleport(velocity:impulse);
                var menu=NewPanel("movement diagnostic");menu.IsRoot=true;menu.RememberSelection=false;
                for(int i=0;i<4;i++) menu.AddMenuOption($"item {i}",(_,_)=>{});
                OpenPanelCore(owner,menu);
                CheckState(pawn.MoveType==mode && pawn.ActualMoveType==mode &&
                    Vector3.Distance(NativeWorld.FromNative(pawn.AbsVelocity),impulse)<.01f,$"{mode}: open preserves movement and velocity");
                HandlePanelButtons(owner,PlayerButtons.Forward);
                HandlePanelButtons(owner,PlayerButtons.Back);
                CheckState(CurrentPanel(owner,out var selected) && selected.Selected==0,$"{mode}: W/S never select");
                PanelWheel(owner,1);PanelWheel(owner,1);PanelWheel(owner,1);
                CheckState(CurrentPanel(owner,out selected) && selected.Selected==3,$"{mode}: three same-frame wheel notches retained");
                PanelWheel(owner,-1);
                CheckState(CurrentPanel(owner,out selected) && selected.Selected==2,$"{mode}: reverse wheel direction");
                ReceivePanelDirection(owner,1);ReceivePanelDirection(owner,1);ReceivePanelDirection(owner,-1);
                CheckState(CurrentPanel(owner,out selected) && selected.Selected==3,$"{mode}: same-frame direction events are not coalesced");
                PanelWheel(owner,-1);
                sandbox.NextPracticeButtonAt=0;
                HandlePanelButtons(owner,PlayerButtons.Speed|PlayerButtons.Use);
                CheckState(CurrentPanel(owner,out selected) && selected.Selected==2,$"{mode}: Shift+E does not navigate or activate menus");
                sandbox.NextPracticeButtonAt=0;
                HandlePanelButtons(owner,PlayerButtons.Speed|PlayerButtons.Reload);
                CheckState(CurrentPanel(owner,out selected) && selected.Selected==2,$"{mode}: Shift+R does not navigate or return from menus");
                BackPanel(owner);
                PanelWheel(owner,1);
                CheckState(!HasPanel(owner) && pawn.MoveType==mode &&
                    Vector3.Distance(NativeWorld.FromNative(pawn.AbsVelocity),impulse)<.01f,$"{mode}: close preserves motion");
                OpenPanelCore(owner,menu);sandbox.Revision++;
                CheckState(!HasPanel(owner) && pawn.MoveType==mode,$"{mode}: stale panel closes without altering movement");
            }
            var targetBefore=sandbox.Target;var startBefore=sandbox.Start;int revisionBefore=sandbox.Revision;
            OpenPanelCore(owner,InputCheckMenu());
            CheckState(CurrentPanel(owner,out var probe) && probe.Menu.InputCheck && probe.Menu.Options.Count==2,
                "input check opens with two inert navigation entries");
            ObservePanelInput(owner,InputProbeSource.WheelUp);ReceivePanelDirection(owner,1);
            CheckState(CurrentPanel(owner,out probe) && probe.Selected==1 && probe.ObservedInputs==InputProbeSource.None,
                "diagnostic bot navigation never confirms real client input");
            ClosePanel(owner);
            CheckState(sandbox.Target==targetBefore && sandbox.Start==startBefore && sandbox.Revision==revisionBefore,
                "input check keeps target, origin and workflow revision");
            OpenPanelCore(owner,InputCheckMenu());
            CheckState(CurrentPanel(owner,out probe) && probe.ObservedInputs==InputProbeSource.None && probe.Selected==0,
                "reopening input check starts a fresh measurement");
        }
        catch(Exception ex) {failed++;Say(null,"MOVEMENTCHECK exception: "+ex.Message);}
        finally
        {
            if(grenadeEpoch==epoch && owner.IsValid && owner.EntityHandle.Raw==controller && owner.PlayerPawn.Raw==handle && pawn.IsValid)
            {
                ClosePanel(owner);SetMovement(pawn,move,actual);pawn.Teleport(velocity:velocity);
                if(previous is null) sessions.Remove(owner.Slot);else sessions[owner.Slot]=previous;
            }
        }
        Say(null,$"MOVEMENTCHECK DONE {(failed==0 ? "PASS" : "FAIL")}: {passed} passed; {failed} failed; real client wheel/Shift input and pixels require client validation.");
    });
}
