using CounterStrikeSharp.API.Core;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    [Flags]
    private enum InputProbeSource { None=0,WheelUp=1,WheelDown=2,ShiftUp=4,ShiftDown=8,ArrowUp=16,ArrowDown=32 }

    private void OpenInputCheck(CCSPlayerController? player)
    {
        if(!Check(player,out _,allowPlacement:true)) return;
        InputCheckMenu().Open(player!);
    }

    private PanelMenu InputCheckMenu()
    {
        var menu=NewPanel("按键检测 · 使用或换弹结束");
        menu.AllowPlacement=true;menu.InputCheck=true;menu.RememberSelection=false;
        menu.Back=FinishInputCheck;
        // Two inert entries make navigation visible; confirming either only
        // ends the check. The current target, stance and search are untouched.
        menu.AddMenuOption("测试位置 A（使用键结束）",(who,_)=>FinishInputCheck(who));
        menu.AddMenuOption("测试位置 B（使用键结束）",(who,_)=>FinishInputCheck(who));
        return menu;
    }

    private void FinishInputCheck(CCSPlayerController player)
    {
        OpenMenu(player);
        var s=Get(player);
        HudNotice(s,HasCompanion(player) ? "检测结束。滚轮/↑↓选择，E确认、R返回；个人键位保持不变。" :
            "检测结束。请从启动器连接输入模块，再测试滚轮和方向键。",6);
    }

    private void ObservePanelInput(CCSPlayerController? player,InputProbeSource source)
    {
        // Never mark a real-client test successful using diagnostic bot calls.
        if(player?.IsValid!=true || player.IsBot || !CurrentPanel(player,out var panel) || !panel.Menu.InputCheck) return;
        panel.ObservedInputs|=source;
        Get(player).NextHudAt=0;
    }

    private void ReceivePanelDirection(CCSPlayerController? player,int delta)
    {
        if(!Practice || player?.IsValid!=true || !CurrentPanel(player,out _)) return;
        ObservePanelInput(player,delta<0 ? InputProbeSource.ArrowUp : InputProbeSource.ArrowDown);
        // As with wheel input, keep each notch/press instead of coalescing it
        // with a queued gameplay action later in the frame.
        MovePanel(player,delta);
    }

    private bool RenderInputCheckNotice(CCSPlayerController player)
    {
        if(!CurrentPanel(player,out var panel) || !panel.Menu.InputCheck) return false;
        string State(InputProbeSource source)=>panel.ObservedInputs.HasFlag(source) ? "已收到" : "未收到";
        if(!Get(player).LegacyControls && !HasCompanion(player))
        {
            SendNoticeText(player,PanelText.Bounded(
                "输入模块未连接\n请用启动器进入训练，再测试滚轮/方向键\n使用确认结束 · 换弹返回",360,3));
            return true;
        }
        SendNoticeText(player,PanelText.Bounded(
            $"滚轮上 {State(InputProbeSource.WheelUp)} · 下 {State(InputProbeSource.WheelDown)}\n"+
            $"方向↑ {State(InputProbeSource.ArrowUp)} · ↓ {State(InputProbeSource.ArrowDown)}\nE 结束检测 · R 返回",360,3));
        return true;
    }
}
