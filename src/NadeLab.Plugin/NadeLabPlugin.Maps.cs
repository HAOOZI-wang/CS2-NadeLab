using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Timers;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private string? pendingMapChange;
    private int mapChangeToken;
    private IEnumerable<string> GameContentRoots=>new[]{Server.GameDirectory,Path.Combine(Server.GameDirectory,"csgo"),Path.GetFullPath(Path.Combine(ModuleDirectory,"../../../.."))}.Distinct(StringComparer.OrdinalIgnoreCase);
    private string GameContentRoot=>GameContentRoots.FirstOrDefault(root=>File.Exists(Path.Combine(root,"steam.inf"))) ?? Server.GameDirectory;
    private string? InstalledMapVpk(string? name)
    {
        var map=MapCatalog.Find(name);
        if(map is null) return null;
        // Use the instance's actual maps, not an arbitrary installed Steam
        // library: installation and the world fingerprint share this path.
        return GameContentRoots.Select(root=>Path.Combine(root,"maps",map.Name+".vpk")).FirstOrDefault(File.Exists);
    }
    private void ResetMapSelection(){pendingMapChange=null;mapChangeToken++;}
    private void RegisterMapControls()
    {
        AddCommand("css_nl_mapstatus","Server-console installed training maps",(player,command)=>
        {
            if(player is not null) return;
            var installed=MapCatalog.Installed(name=>InstalledMapVpk(name) is not null);
            Say(null,$"MAPS: current={Server.MapName}; supported={MapCatalog.IsSupported(Server.MapName)}; installed={installed.Count}; pending={pendingMapChange ?? "none"}; names={string.Join(',',installed.Select(map=>map.Name))}");
        });
    }
    private static bool MayChangeMap(CCSPlayerController player)=>MapCatalog.CanChange(
        Utilities.GetPlayers().Count(who=>who.IsValid && !who.IsBot && !who.IsHLTV),
        AdminManager.PlayerHasPermissions(player,"@css/changemap") || AdminManager.PlayerHasPermissions(player,"@css/root"));
    private void OpenMapMenu(CCSPlayerController player)
    {
        if(!Check(player,out _)) return;
        if(pendingMapChange is not null) {Say(player,"正在切换地图，请稍候。");return;}
        var current=MapCatalog.Find(Server.MapName);
        var menu=WorkflowPanel(player,$"切图 · 当前{current?.English ?? Server.MapName}",out var Add);
        var installed=MapCatalog.Installed(name=>InstalledMapVpk(name) is not null);
        foreach(var map in installed)
        {
            bool same=string.Equals(map.Name,Server.MapName,StringComparison.OrdinalIgnoreCase);
            Add($"{map.DisplayName} · {(same ? "当前" : map.ActiveDuty ? "服役" : "退役")}",who=>ConfirmMapChange(who,map.Name),same);
        }
        if(installed.Count==0) Add("尚未安装可选地图",_=>{},true);
        Add("返回主面板",OpenMenu);
        menu.Open(player);
        if(!MayChangeMap(player)) Say(player,"多人训练时仅管理员可切图；可用滚轮浏览已安装地图。");
    }
    private void ConfirmMapChange(CCSPlayerController player,string name)
    {
        if(!Check(player,out _)) return;
        var map=MapCatalog.Find(name);
        if(map is null || InstalledMapVpk(map.Name) is null) {Say(player,"该地图尚未安装，请返回地图列表。当前训练保留。");return;}
        if(!MayChangeMap(player)) {Say(player,"多人训练时仅有切图权限的管理员可操作，避免中断其他玩家。");OpenMapMenu(player);return;}
        var menu=WorkflowPanel(player,"切图将清空全服本轮训练",out var Add);
        menu.RememberSelection=false;
        menu.Back=OpenMapMenu;
        // Cancel is the first selection; a repeated confirmation key never
        // turns merely opening this panel into a destructive training reset.
        Add("取消，继续当前训练",OpenMapMenu);
        Add($"确认切换：{map.DisplayName}",who=>ChangeTrainingMap(who,map.Name));
        menu.Open(player);
        Say(player,"切图会清空全服本轮目标、搜索和假人；已保存投法与实投记录会保留。");
    }
    private void ChangeTrainingMap(CCSPlayerController player,string name)
    {
        if(!Check(player,out _)) return;
        var map=MapCatalog.Find(name);
        if(map is null || InstalledMapVpk(map.Name) is null) {Say(player,"地图文件已不可用，当前训练保留。");return;}
        if(pendingMapChange is not null) {Say(player,"已有切图请求，请稍候。");return;}
        if(string.Equals(map.Name,Server.MapName,StringComparison.OrdinalIgnoreCase)) {Say(player,"已在此地图，当前训练保留。");OpenMapMenu(player);return;}
        // Recheck permission and connected humans at the final action, after
        // the ordinary panel identity/revision/epoch guard has been consumed.
        if(!MayChangeMap(player)) {Say(player,"当前多人训练，你没有切图权限；当前训练保留。");return;}
        pendingMapChange=map.Name;int token=++mapChangeToken,epoch=grenadeEpoch;
        foreach(var who in Utilities.GetPlayers().Where(who=>who.IsValid && !who.IsBot && !who.IsHLTV))
            Say(who,$"正在切换至{map.DisplayName}；本轮目标、搜索和假人将清空，已保存记录保留。");
        // All command text originates in the fixed catalog; no user string is
        // interpolated. Existing MapEnd/MapStart cleanup owns state disposal.
        try {Server.ExecuteCommand("changelevel "+map.Name);}
        catch {pendingMapChange=null;throw;}
        AddTimer(15,()=>
        {
            if(!loaded || token!=mapChangeToken || epoch!=grenadeEpoch) return;
            pendingMapChange=null;
            Say(null,"地图切换未完成；已解除待切换状态。请检查服务器地图文件和控制台。");
        },TimerFlags.STOP_ON_MAPCHANGE);
    }
}
