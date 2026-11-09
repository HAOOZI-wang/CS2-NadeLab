namespace NadeLab.Core;

// Only value facts cross this boundary: resolving a workflow cannot mutate a
// session, a search, a candidate, or a player entity.
public readonly record struct WorkflowSnapshot(
    bool Placement=false, bool Loading=false, bool RegionDraft=false,
    bool Flash=false, bool Searching=false, bool ReviewedCandidate=false,
    bool HasTarget=false, bool HasOrigin=false, bool HasCandidate=false,
    bool Continuation=false, bool Preparing=false, bool PreparationBlocked=false,
    bool Ready=false, bool Recording=false, bool Flying=false);

public enum WorkflowAction
{
    ConfirmPlacement, OpenLoading, EditRegion, SelectTarget, CaptureOrigin,
    ReviewSearch, Prepare, ContinueSearch, Search, FlashSearch,
    NextCandidate, Reposition, Menu, Flight, Region, Grenade, Effect, Retarget, CancelPlacement, Camera
}
public enum WorkflowAvailability { Available, Deferred }
public sealed record WorkflowShortcut(WorkflowAction Action,string Keys,string Label,WorkflowAvailability Availability=WorkflowAvailability.Available,int Row=0)
{
    public string Text=>Keys+Label;
}

public sealed record WorkflowResolution(WorkflowStep Step,WorkflowAction Primary,bool Flash,bool Flying,
    IReadOnlyList<WorkflowShortcut> Shortcuts)
{
    public WorkflowAvailability? Availability(WorkflowAction action)=>Shortcuts.FirstOrDefault(s=>s.Action==action)?.Availability;
}

public static class WorkflowModel
{
    public static WorkflowResolution Resolve(WorkflowSnapshot snapshot)
    {
        var s=snapshot;
        var keys=new List<WorkflowShortcut>();
        void Add(WorkflowAction action,string key,string label,bool deferred=false,int row=0)=>
            keys.Add(new(action,key,label,deferred ? WorkflowAvailability.Deferred : WorkflowAvailability.Available,row));
        WorkflowResolution Finish(WorkflowStep step,WorkflowAction primary)=>new(step,primary,s.Flash,s.Flying,keys.AsReadOnly());
        if(s.Placement)
        {
            Add(WorkflowAction.ConfirmPlacement,"E/F6","确认");Add(WorkflowAction.CancelPlacement,"R/F7/F5","取消");Add(WorkflowAction.Camera,"F9","视角");
            Add(WorkflowAction.Effect,"F4","预测",row:1);Add(WorkflowAction.Menu,"F1","菜单",row:1);
            return Finish(WorkflowStep.Placement,WorkflowAction.ConfirmPlacement);
        }
        if(s.Loading)
        {
            Add(WorkflowAction.OpenLoading,"E/F6","加载进度");Add(WorkflowAction.Menu,"F1","组合菜单");
            return Finish(WorkflowStep.Loading,WorkflowAction.OpenLoading);
        }
        if(s.RegionDraft)
        {
            Add(WorkflowAction.EditRegion,"E/R/Shift+E/Shift+R","回编辑");Add(WorkflowAction.Menu,"F1","菜单");Add(WorkflowAction.Flight,"V",s.Flying ? "退出飞行" : "飞行");
            Add(WorkflowAction.EditRegion,"F2/F6/F7/F8","编辑",row:1);Add(WorkflowAction.Grenade,"F3","换道具并弃草稿",row:1);Add(WorkflowAction.Effect,"F4","效果",row:1);
            return Finish(WorkflowStep.Region,WorkflowAction.EditRegion);
        }
        WorkflowAction primary=s.Searching && !s.HasCandidate ? WorkflowAction.ReviewSearch : s.Flash ? s.HasCandidate ? WorkflowAction.Prepare : WorkflowAction.FlashSearch :
            !s.HasTarget ? WorkflowAction.SelectTarget : !s.HasOrigin ? WorkflowAction.CaptureOrigin : s.HasCandidate ? WorkflowAction.Prepare :
            s.Continuation ? WorkflowAction.ContinueSearch : WorkflowAction.Search;
        WorkflowStep step=s.Searching && !s.HasCandidate ? WorkflowStep.Searching : s.Preparing ? WorkflowStep.Preparing : s.PreparationBlocked ? WorkflowStep.PreparationBlocked :
            s.Recording ? WorkflowStep.Recording : s.Ready ? WorkflowStep.Ready : s.Flash ? s.HasCandidate ? WorkflowStep.Result : WorkflowStep.Retry :
            !s.HasTarget ? WorkflowStep.Target : !s.HasOrigin ? WorkflowStep.Origin : s.HasCandidate ? WorkflowStep.Result : WorkflowStep.Retry;
        string primaryLabel=primary switch
        {
            WorkflowAction.SelectTarget=>"选点",
            WorkflowAction.CaptureOrigin=>s.Flying ? "记站位（先退出飞行落地）" : "记录站位并搜索",
            WorkflowAction.ReviewSearch=>s.ReviewedCandidate ? s.Flying ? "进度或采用（采用前落地）" : "进度或采用投法" : "查看进度",
            WorkflowAction.Prepare=>s.Flying ? "就位（落地后）" : s.Preparing ? "重新就位（等待准备完成）" : "就位",
            WorkflowAction.FlashSearch=>s.Flying ? "搜索（落地后）" : "搜索",
            _=>"继续搜索"
        };
        bool primaryDeferred=s.Flying && (primary is WorkflowAction.Prepare or WorkflowAction.CaptureOrigin or WorkflowAction.FlashSearch);
        Add(primary,"E/F6",primaryLabel,primaryDeferred);
        if(s.HasCandidate)
            Add(WorkflowAction.NextCandidate,"R/F7",s.Flying ? "看方案" : s.Flash ? "下一候选" : "下一方案");
        Add(WorkflowAction.Menu,"F1","菜单");Add(WorkflowAction.Flight,"V",s.Flying ? "退出飞行" : "飞行");
        Add(WorkflowAction.Region,"F2",s.Flash ? "要求" : step==WorkflowStep.Target ? "区域（选点后）" : s.Searching ? "区域（搜索后）" : "区域",!s.Flash && (s.Searching || !s.HasTarget),1);
        if(s.Flash)
            Add(WorkflowAction.Reposition,"Shift+R/F8",s.Flying ? "换站位（落地后）" : "换站位",s.Flying,1);
        Add(WorkflowAction.Grenade,"F3","道具",row:1);Add(WorkflowAction.Effect,"F4",s.Flash ? "结果" : "效果",row:1);
        Add(WorkflowAction.Retarget,"F5/Shift+E",s.Flash ? "改爆点" : "重选",row:1);
        if(!s.Flash && s.HasTarget)
            Add(WorkflowAction.Reposition,"Shift+R/F8",s.Flying ? "换站位（落地后）" : "换站位",s.Flying,1);
        return Finish(step,primary);
    }
}
