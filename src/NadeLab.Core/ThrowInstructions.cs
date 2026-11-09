namespace NadeLab.Core;

public static class ThrowInstructions
{
    public static string Compact(Aim aim)
    {
        string action=aim.EffectiveAction switch
        {
            ThrowAction.Jump=>"原地跳投",ThrowAction.AirForwardJump=>"轻点W跳投",ThrowAction.Crouch=>"Ctrl蹲投",
            ThrowAction.Walk=>"W+Shift走投",ThrowAction.Run=>"W跑投",ThrowAction.RunJump=>RunUpKeys(aim)+"助跑跳投",
            ThrowAction.Step=>"起步投（控速）",ThrowAction.StepJump=>"起步跳投（控速）",_=>"站投"
        };
        return action+" · "+(aim.Mode switch {ThrowMode.Left=>"左键",ThrowMode.Right=>"右键",_=>"双键"});
    }
    // Keep the same run-jump speed split as the detailed execution guidance.
    public static string RunUpKeys(Aim aim)=>aim.EffectiveAction==ThrowAction.Walk ||
        aim.EffectiveAction==ThrowAction.RunJump && aim.ForwardSpeed<180 ? "W+Shift" : "W";

    public static string Operation(Aim aim)=>aim.EffectiveAction==ThrowAction.AirForwardJump ? "停稳蓄力，原地起跳时轻点W并松鼠标；勿先走一步" :
        aim.EffectiveAction==ThrowAction.StepJump ? "按动作时序起步、起跳并松鼠标；详情见菜单" :
        aim.Airborne ? aim.NeedsRunUp ? $"蓄力后{RunUpKeys(aim)}助跑，到标记处起跳并松鼠标" : "停稳蓄力，起跳同时松鼠标" :
        aim.EffectiveAction==ThrowAction.Crouch ? "按Ctrl蹲稳，蓄力后松鼠标" :
        aim.NeedsRunUp ? $"蓄力后{RunUpKeys(aim)}助跑，到标记处松鼠标并继续前进" :
        aim.ForwardSpeed>0 ? "蓄力后按指定节奏移动，看上方时机提示松鼠标" : "就位完成后，按指定鼠标键蓄力并手投";
}
