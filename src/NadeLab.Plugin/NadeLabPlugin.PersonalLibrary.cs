using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private sealed record LibraryRecording(PersonalLineup Entry,Session Session,int Revision,ImpactOwner Owner,uint Pawn,float Since,PersonalCapture Capture)
    {public RecordedImpact? Completed;}
    private sealed record LibraryPositioning(PersonalLineup Entry,Session Session,int Revision,ImpactOwner Owner,uint Pawn,Vector3 Destination,float Since);
    private readonly Dictionary<int,LibraryRecording> libraryRecordings=[];
    private readonly Dictionary<int,LibraryPositioning> libraryPositions=[];
    private readonly Dictionary<int,LibraryPositioning> libraryReplays=[];
    private readonly Dictionary<int,PersonalLineup> librarySelection=[];
    private readonly Dictionary<int,(GrenadeKind Category,int Page)> libraryBrowse=[];
    private readonly Dictionary<(int Slot,GrenadeKind Category),int> libraryPages=[];
    private void ClearLibraryBrowse(int slot)
    {
        libraryBrowse.Remove(slot);
        foreach(var key in libraryPages.Keys.Where(k=>k.Slot==slot).ToArray()) libraryPages.Remove(key);
    }
    private PersonalLineupStore PersonalLibrary=>new(Path.Combine(DataPath,"personal-lineups"));

    private void RegisterPersonalLibrary()
    {
        AddPlayerCommand("css_nl_library","Open your personal grenade library",(p,a)=>OpenPersonalLibrary(p));
        AddPlayerCommand("css_nl_library_name","Rename selected personal grenade",(p,a)=>EditPersonalLineup(p,string.Join(" ",a),false));
        AddPlayerCommand("css_nl_library_note","Set action note for selected personal grenade",(p,a)=>EditPersonalLineup(p,string.Join(" ",a),true));
        AddCommand("css_nl_librarycheck","Empty-server library error-boundary check",(caller,command)=>
        {
            if(caller is not null || !loaded || !Practice || Utilities.GetPlayers().Any(p=>!p.IsBot)) return;
            var bot=Utilities.GetPlayers().FirstOrDefault(p=>p.IsValid && p.IsBot);if(bot is null) {Say(null,"LIBRARYCHECK refused: idle bot required.");return;}
            int passed=0,failed=0;
            foreach(var error in new Exception[]{new InvalidDataException("diagnostic validation failure"),new IOException("diagnostic write failure"),new UnauthorizedAccessException("diagnostic access failure")})
            {
                try {if(!LibraryIo(bot,()=>throw error)) passed++;else failed++;}
                catch(Exception) {failed++;}
            }
            Say(null,$"LIBRARYCHECK DONE {(failed==0 ? "PASS" : "FAIL")}: {passed} handled errors; {failed} failures; no library files changed.");
        });
        RegisterListener<Listeners.OnTick>(TickPersonalLibrary);
        RegisterListener<Listeners.OnClientDisconnect>(slot=>{ClearPersonalActivity(slot);librarySelection.Remove(slot);ClearLibraryBrowse(slot);});
        RegisterListener<Listeners.OnMapEnd>(ClearPersonalLibrary);
    }
    private void ClearPersonalActivity(int slot)
    {libraryRecordings.Remove(slot);libraryPositions.Remove(slot);libraryReplays.Remove(slot);}
    private void ClearPersonalLibrary()
    {libraryRecordings.Clear();libraryPositions.Clear();libraryReplays.Clear();librarySelection.Clear();libraryBrowse.Clear();libraryPages.Clear();}
    private string? PersonalLibraryFeedback(CCSPlayerController p,float now)
    {
        if(libraryRecordings.TryGetValue(p.Slot,out var recording) && LibraryCurrent(p,recording.Session,recording.Revision,recording.Owner,recording.Pawn))
            return recording.Completed is not null ? "实投已捕获，保存未完成；菜单→我的道具库重试保存。" : recording.Capture.AwaitingImpact ? "已投出，等待这颗道具生效后保存；请勿连续补投。" :
                now-recording.Since<120 ? $"录制个人道具 · {120-(now-recording.Since):F0}秒内投出，生效后保存" : "录制窗口已结束，等待收尾；菜单→我的道具库可取消并重新录制。";
        if(libraryPositions.TryGetValue(p.Slot,out var positioning) && LibraryCurrent(p,positioning.Session,positioning.Revision,positioning.Owner,positioning.Pawn))
            return "正在确认收藏起点、瞄点和道具；请先站稳等待。";
        if(libraryReplays.TryGetValue(p.Slot,out var replay) && LibraryCurrent(p,replay.Session,replay.Revision,replay.Owner,replay.Pawn))
            return PanelText.Line(replay.Entry.Instruction,85);
        return null;
    }
    private bool RenderPersonalLibraryHud(CCSPlayerController p,Session s,float now)
    {
        if(PersonalLibraryFeedback(p,now) is null) return false;
        string text=libraryRecordings.ContainsKey(p.Slot) ? "我的道具库 · 录制中\n按自己的方法手投；生效后自动保存\n菜单→我的道具库取消或查看" :
            libraryPositions.ContainsKey(p.Slot) ? "我的道具库 · 正在就位\n停稳等待，确认切出道具后再手投" :
            "我的道具库 · 手动复现\n"+PanelText.Line(libraryReplays[p.Slot].Entry.Name,36)+"\n菜单→我的道具库再次就位或设置备注";
        SendBottomHud(p,s,text,now,keys:"鼠标 手投 · WASD 移动\nF1 菜单 · V 飞行观察",
            nativeKeys:"鼠标 手投 · 移动不变\n长按静步+换弹 菜单 · 飞行/道具/设置在菜单");return true;
    }
    private bool LibraryIo(CCSPlayerController player,Action operation)
    {
        try {operation();return true;}
        catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {Logger.LogWarning(ex,"Personal grenade library operation failed");Say(player,"道具库操作未完成："+ex.Message);return false;}
    }
    private PersonalLineup[]? ReadPersonalLibrary(CCSPlayerController p)
    {
        PersonalLineup[]? result=null;
        LibraryIo(p,()=>result=PersonalLibrary.Read(p.SteamID,Server.MapName).ToArray());return result;
    }
    private void OpenPersonalLibrary(CCSPlayerController? player)
    {
        if(!Check(player,out _)) return;
        var p=player!;var entries=ReadPersonalLibrary(p);if(entries is null) return;
        var menu=WorkflowPanel(p,"我的道具库 · "+Server.MapName,out var Add);menu.Back=OpenMenu;
        menu.Feedback=()=>"个人收藏，当前地图"+$" {entries.Length}/{PersonalLineupStore.MaximumEntries}；录制无需先选落点。";
        if(libraryRecordings.TryGetValue(p.Slot,out var recording))
        {
            menu.Feedback=()=>recording.Completed is null ? "录制中：按自己的方法投一颗，等它生效后自动保存。" : "实投已捕获，但保存失败；重试或取消，不会覆盖旧收藏。";
            if(recording.Completed is not null) Add("重试保存刚才的实投",who=>SavePersonalRecording(who,recording));
            Add("取消本次录制",who=>{libraryRecordings.Remove(who.Slot);Say(who,"已取消录制，已有收藏保留。");OpenPersonalLibrary(who);});
        }
        else
        {
            Add("录制下一颗自己的道具",who=>BeginPersonalRecording(who,false),entries.Length>=PersonalLineupStore.MaximumEntries,hint:"先在投掷起点瞄好；记录起点和真实生效点，投掷动作由你完成。");
            Add("收藏当前站位与瞄点",who=>BeginPersonalRecording(who,true),entries.Length>=PersonalLineupStore.MaximumEntries,hint:"不必投掷；保存为未验证姿态书签，可随后填写动作备注。");
        }
        foreach(var category in PersonalLibraryMenu.Categories)
        {
            int count=entries.Count(e=>PersonalLibraryMenu.Contains(category,e.Kind));
            Add(PersonalLibraryMenu.Label(category)+$" · {count}",who=>OpenPersonalList(who,category),key:category.ToString());
        }
        Add("返回训练菜单",OpenMenu);menu.Open(p);
    }
    private void ReturnToPersonalList(CCSPlayerController p)
    {
        if(libraryBrowse.TryGetValue(p.Slot,out var browse)) OpenPersonalList(p,browse.Category,browse.Page);
        else OpenPersonalLibrary(p);
    }
    private void OpenPersonalList(CCSPlayerController p,GrenadeKind category,int? requestedPage=null)
    {
        var all=ReadPersonalLibrary(p);if(all is null) return;
        var entries=all.Where(e=>PersonalLibraryMenu.Contains(category,e.Kind)).ToArray();
        const int size=5;
        int page=Math.Clamp(requestedPage ?? libraryPages.GetValueOrDefault((p.Slot,category)),0,Math.Max(0,(entries.Length-1)/size));
        libraryBrowse[p.Slot]=(category,page);libraryPages[(p.Slot,category)]=page;
        var menu=WorkflowPanel(p,$"{PersonalLibraryMenu.Label(category)} · 第{page+1}页",out var Add);menu.Back=OpenPersonalLibrary;
        menu.Identity+=$"/{Server.MapName}/{category}/{page}";
        menu.Feedback=()=>entries.Length==0 ? "本图暂无此类收藏；返回道具库可录制或收藏姿态。" : "本图收藏；有实投点也不代表战术效果已验证。";
        var visible=entries.Skip(page*size).Take(size).ToArray();menu.RestoreCount=visible.Length;
        foreach(var entry in visible)
            Add(PanelText.Line($"{(entry.ActualImpact is null ? "[未实投]" : "[有实投]")} {entry.Kind.Label()} · {entry.Name}",35),who=>OpenPersonalEntry(who,entry.Id),key:entry.Id.ToString());
        int current=page;
        if(page>0) Add("上一页",who=>OpenPersonalList(who,category,current-1),key:"previous");
        if((page+1)*size<entries.Length) Add("下一页",who=>OpenPersonalList(who,category,current+1),key:"next");
        Add("返回道具类别",OpenPersonalLibrary,key:"back");menu.Open(p);
    }
    private void OpenPersonalEntry(CCSPlayerController p,Guid id)
    {
        var entry=ReadPersonalLibrary(p)?.FirstOrDefault(x=>x.Id==id);
        if(entry is null) {Say(p,"这条收藏已不存在，请重新打开道具库。");return;}
        librarySelection[p.Slot]=entry;
        var category=PersonalLibraryMenu.Category(entry.Kind);
        if(!libraryBrowse.TryGetValue(p.Slot,out var browse) || browse.Category!=category)
            libraryBrowse[p.Slot]=(category,libraryPages.GetValueOrDefault((p.Slot,category)));
        var menu=WorkflowPanel(p,PanelText.Line(entry.Name,28),out var Add);menu.Back=ReturnToPersonalList;menu.Identity+="/"+entry.Id;
        bool current=entry.World==worldVersion?.ToJsonString();
        menu.Feedback=()=>PanelText.Line(entry.Instruction,70)+"\n"+(entry.ActualImpact is null ? "姿态书签 · 未记录实投" : "有真实生效点 · 战术效果未验证")+(current ? "" : " · 游戏/地图版本已变化");
        Add("就位并切出道具",who=>ConfirmPersonalPosition(who,entry),hint:entry.EyeHeight<56 ? "蹲姿收藏：按住Ctrl蹲稳再确认；恢复起点与视角，动作仍需手动完成。" : "恢复录制开始时的位置和视角；跳跃、移动、蓄力和释放仍需手动完成。");
        Add("查看位置 / 动作 / 落点",who=>ShowDetails(who,"个人道具详情",()=>
        {
            Say(who,$"{entry.Name} · {entry.Kind.Label()}\n{entry.Instruction}\n起点 {entry.Feet.X:F1}, {entry.Feet.Y:F1}, {entry.Feet.Z:F1}\n俯仰 {entry.Pitch:F2}° 水平 {entry.Yaw:F2}°\n保存于 {entry.SavedAt.ToLocalTime():yyyy-MM-dd HH:mm}");
            if(entry.ActualImpact is { } impact) Say(who,$"实投生效点 {impact.X:F1}, {impact.Y:F1}, {impact.Z:F1}；不代表封烟/致盲/伤害已验证。");
        },back=>OpenPersonalEntry(back,entry.Id)));
        Add("设置投掷动作备注",who=>OpenPersonalNote(who,entry));
        Add("改名 / 自定义备注",who=>ShowDetails(who,"收藏名称与备注",()=>Say(who,"自动名称可以直接使用。需要自定义时在聊天输入：\n!nl_library_name 你的名称\n!nl_library_note 你的投掷动作\n仅修改刚才选中的收藏。"),back=>OpenPersonalEntry(back,entry.Id)));
        Add("删除这条收藏",who=>ConfirmPersonalDelete(who,entry));menu.Open(p);
    }
    private void OpenPersonalNote(CCSPlayerController p,PersonalLineup entry)
    {
        var menu=WorkflowPanel(p,"选择动作备注",out var Add);menu.Identity+="/"+entry.Id;menu.Back=who=>{if(who is not null) OpenPersonalEntry(who,entry.Id);};
        foreach(string action in new[]{"站投","原地跳投","W移动投","静步投","W一步投","W跳投","蹲投"})
            Add(action,who=>
            {
                var buttons=WorkflowPanel(who,action+" · 鼠标方式",out var Button);buttons.Identity+="/"+entry.Id+"/"+action;buttons.Back=back=>{if(back is not null) OpenPersonalNote(back,entry);};
                foreach(string button in new[]{"左键","右键","双键"}) Button(button,who2=>
                {if(LibraryIo(who2,()=>PersonalLibrary.Replace(who2.SteamID,Server.MapName,entry,entry with {Instruction=action+" + "+button+"（用户备注）"}))) OpenPersonalEntry(who2,entry.Id);});
                buttons.Open(who);
            });
        menu.Open(p);
    }
    private void EditPersonalLineup(CCSPlayerController? p,string text,bool note)
    {
        if(!Check(p,out _) || !librarySelection.TryGetValue(p!.Slot,out var entry)) {Say(p,"先从我的道具库选择一条收藏。");return;}
        if(LibraryIo(p,()=>PersonalLibrary.Replace(p.SteamID,Server.MapName,entry,note ? entry with {Instruction=text.Trim()} : entry with {Name=text.Trim()})))
        {Say(p,note ? "动作备注已保存。" : "收藏名称已保存。");OpenPersonalEntry(p,entry.Id);}
    }
    private void ConfirmPersonalDelete(CCSPlayerController p,PersonalLineup entry)
    {
        var menu=WorkflowPanel(p,"删除当前收藏？",out var Add);menu.RememberSelection=false;menu.Identity+="/"+entry.Id;menu.Back=who=>{if(who is not null) OpenPersonalEntry(who,entry.Id);};
        menu.Feedback=()=>PanelText.Line(entry.Name,40)+"；仅删除这一条，其他收藏保留。";
        Add("取消",who=>OpenPersonalEntry(who,entry.Id));
        Add("确认删除",who=>{if(LibraryIo(who,()=>PersonalLibrary.Delete(who.SteamID,Server.MapName,entry))) {librarySelection.Remove(who.Slot);ReturnToPersonalList(who);}});menu.Open(p);
    }
    private void BeginPersonalRecording(CCSPlayerController p,bool bookmark)
    {
        if(!Check(p,out var pawn)) return;
        if(!StablePersonalPose(pawn)) {Say(p,"先退出飞行，在投掷起点站稳或蹲稳并瞄好，再录制。");return;}
        var s=Get(p);var kind=s.Kind;
        var active=pawn.WeaponServices?.ActiveWeapon.Value;
        if(active is {IsValid:true}) foreach(var k in Enum.GetValues<GrenadeKind>()) if(active.DesignerName==k.Weapon()) kind=k;
        var entry=new PersonalLineup(Guid.NewGuid(),$"{kind.Label()}-{DateTime.Now:MMdd-HHmmss}",kind,NativeWorld.FromNative(pawn.AbsOrigin!),pawn.ViewOffset.Z,
            pawn.EyeAngles.X,Geometry.NormalizeYaw(pawn.EyeAngles.Y),"按自己的动作手投；可在收藏中设置动作备注",null,null,worldVersion?.ToJsonString() ?? "{}",DateTimeOffset.UtcNow);
        if(bookmark)
        {if(LibraryIo(p,()=>PersonalLibrary.Add(p.SteamID,Server.MapName,entry))) {Say(p,"已收藏当前站位和瞄点（未验证）。");OpenPersonalEntry(p,entry.Id);}return;}
        Invalidate(p.Slot,s);s.Kind=kind;ClosePanel(p);libraryPositions.Remove(p.Slot);
        libraryRecordings[p.Slot]=new(entry,s,s.Revision,ImpactIdentity(p),pawn.EntityHandle.Raw,Server.CurrentTime,new(ImpactIdentity(p),pawn.EntityHandle.Raw,kind,Server.CurrentTime));
        GiveSelectedGrenade(p,kind);EquipSmoke(p);
        Say(p,$"开始录制{kind.Label()}：120秒内按自己的方法投一颗，生效后自动收藏。无需选择目标；菜单→我的道具库可取消。");
    }
    private void PersonalRelease(ImpactRelease release)
    {
        if(libraryRecordings.TryGetValue(release.Slot,out var recording))
            recording.Capture.Release(release.Owner,release.Pawn,release.Kind,release.At);
    }
    private void PersonalImpact(CCSPlayerController owner,ImpactRelease release,RecordedImpact impact)
    {
        if(!libraryRecordings.TryGetValue(owner.Slot,out var r) || !LibraryCurrent(owner,r.Session,r.Revision,r.Owner,r.Pawn) ||
            !r.Capture.Complete(ImpactIdentity(owner),release.Pawn,impact.Kind,release.At,Server.CurrentTime)) return;
        r.Completed=impact;SavePersonalRecording(owner,r);
    }
    private void SavePersonalRecording(CCSPlayerController owner,LibraryRecording recording)
    {
        if(recording.Completed is not { } impact || !LibraryCurrent(owner,recording.Session,recording.Revision,recording.Owner,recording.Pawn)) return;
        var entry=recording.Entry with {ActualImpact=impact.Position,ThrowId=impact.Id};
        if(!LibraryIo(owner,()=>PersonalLibrary.Add(owner.SteamID,Server.MapName,entry))) return;
        libraryRecordings.Remove(owner.Slot);librarySelection[owner.Slot]=entry;
        Say(owner,$"已加入我的道具库：{entry.Name}。起点、瞄点和真实生效点已保存；动作可在收藏中备注。");
    }
    private bool LibraryCurrent(CCSPlayerController? p,Session session,int revision,ImpactOwner owner,uint pawn)=>
        loaded && Practice && p is {IsValid:true} && ImpactIdentity(p)==owner && p.PlayerPawn.Value is {IsValid:true,LifeState:0} live &&
        live.EntityHandle.Raw==pawn && sessions.TryGetValue(p.Slot,out var current) && ReferenceEquals(current,session) && current.Revision==revision;
    private static bool StablePersonalPose(CCSPlayerPawn pawn)=>PersonalPose.Stable(pawn.MoveType==MoveType_t.MOVETYPE_WALK,(pawn.Flags&1)!=0,
        NativeWorld.FromNative(pawn.AbsVelocity),pawn.MovementServices?.As<CCSPlayer_MovementServices>().DuckAmount ?? (pawn.ViewOffset.Z<56 ? 1 : 0),pawn.ViewOffset.Z);
    private void ConfirmPersonalPosition(CCSPlayerController p,PersonalLineup entry)
    {
        if(entry.World==worldVersion?.ToJsonString()) {PositionPersonalLineup(p,entry);return;}
        var menu=WorkflowPanel(p,"旧版本收藏",out var Add);menu.Back=who=>{if(who is not null) OpenPersonalEntry(who,entry.Id);};
        menu.Feedback=()=>"游戏或地图版本已变化，落点可能不同。将检查脚下支撑，投法需要重新实测。";
        Add("返回",who=>OpenPersonalEntry(who,entry.Id));Add("仍然就位并重新测试",who=>PositionPersonalLineup(who,entry));menu.Open(p);
    }
    private void PositionPersonalLineup(CCSPlayerController p,PersonalLineup entry)
    {
        if(!Check(p,out var pawn)) return;
        if(ReadPersonalLibrary(p)?.FirstOrDefault(x=>x.Id==entry.Id)!=entry) {Say(p,"收藏已变化，请重新选择。");return;}
        if(pawn.MoveType!=MoveType_t.MOVETYPE_WALK) {Say(p,"先从菜单退出飞行并落地，再调用收藏。");return;}
        if(!PersonalPose.CanRecall(StablePersonalPose(pawn),pawn.ViewOffset.Z,entry.EyeHeight))
        {
            Logger.LogInformation("Personal library pose rejected: {Pose}; requiredEye={Eye}",PlacementDiagnosticPose(p,pawn),entry.EyeHeight);
            Say(p,entry.EyeHeight<56 ? "这条收藏是蹲姿起点：先按住Ctrl蹲稳，再确认就位。" : "这条收藏是站姿起点：先松开Ctrl站稳，再确认就位。");return;
        }
        var s=Get(p);Invalidate(p.Slot,s);s.Kind=entry.Kind;s.Target=null;s.TargetPreset=null;s.Start=null;s.RequestedStart=null;
        libraryRecordings.Remove(p.Slot);libraryPositions.Remove(p.Slot);ClosePanel(p);
        if(!ReturnToStart(p,pawn,entry.Feet) || !TryAlignPreparation(p,pawn,s,new(entry.Pitch,entry.Yaw,ThrowMode.Left))) return;
        GiveSelectedGrenade(p,entry.Kind);EquipSmoke(p);
        libraryPositions[p.Slot]=new(entry,s,s.Revision,ImpactIdentity(p),pawn.EntityHandle.Raw,NativeWorld.FromNative(pawn.AbsOrigin!),Server.CurrentTime);
        Say(p,entry.EyeHeight<56 ? "正在恢复收藏起点和瞄点；这条收藏使用低眼高，请按Ctrl蹲稳，等确认后再手投。" : "正在恢复收藏起点和瞄点，请站立停稳，确认道具切出后再手投。");
    }
    private void TickPersonalLibrary()
    {
        float now=Server.CurrentTime;
        foreach(var (slot,r) in libraryRecordings.ToArray())
        {
            var p=Utilities.GetPlayerFromSlot(slot);
            if(!LibraryCurrent(p,r.Session,r.Revision,r.Owner,r.Pawn)) {libraryRecordings.Remove(slot);continue;}
            if(r.Completed is null && now-r.Since>150)
            {libraryRecordings.Remove(slot);Say(p,"道具库录制已超时，未保存不完整记录；可以重新录制。");}
        }
        foreach(var (slot,r) in libraryPositions.ToArray())
        {
            var p=Utilities.GetPlayerFromSlot(slot);
            if(!LibraryCurrent(p,r.Session,r.Revision,r.Owner,r.Pawn)) {libraryPositions.Remove(slot);continue;}
            var pawn=p!.PlayerPawn.Value!;
            bool ready=StablePersonalPose(pawn) && Vector3.Distance(NativeWorld.FromNative(pawn.AbsOrigin!),r.Destination)<2 &&
                MathF.Abs(pawn.ViewOffset.Z-r.Entry.EyeHeight)<.75f &&
                MathF.Abs(pawn.EyeAngles.X-r.Entry.Pitch)<.1f && MathF.Abs(Geometry.NormalizeYaw(pawn.EyeAngles.Y-r.Entry.Yaw))<.1f &&
                pawn.WeaponServices?.ActiveWeapon.Value is {IsValid:true} weapon && weapon.DesignerName==r.Entry.Kind.Weapon();
            if(now-r.Since>.35f && ready)
            {libraryPositions.Remove(slot);libraryReplays[slot]=r;HudNotice(r.Session,$"收藏已就位 · {r.Entry.Name}\n{r.Entry.Instruction}\n手动完成投掷；菜单→我的道具库可再次调用。",5);}
            else if(now-r.Since>10)
            {libraryPositions.Remove(slot);Say(p,r.Entry.EyeHeight<56 ? "收藏就位未完成：这条收藏需要按Ctrl蹲稳，确认道具切出后再从道具库重试。" : "收藏就位未完成：请站立停稳、松开移动键并确认道具已切出，再从道具库重试。");}
        }
        foreach(var (slot,r) in libraryReplays.ToArray())
            if(!LibraryCurrent(Utilities.GetPlayerFromSlot(slot),r.Session,r.Revision,r.Owner,r.Pawn)) libraryReplays.Remove(slot);
    }
}
