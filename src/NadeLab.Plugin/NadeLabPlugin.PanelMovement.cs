using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private void RegisterPanelInput()
    {
        RegisterEventHandler<EventPlayerDeath>((ev,info)=>
        {if(ev.Userid is { } player) ClosePanel(player);return HookResult.Continue;});
        RegisterEventHandler<EventPlayerSpawn>((ev,info)=>
        {if(ev.Userid is { } player) ClosePanel(player);return HookResult.Continue;});
        // Wheel notches are UI-only work: preserve multiple events in one frame.
        AddCommand("css_nl_wheel_up","Scroll menu up",(p,c)=>ReceivePanelWheel(p,-1));
        AddCommand("css_nl_wheel_down","Scroll menu down",(p,c)=>ReceivePanelWheel(p,1));
        AddCommand("css_nl_inputstatus","Read received menu input counts",(p,c)=>
        {
            foreach(var player in Utilities.GetPlayers().Where(who=>who.IsValid && !who.IsBot && (p is null || who.Slot==p.Slot)))
                if(sessions.TryGetValue(player.Slot,out var session))
                    Say(p,$"INPUTSTATUS slot={player.Slot}; wheelUp={session.WheelUpEvents}; wheelDown={session.WheelDownEvents}; menuMoves={session.WheelMenuMoves}; menu={HasPanel(player)}");
        });
        RegisterPanelMovementDiagnostic();
    }
    private void ReceivePanelWheel(CCSPlayerController? player,int delta)
    {
        if(!Practice || player?.IsValid!=true || player.IsBot) return;
        var session=Get(player);
        if(delta<0) session.WheelUpEvents++;else session.WheelDownEvents++;
        if(CurrentPanel(player,out _)) session.WheelMenuMoves++;
        ObservePanelInput(player,delta<0 ? InputProbeSource.WheelUp : InputProbeSource.WheelDown);
        PanelWheel(player,delta);
    }
    private void PanelWheel(CCSPlayerController? player,int delta)
    {
        if(player?.IsValid!=true) return;
        if(CurrentPanel(player,out _)) MovePanel(player,delta);
        // No server-to-client invprev/invnext: that path is command-flag limited.
        // Outside a menu, a scroll must not move the player or switch a lineup.
    }

    private bool HandlePanelButtons(CCSPlayerController player,PlayerButtons pressed,PlayerButtons released=0)
    {
        if(!CurrentPanel(player,out _)) return false;
        bool select=(pressed&PlayerButtons.Use)!=0,back=(pressed&PlayerButtons.Reload)!=0;
        if(select || back)
        {
            var session=Get(player);
            if(Server.CurrentTime>=session.NextPracticeButtonAt)
            {
                var held=(player.Buttons|pressed)&~released;
                if((held&(PlayerButtons.Speed|PlayerButtons.Walk))!=0) return true;
                session.NextPracticeButtonAt=Server.CurrentTime+.25f;
                QueuePanelOrWorkflowAction(player,current=>{if(select) ActivatePanel(current);else BackPanel(current);},_=>{});
            }
            return true;
        }
        return true;
    }
}
