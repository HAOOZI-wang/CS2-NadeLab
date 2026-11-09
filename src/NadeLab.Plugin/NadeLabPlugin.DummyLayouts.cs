using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using NadeLab.Core;
using CsTrace=CounterStrikeSharp.API.Modules.Utils.Trace;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private sealed class DummyLayoutLoad
    {
        public required int Slot,Epoch,Revision,LayoutSlot;
        public required uint Owner,Pawn;
        public required ulong Steam;
        public required Session Session;
        public required string Map;
        public required DummyLayoutMember[] Members;
        public readonly HashSet<uint> Created=[];
        public int Next;
        public bool ReturnToFlash;
        public float CheckAt,Deadline;
        public DummyCreation? Request;
        public PanelMenu? ProgressMenu;
    }
    private DummyLayoutLoad? dummyLayoutLoad;
    private bool DummyLayoutLoadActive=>dummyLayoutLoad is not null;
    private DummyLayoutStore LayoutStore=>new(Path.Combine(DataPath,"dummy-layouts"));
    private void RegisterDummyLayouts()
    {
        AddPlayerCommand("css_nl_dummy_layouts","Saved map-specific dummy combinations",(p,args)=>OpenDummyLayouts(p));
        RegisterDummyLayoutDiagnostic();
        RegisterListener<Listeners.OnTick>(TickDummyLayoutLoad);
    }
    // Saving snapshots only static pose/role settings. Merely preparing a
    // grenade (Arm/Pending) must not block it or cancel any player's trial.
    private bool DummyLayoutEntitiesBusy=>DummyLayoutLoadActive || creatingDummy || dummyRemoving ||
        flashSearchCheckPending || flashVerificationCheckPending || dummyCheckPending || panelCheckPending || placementCheck is not null ||
        effectStressCheck is not null || recentImpactCheck is not null || measurementPawn is not null;
    private bool DummyLayoutEffectsBusy=>flights.Count>0 || grenadeFlights.Count>0 || infernos.Count>0 ||
        Utilities.GetPlayers().Any(p=>p.IsValid && !p.IsBot && ThrowInProgress(p));
    private bool DummyLayoutSaveBusy=>DummyLayoutEntitiesBusy || DummyLayoutEffectsBusy;
    private bool DummyLayoutLoadBusy(CCSPlayerController owner)=>DummyLayoutEntitiesBusy || DummyLayoutEffectsBusy ||
        job is not null || flashJob is not null ||
        effectDummies.Values.Any(d=>!d.Removing && (d.ResetRequested || !d.Ready)) ||
        sessions.Any(pair=>pair.Key!=owner.Slot && pair.Value.Placement is not null);

    private void DummyLayoutHome(CCSPlayerController player)
    {if(Get(player).FlashMode) OpenFlashMenu(player);else OpenEffectTarget(player);}
    private void DummyLayoutReadError(CCSPlayerController owner,int? slot,string message)
    {
        var menu=WorkflowPanel(owner,slot is { } number ? $"组合{number}" : "假人组合",out var Add);
        menu.Back=DummyLayoutHome;
        menu.Feedback=()=>"读取失败；当前假人、原文件和备份保留。";
        Add("重试读取",who=>{if(slot is { } selected) OpenDummyLayoutSlot(who,selected);else OpenDummyLayouts(who);});
        Add("返回训练",DummyLayoutHome);
        Say(owner,message);menu.Open(owner);
    }
    private void DummyLayoutRetry(CCSPlayerController owner,int slot,string message)
    {Say(owner,message);OpenDummyLayoutSlot(owner,slot);}
    private void OpenDummyLayouts(CCSPlayerController? player)
    {
        if(!Check(player,out _)) return;
        var p=player!;
        if(dummyLayoutLoad is { } active)
        {
            if(active.Owner!=p.EntityHandle.Raw || active.Steam!=p.SteamID)
            {
                var waiting=WorkflowPanel(p,"假人组合",out var Wait);
                waiting.Back=OpenMenu;
                waiting.Feedback=()=>DummyLayoutLoadActive ? "其他玩家正在加载组合，请稍后重试。" : "加载已结束，可以重新查看组合。";
                Wait("刷新状态",OpenDummyLayouts);Wait("关闭菜单",who=>ClosePanel(who));
                waiting.Open(p);return;
            }
            var progress=WorkflowPanel(p,$"组合{active.LayoutSlot} · 加载",out var Add,()=>ReferenceEquals(dummyLayoutLoad,active));
            progress.Back=OpenMenu;
            progress.Feedback=()=>ReferenceEquals(dummyLayoutLoad,active) ? $"已就位{active.Next}/{active.Members.Length}个假人" : "本次加载已结束。";
            active.ProgressMenu=progress;
            Add("停止加载",who=>FinishDummyLayoutLoad(active,who,"已停止加载",cleanup:false),hint:"停止后保留已就位的假人；保存文件不变。");
            progress.Open(p);return;
        }
        IReadOnlyList<DummyLayout> layouts;
        try {layouts=LayoutStore.Read(p.SteamID,Server.MapName);}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {Logger.LogWarning(ex,"Dummy combinations could not be read");DummyLayoutReadError(p,null,"组合文件无法读取，原文件及备份保留；当前假人未改动。");return;}
        var menu=WorkflowPanel(p,"假人组合 · "+(MapCatalog.Find(Server.MapName)?.Chinese ?? Server.MapName),out var Entry);
        menu.Back=DummyLayoutHome;
        menu.Feedback=()=>"每张地图8个槽；保存位置、朝向、护甲和要求。";
        for(int slot=1;slot<=DummyLayoutStore.SlotCount;slot++)
        {
            int selected=slot;var layout=layouts.FirstOrDefault(x=>x.Slot==slot);
            string text=layout is null ? $"组合{slot} · 空" : $"组合{slot} · {layout.Members.Count}个假人";
            Entry(text,who=>OpenDummyLayoutSlot(who,selected));
        }
        menu.Open(p);
    }
    private void OpenDummyLayoutSlot(CCSPlayerController owner,int slot)
    {
        if(!Check(owner,out _)) return;
        if(DummyLayoutLoadActive) {OpenDummyLayouts(owner);return;}
        DummyLayout? layout;
        try {layout=LayoutStore.Read(owner.SteamID,Server.MapName).FirstOrDefault(x=>x.Slot==slot);}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {Logger.LogWarning(ex,"Dummy combination slot could not be read");DummyLayoutReadError(owner,slot,"组合文件读取失败，当前假人保留。");return;}
        var menu=WorkflowPanel(owner,$"组合{slot} · {(layout is null ? "空槽" : layout.Members.Count+"个假人")}",out var Add);
        menu.Identity+="/"+slot;
        menu.Back=OpenDummyLayouts;
        menu.Feedback=()=>layout is null ? "先放置至少一个已就位的假人，再保存。" : "加载前先离开保存的位置，给假人留出空间。";
        if(layout is null) Add("保存当前假人",who=>SaveDummyLayout(who,slot));
        else
        {
            Add("加载组合，替换我的假人",who=>StartDummyLayoutLoad(who,slot),hint:"替换自己的全部假人；恢复100HP，不恢复旧受伤。");
            Add("用当前假人覆盖…",who=>ConfirmDummyLayoutOverwrite(who,slot,layout),hint:"下一步确认覆盖此槽；其他槽和当前假人保留。");
            Add("查看位置与闪光要求",who=>
            {
                ShowDetails(who,$"组合{slot}",()=>
                {
                    Say(who,"当前玩家 / 当前地图。加载恢复100HP、护甲设定、字母、朝向和闪光要求。");
                    foreach(var item in layout.Members) Say(who,$"{DummyRoster.Label(item.LabelIndex)} · {FlashRoleLabel(item.Role)} · {item.Armor}甲 · 位置 {item.Feet.X:0},{item.Feet.Y:0},{item.Feet.Z:0} · 朝向 {item.View.Yaw:0}°");
                });
                if(panels.TryGetValue(who.Slot,out var details)) details.Menu.Back=current=>OpenDummyLayoutSlot(current,slot);
            });
        }
        menu.Open(owner);
    }
    private void ConfirmDummyLayoutOverwrite(CCSPlayerController owner,int slot,DummyLayout previous)
    {
        if(!Check(owner,out _)) return;
        var menu=WorkflowPanel(owner,$"覆盖组合{slot}？",out var Add);
        menu.RememberSelection=false;
        menu.Back=who=>OpenDummyLayoutSlot(who,slot);
        menu.Feedback=()=>$"此槽原有{previous.Members.Count}个假人；将保存当前组合。";
        Add("返回，保留原组合",who=>OpenDummyLayoutSlot(who,slot));
        Add("确认覆盖此组合",who=>SaveDummyLayout(who,slot,previous.SavedAt),hint:"将替换此槽内容并备份上一版；其他槽保持不变。");
        menu.Open(owner);
    }
    private void SaveDummyLayout(CCSPlayerController owner,int slot,DateTimeOffset? expectedSavedAt=null)
    {
        if(!Check(owner,out _)) return;
        if(DummyLayoutSaveBusy) {DummyLayoutRetry(owner,slot,"正在处理假人或道具效果，请稍后重试保存；准备手投本身不影响保存。");return;}
        var dummies=OwnedDummies(owner);
        if(dummies.Length==0 || dummies.Any(d=>!d.Ready || d.ResetRequested))
        {DummyLayoutRetry(owner,slot,"先放置至少一个已就位的假人，再保存组合。");return;}
        var members=dummies.Select(SnapshotDummyLayout).ToArray();
        try
        {
            var previous=LayoutStore.Read(owner.SteamID,Server.MapName).FirstOrDefault(x=>x.Slot==slot);
            if(previous is not null && expectedSavedAt is null) {ConfirmDummyLayoutOverwrite(owner,slot,previous);return;}
            if(expectedSavedAt is not null && previous?.SavedAt!=expectedSavedAt)
            {DummyLayoutRetry(owner,slot,"保存槽已变化，请重新查看并确认覆盖；原文件保留。");return;}
            LayoutStore.Save(owner.SteamID,Server.MapName,slot,members);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {Logger.LogWarning(ex,"Dummy combination save failed");DummyLayoutRetry(owner,slot,"保存失败，旧组合和当前假人均保留，可重试。");return;}
        Say(owner,$"组合{slot}已保存：{members.Length}个假人，位置、朝向、护甲和闪光要求均保留。");OpenDummyLayoutSlot(owner,slot);
    }
    // PositionEffectDummy applies the placement clearance lift. Persist its
    // original input, otherwise every save/reload cycle adds another lift.
    private static DummyLayoutMember SnapshotDummyLayout(EffectDummy d)=>new(d.LabelIndex,d.Pose.Feet,d.Pose.EyeHeight,
        new(d.Pose.View.Pitch,Geometry.NormalizeYaw(d.Pose.View.Yaw),ThrowMode.Left),d.Pose.Armor,d.SearchRole);
    private void StartDummyLayoutLoad(CCSPlayerController owner,int slot)
    {
        if(!Check(owner,out var pawn)) return;
        if(DummyLayoutLoadBusy(owner)) {DummyLayoutRetry(owner,slot,"正在处理假人、搜索、定位或道具效果，请结束后重试；当前假人保留。");return;}
        DummyLayout? layout;
        try {layout=LayoutStore.Read(owner.SteamID,Server.MapName).FirstOrDefault(x=>x.Slot==slot);}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {Logger.LogWarning(ex,"Dummy combination load failed");DummyLayoutRetry(owner,slot,"组合文件无效，未改动当前假人。");return;}
        if(layout is null) {DummyLayoutRetry(owner,slot,"该组合为空，请先保存。");return;}
        var mine=OwnedDummies(owner);var owned=mine.Select(d=>d.Handle).ToHashSet();
        int others=effectDummies.Values.Count(d=>!owned.Contains(d.Handle));
        if(others+layout.Members.Count>MaxServerDummies)
        {DummyLayoutRetry(owner,slot,$"其他玩家已占用{others}个假人名额，此组合需要{layout.Members.Count}个，超过全服上限；当前假人保留。");return;}
        // Preflight excludes old player bodies from native hull traces, then
        // checks other live bodies explicitly. Nothing is moved for this check.
        foreach(var item in layout.Members)
        {
            if(!DummyLayoutSupported(item.Feet,out var safe) || Vector3.Distance(safe,item.Feet)>3.1f)
            {DummyLayoutRetry(owner,slot,$"{DummyRoster.Label(item.LabelIndex)}的位置被挡住或失去支撑；未替换当前假人。");return;}
            if(DummyLayoutBodyBlocked(safe,owned))
            {DummyLayoutRetry(owner,slot,$"{DummyRoster.Label(item.LabelIndex)}的位置有玩家或其他假人，请让开后重试；当前假人保留。");return;}
        }
        var session=Get(owner);bool returnToFlash=session.FlashMode;
        // Replacing this owner's targets ends only this owner's pending throw.
        // Keep the chosen workflow, but never reuse candidates for old handles.
        Invalidate(owner.Slot,session);session.FlashMode=returnToFlash;ClosePanel(owner);
        var load=new DummyLayoutLoad {Slot=owner.Slot,Owner=owner.EntityHandle.Raw,Pawn=pawn.EntityHandle.Raw,Steam=owner.SteamID,
            Epoch=grenadeEpoch,Map=Server.MapName,Session=session,Revision=session.Revision,LayoutSlot=slot,
            Members=layout.Members.OrderBy(x=>x.LabelIndex).ToArray(),Deadline=Server.CurrentTime+5,ReturnToFlash=returnToFlash};
        dummyLayoutLoad=load;
        foreach(var dummy in mine) RemoveDummy(dummy);
        Say(owner,$"正在加载组合{slot}，恢复{load.Members.Length}个假人；打开菜单查看进度或停止。");
        OpenDummyLayouts(owner);
    }
    private static bool DummyLayoutSupported(Vector3 feet,out Vector3 safe)
    {
        var mins=new CounterStrikeSharp.API.Modules.Utils.Vector(-16,-16,0);var maxs=new CounterStrikeSharp.API.Modules.Utils.Vector(16,16,72);
        var options=new TraceOptions {InteractsAs=Contents.Player,InteractsWith=Masks.PlayerSolid|Contents.PhysicsProp,InteractsExclude=Contents.Player|Contents.Hitbox};
        bool CanStand(Vector3 position)
        {
            var point=NativeWorld.ToNative(position);var clear=CsTrace.TraceHullShape(point,point,mins,maxs,null,options);
            if(clear.IsAllSolid || clear.DidHit()) return false;
            var floor=CsTrace.TraceHullShape(point,NativeWorld.ToNative(position-new Vector3(0,0,4)),mins,maxs,null,options);
            return floor.DidHit() && !floor.IsAllSolid && floor.Normal.Z>=.7f;
        }
        if(SafePlacement.TryRaise(feet,CanStand,out safe)) return true;
        var support=CsTrace.TraceHullShape(NativeWorld.ToNative(feet+new Vector3(0,0,18)),NativeWorld.ToNative(feet-new Vector3(0,0,4)),mins,maxs,null,options);
        return SafePlacement.TryStepSupport(feet,new(support.Fraction,NativeWorld.FromNative(support.EndPos),NativeWorld.FromNative(support.Normal),support.IsAllSolid),CanStand,out safe);
    }
    private static bool DummyLayoutBodyBlocked(Vector3 feet,HashSet<uint> ignoredControllers)=>Utilities.GetPlayers().Any(p=>
        p.IsValid && !ignoredControllers.Contains(p.EntityHandle.Raw) && p.PlayerPawn.Value is {IsValid:true,LifeState:0,AbsOrigin:not null} pawn &&
        MathF.Abs(pawn.AbsOrigin.X-feet.X)<33 && MathF.Abs(pawn.AbsOrigin.Y-feet.Y)<33 && MathF.Abs(pawn.AbsOrigin.Z-feet.Z)<73);
    private bool DummyLayoutIdentity(DummyLayoutLoad load,CCSPlayerController? owner)=>loaded && Practice && grenadeEpoch==load.Epoch && Server.MapName==load.Map &&
        owner is {IsValid:true,IsBot:false} && owner.EntityHandle.Raw==load.Owner && owner.SteamID==load.Steam &&
        owner.PlayerPawn.Value is {IsValid:true,LifeState:0} pawn && pawn.EntityHandle.Raw==load.Pawn &&
        sessions.TryGetValue(load.Slot,out var session) && ReferenceEquals(session,load.Session) && session.Revision==load.Revision;
    private void TickDummyLayoutLoad()
    {
        if(dummyLayoutLoad is not { } load || Server.CurrentTime<load.CheckAt) return;
        load.CheckAt=Server.CurrentTime+.1f;var owner=Utilities.GetPlayerFromSlot(load.Slot);
        try
        {
            if(!DummyLayoutIdentity(load,owner)) {FinishDummyLayoutLoad(load,null,"玩家或任务状态变化",cleanup:true);return;}
            if(ThrowInProgress(owner!)) {FinishDummyLayoutLoad(load,owner,"开始投掷，已中止加载",cleanup:true);return;}
            if(load.Request is { } request)
            {
                if(request.Claimed is { } claimed) load.Created.Add(claimed.Handle);
                if(ReferenceEquals(dummyCreation,request)) return;
                if(request.Claimed is not {Live:true,Ready:true,Removing:false,ResetRequested:false} target ||
                    target.Owner!=load.Owner || target.OwnerSteam!=load.Steam || target.LabelIndex!=load.Members[load.Next].LabelIndex || target.SearchRole!=load.Members[load.Next].Role)
                {FinishDummyLayoutLoad(load,owner,"有目标未能就位",cleanup:false);return;}
                load.Next++;load.Request=null;
                if(panels.TryGetValue(load.Slot,out var progress) && ReferenceEquals(progress.Menu,load.ProgressMenu))
                    load.Session.NextHudAt=0;
                if(load.Next==load.Members.Length) {FinishDummyLayoutLoad(load,owner,"加载完成",cleanup:false);return;}
            }
            if(dummyRemoving || creatingDummy)
            {if(Server.CurrentTime>load.Deadline) FinishDummyLayoutLoad(load,owner,"等待假人移除超时",cleanup:false);return;}
            var member=load.Members[load.Next];
            if(!DummyLayoutSupported(member.Feet,out var safe) || Vector3.Distance(safe,member.Feet)>3.1f || DummyLayoutBodyBlocked(safe,[]))
            {FinishDummyLayoutLoad(load,owner,$"{DummyRoster.Label(member.LabelIndex)}的位置现在被占用",cleanup:false);return;}
            PlaceEffectDummyCore(owner!,new(member.Feet,member.EyeHeight,member.View,member.Armor),member.LabelIndex,member.Role,fromLayout:true);
            if(dummyCreation is not { } pending || pending.Owner!=load.Owner || pending.Steam!=load.Steam || pending.PreferredLabel!=member.LabelIndex)
            {FinishDummyLayoutLoad(load,owner,"创建请求未被接受",cleanup:false);return;}
            load.Request=pending;load.Deadline=Server.CurrentTime+10;
        }
        catch(Exception ex)
        {Logger.LogError(ex,"Dummy combination interrupted");FinishDummyLayoutLoad(load,owner,"加载遇到错误",cleanup:true);}
    }
    private void FinishDummyLayoutLoad(DummyLayoutLoad load,CCSPlayerController? owner,string reason,bool cleanup)
    {
        if(!ReferenceEquals(dummyLayoutLoad,load)) return;
        dummyLayoutLoad=null;
        // Creation may have completed between the last layout tick and this
        // cancellation. Keep its full handle even after dummyCreation cleared.
        if(load.Request?.Claimed is { } newest) load.Created.Add(newest.Handle);
        if(load.Request is { } request && ReferenceEquals(dummyCreation,request))
        {
            request.Cancelled=true;request.Deadline=MathF.Min(request.Deadline,Server.CurrentTime+1);
            if(request.Claimed is { } claimed)
            {
                if(!cleanup && claimed.Ready && !claimed.Removing) dummyCreation=null;
                else RemoveDummy(claimed);
            }
            SetDummyQuota(0);
        }
        if(cleanup) foreach(uint handle in load.Created)
            if(effectDummies.TryGetValue(handle,out var target) && target.Owner==load.Owner && target.OwnerSteam==load.Steam) RemoveDummy(target);
        int retained=cleanup ? 0 : load.Created.Count(handle=>effectDummies.TryGetValue(handle,out var d) && d.Live && d.Ready && !d.Removing);
        if(owner is {IsValid:true} && owner.EntityHandle.Raw==load.Owner && owner.SteamID==load.Steam)
        {
            Say(owner,$"组合{load.LayoutSlot}：{reason}；已就位{retained}/{load.Members.Length}个。保存文件保留。");
            if(DummyLayoutIdentity(load,owner) && !ThrowInProgress(owner))
            {if(load.ReturnToFlash && load.Session.FlashMode) OpenFlashMenu(owner);else OpenEffectTarget(owner);}
        }
    }
    private void StopDummyLayoutLoad()
    {if(dummyLayoutLoad is { } load) FinishDummyLayoutLoad(load,null,"地图或插件状态重置",cleanup:true);}
    private void StopDummyLayoutLoad(int ownerSlot)
    {if(dummyLayoutLoad is { } load && load.Slot==ownerSlot) FinishDummyLayoutLoad(load,null,"玩家离开",cleanup:true);}
}
