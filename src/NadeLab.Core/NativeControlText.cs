namespace NadeLab.Core;
public static class NativeControlText
{
    public static string Keys(WorkflowResolution flow,bool menu,bool root=false)=>menu ?
        "使用 确认 · 换弹 "+(root ? "关闭" : "返回")+" · 移动不变\n滚轮/方向键：请从启动器连接输入模块" :
        (flow.Step==WorkflowStep.Placement ? "使用 确认爆点 · 换弹 取消" : "使用 "+flow.Shortcuts.First(s=>s.Action==flow.Primary).Label+
            (flow.Availability(WorkflowAction.NextCandidate) is not null ? " · 换弹 下一投法" : ""))+
        "\n长按静步+换弹 菜单 · 飞行/道具/设置在菜单";
    // Shortcut presentation never rewrites composed content: it can contain
    // dummy identities (E紫), personal names or instructions using literal keys.
    public static (string Text,string Keys) Panel(string text,WorkflowResolution flow,bool menu,bool root=false)
        =>(text,Keys(flow,menu,root));
}
