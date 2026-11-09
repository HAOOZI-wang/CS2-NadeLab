namespace NadeLab.Core;

public enum WorkflowStep { Target, Origin, Searching, Result, Retry, Placement, Region, Loading, Preparing, PreparationBlocked, Ready, Recording }

public static class WorkflowKeys
{
    public static string Menu(WorkflowResolution workflow,bool pages=false,bool root=false)
    {
        string keys=PanelText.MenuKeys(workflow.Step==WorkflowStep.Placement,pages,workflow.Step==WorkflowStep.Region,
            workflow.Flash && workflow.Step!=WorkflowStep.Region,root);
        if(workflow.Step==WorkflowStep.Loading) return keys.Split('\n')[0]+"\nWASD移动";
        if(workflow.Flying) keys=keys.Replace("V飞行","V退出飞行",StringComparison.Ordinal);
        if(workflow.Availability(WorkflowAction.Region)==WorkflowAvailability.Deferred)
            keys=keys.Replace("F2区域",workflow.Step==WorkflowStep.Searching ? "F2区域（搜索后）" : "F2区域（选点后）",StringComparison.Ordinal);
        return keys;
    }
    public static string Text(WorkflowResolution workflow)
    {
        string Format(WorkflowShortcut key)=>key.Availability==WorkflowAvailability.Deferred ? key.Action switch
        {
            WorkflowAction.CaptureOrigin=>"先退出飞行落地，再E/F6记站位",
            WorkflowAction.Prepare=>"落地后E/F6就位",
            WorkflowAction.FlashSearch=>"落地后E/F6搜索",
            WorkflowAction.Reposition=>"落地后Shift+R/F8换站位",
            _=>key.Text
        } : key.Text;
        string first=string.Join(" · ",workflow.Shortcuts.Where(k=>k.Row==0).Select(Format));
        string second=string.Join(" · ",workflow.Shortcuts.Where(k=>k.Row==1).Select(Format));
        if(workflow.Step is WorkflowStep.Placement or WorkflowStep.Loading) second="WASD移动"+(second.Length>0 ? " · "+second : "");
        return first+(second.Length>0 ? "\n"+second : "");
    }

    // Compatibility entry points resolve the same action table as live sessions.
    public static string Flash(bool searching=false,bool hasCandidate=false,bool flying=false)=>
        Text(WorkflowModel.Resolve(new(Flash:true,Searching:searching,ReviewedCandidate:true,HasCandidate:hasCandidate,Flying:flying)));

    public static string Text(WorkflowStep step,bool flying=false)=>Text(WorkflowModel.Resolve(new(
        Placement:step==WorkflowStep.Placement,Loading:step==WorkflowStep.Loading,RegionDraft:step==WorkflowStep.Region,
        Searching:step==WorkflowStep.Searching,ReviewedCandidate:true,HasTarget:step!=WorkflowStep.Target,
        HasOrigin:step is not (WorkflowStep.Target or WorkflowStep.Origin),
        HasCandidate:step is WorkflowStep.Result or WorkflowStep.Preparing or WorkflowStep.PreparationBlocked or WorkflowStep.Ready or WorkflowStep.Recording,
        Continuation:step==WorkflowStep.Retry,Preparing:step==WorkflowStep.Preparing,PreparationBlocked:step==WorkflowStep.PreparationBlocked,
        Ready:step==WorkflowStep.Ready,Recording:step==WorkflowStep.Recording,Flying:flying)));
}
