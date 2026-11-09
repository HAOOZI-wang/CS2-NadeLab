using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using NadeLab.Core;

namespace NadeLab.Plugin;
public sealed partial class NadeLabPlugin
{
    // This only changes presentation/input interpretation. Keep it synchronous:
    // legacy F1 executes the cfg and then css_nl in the same command batch.
    // Queuing both would consume the one-action slot before the menu command.
    private void RegisterNativeControls()=>AddCommand("css_nl_controls","Choose native or explicitly installed legacy controls",(p,command)=>
    {
        if(!Practice || p is not {IsValid:true,IsBot:false}) return;
        string mode=command.GetArg(1);
        if(mode is not ("native" or "legacy")) {Say(p,"css_nl_controls native|legacy；切换说明模式不修改或恢复个人键位。");return;}
        var s=Get(p);s.LegacyControls=mode=="legacy";s.MenuHold.Reset();s.NextHudAt=0;s.LastHudText=null;
        Say(p,s.LegacyControls ? "兼容快捷键模式；仅在你主动加载旧快捷键配置时使用。" : "原键位模式：长按静步+换弹开菜单。此命令不改键位；已加载旧配置需先按记录恢复。");
    });
    private void TickNativeControls()
    {
        foreach(var p in Utilities.GetPlayers())
        {
            if(!p.IsValid || p.IsBot) continue;
            var s=Get(p);var pawn=p.PlayerPawn.Value;uint identity=pawn is {IsValid:true} ? pawn.EntityHandle.Raw : 0;
            if(s.MenuPawn!=identity) {s.MenuHold.Reset();s.MenuPawn=identity;}
            var buttons=p.Buttons;
            bool chord=(buttons&(PlayerButtons.Speed|PlayerButtons.Walk))!=0 && (buttons&PlayerButtons.Reload)!=0;
            // In menus Walk+Reload remains the downward navigation action.
            bool allowed=!HasCompanion(p) && !s.LegacyControls && pawn is {IsValid:true,LifeState:0} && !HasPanel(p) && !ThrowInProgress(p);
            if(s.MenuHold.Observe(Server.CurrentTime,chord,allowed)) QueueAction(p,OpenMenu);
        }
    }
    private void ToggleTrainingFlight(CCSPlayerController p)
    {
        if(!Check(p,out var pawn)) return;
        bool flying=pawn.MoveType==MoveType_t.MOVETYPE_NOCLIP;
        var mode=flying ? MoveType_t.MOVETYPE_WALK : MoveType_t.MOVETYPE_NOCLIP;
        SetMovement(pawn,mode,mode);
        var s=Get(p);s.Pending=null;s.Arm=null;s.FlightCheck=null;s.PreparationIssue=null;s.CueStage=ReleaseStage.None;s.PreparationRevision++;
        Say(p,flying ? "已退出飞行；落地停稳后继续。" : "已开启飞行；菜单可退出飞行，个人键位未改动。");
    }
}
