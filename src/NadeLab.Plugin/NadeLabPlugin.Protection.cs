using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private readonly Dictionary<nint,(CCSPlayerPawn Pawn,bool TakesDamage)> protectedPawns=[];

    private void SyncTrainingProtection()
    {
        if(!loaded) return;
        if(!Practice)
        {
            RestoreTrainingProtection();windowsTiming?.Dispose();windowsTiming=null;return;
        }
        if(OperatingSystem.IsWindows()) windowsTiming ??= new(Logger);
        foreach(var key in protectedPawns.Where(p=>!p.Value.Pawn.IsValid).Select(p=>p.Key).ToArray()) protectedPawns.Remove(key);
        foreach(var player in Utilities.GetPlayers())
        {
            if(player.PlayerPawn.Value is not {IsValid:true,LifeState:0} pawn) continue;
            // Training invulnerability belongs to humans only. A bot which has
            // not yet been claimed must never silently become an immortal AI.
            if(player.IsBot) {protectedPawns.Remove(pawn.Handle);pawn.TakesDamage=true;continue;}
            if(!protectedPawns.ContainsKey(pawn.Handle))
            {
                protectedPawns[pawn.Handle]=(pawn,pawn.TakesDamage);
                pawn.Health=Math.Max(pawn.Health,100);
                Utilities.SetStateChanged(pawn,"CBaseEntity","m_iHealth");
                if(!player.IsBot) Say(player,"训练保护已开启 · 输入模块连接后 F1 菜单、E 下一步；备用入口：聊天 !nl。个人键位文件保持原样。");
            }
            pawn.TakesDamage=false;
        }
    }

    private void RestoreTrainingProtection()
    {
        foreach(var (pawn,original) in protectedPawns.Values)
            if(pawn.IsValid && !pawn.TakesDamage) pawn.TakesDamage=original;
        protectedPawns.Clear();
    }

    private void ShowControls(CCSPlayerController? player)
    {
        if(player is {IsValid:true} && informationCapture is null)
        {ShowDetails(player,"按键与使用指引",()=>ShowControls(player));return;}
        if(player is {IsValid:true} && !Get(player).LegacyControls && !HasCompanion(player))
        {
            Say(player,"保持自己的键位：长按静步+换弹0.65秒开菜单\n识别游戏动作，使用你自己的静步键和换弹键\n也可聊天输入 !nl 打开菜单");
            Say(player,"滚轮/方向键需要启动器输入模块\n使用确认，换弹返回；主页返回即关闭\n移动、滚轮及个人功能键绑定不变");
            Say(player,"菜单外：使用键选点→搜索→就位，换弹键下一投法\n目标与站位菜单可换站位，保留目标\n换道具、重选目标、飞行、范围和视角均在菜单");
            Say(player,"菜单→我的道具库：录制自己的投法并再次调用\n菜单→假人组合：保存或加载假人\n菜单→按假人找闪：设置要闪/不闪，搜索后手投验证");
            return;
        }
        Say(player,"关闭菜单后：F3选道具，E选目标\n到出手位置站稳，E搜索\n有结果后E就位，你蓄力手投");
        Say(player,"烟/火/诱饵：准星指地面，E选点\n闪/雷：E变为道具，自由移动选爆点\n定位中E确认、R取消、F9换视角");
        Say(player,"关闭菜单后：R换投法\nShift+R换站位重搜，原目标保留\nShift+E重选目标；V飞行观察");
        Say(player,"F2调落点区：蓝线/红排除区\nF4效果参考；闪雷定位中开关假人预测\n预测不改变假人血量，真实效果另记");
        Say(player,"F1菜单：滚轮/↑↓选择，E确认\nR返回；主页R收起，WASD仍移动\n菜单打开时用F3换研究道具");
        Say(player,"底部：当前动作和可用按键\n上方：反馈、说明和实时状态\n投掷时机优先显示，假人状态单列");
        Say(player,"其他设置：飞行、无敌状态与显示\n训练玩家无敌，测试假人正常受伤\n跳投、移动投仍手动；跟随时机提示");
        Say(player,"F1 → 假人与寻闪 → 按假人找闪\n逐个设要闪/不闪，不指定爆点\n站稳后E搜索；F2改要求、F4看结果");
        Say(player,"F1 → 假人与寻闪 → 假人组合\n每张地图8槽，保存位置朝向及闪光要求\n加载替换自己的假人，先离开保存位置");
        Say(player,"F1 → 我的道具库：记录自己的已知道具\n在起点瞄好，再录制下一颗实投或收藏姿态\n选收藏即可就位并切出；移动、跳跃、释放仍手动");
    }
}
