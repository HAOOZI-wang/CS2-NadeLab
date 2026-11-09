using System.Security.Cryptography;
using System.Text;

namespace NadeLab.Core;

public sealed record CompanionShortcut(int Key,bool Shift,string Action,string Label,int Row=0);
public sealed record CompanionSnapshot(string Session,string Context,long SentAt,bool Menu,bool Root,
    CompanionShortcut[] Keys,ulong SteamId=0,string Status="",long Acknowledged=0);
public sealed record CompanionRequest(string Session,string Context,long Sequence,string Action,ulong SteamId=0,int Key=0,bool InputActive=false);
public static class CompanionProtocol
{
    public const int LeaseMilliseconds=700;
    public static bool CanRoute(CompanionSnapshot? state,long utcNow,long receiptAge)=>state is {Keys.Length:>0} &&
        receiptAge>=0 && receiptAge<LeaseMilliseconds && state.SentAt>0 && utcNow>=state.SentAt && utcNow-state.SentAt<LeaseMilliseconds;
    public static string PipeName(string gameContentRoot)=>"NadeLab.Input."+Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(gameContentRoot).TrimEnd('/','\\').ToUpperInvariant())))[..24];
}

public static class CompanionControls
{
    public static CompanionShortcut[] Resolve(WorkflowResolution flow,bool menu,bool root,bool batch,bool hasDummies=false)
    {
        var keys=new List<CompanionShortcut>();
        void Add(int key,string action,string label,int row=0,bool shift=false)=>keys.Add(new(key,shift,action,label,row));
        if(menu)
        {
            Add(-1,"up","选择");Add(-2,"down","");Add(0x26,"up","");Add(0x28,"down","");
            Add(0x45,"select","确认");Add(0x52,"back",root ? "收起" : "返回");Add(0x70,"menu","收起");
        }
        else if(batch) {Add(0x45,"primary","记录/放置");Add(0x52,"back","取消/结束");Add(0x70,"menu","菜单");}
        else
        {
            var primary=flow.Shortcuts.FirstOrDefault(k=>k.Action==flow.Primary);
            Add(0x45,"primary",flow.Step==WorkflowStep.Placement ? "确认爆点" : primary?.Label.Contains("就位")==true ? "就位" : primary?.Label.Contains("站位")==true ? "站位搜索" : primary?.Label.Contains("进度")==true ? "搜索进度" : primary?.Label.Contains("选点")==true ? "选目标" : primary?.Label.Contains("编辑")==true ? "编辑" : "搜索/继续");
            if(flow.Step==WorkflowStep.Placement) Add(0x52,"back","取消");
            else if(flow.Availability(WorkflowAction.NextCandidate) is not null) Add(0x52,"next","下一方案");
            Add(0x70,"menu","菜单");
        }
        if(flow.Step!=WorkflowStep.Loading)
        {
            int row=menu ? 1 : 0;
            if(flow.Step!=WorkflowStep.Placement) Add(0x56,"flight",flow.Flying ? "退出飞行" : "飞行",row);
            Add(0x72,"grenade","换道具",1); // Deliberately remains cycle, not a chooser.
            if(!menu && !batch)
            {
                if(flow.Availability(WorkflowAction.Region)==WorkflowAvailability.Available) Add(0x71,"region",flow.Flash ? "要求" : "范围",1);
                if(flow.Availability(WorkflowAction.Reposition) is not null) Add(0x52,"reposition","换站位",1,true);
            }
            if(!menu && (batch || flow.Availability(WorkflowAction.Retarget) is not null)) Add(0x45,"retarget","重选目标",1,true);
            if(!batch) Add(0x73,"effect",flow.Step==WorkflowStep.Placement ? "预测" : flow.Flash ? "结果" : "效果",1);
            if(flow.Step==WorkflowStep.Placement) Add(0x78,"camera","视角",1);
            if(hasDummies && flow.Step!=WorkflowStep.Placement) Add(0x74,"resetdummies","复位假人");
        }
        return keys.ToArray();
    }
    public static string Text(IEnumerable<CompanionShortcut> shortcuts)
    {
        string Key(CompanionShortcut s)=>(s.Shift ? "Shift+" : "")+(s.Key switch
        {-1=>"滚轮/↑↓",>=0x70 and <=0x7B=>"F"+(s.Key-0x6F),_=>((char)s.Key).ToString()});
        var visible=shortcuts.Where(k=>k.Label.Length>0).ToArray();
        return PanelText.Shortcuts(string.Join('\n',visible.GroupBy(k=>k.Row).OrderBy(g=>g.Key)
            .Select(g=>string.Join(" · ",g.Select(k=>Key(k)+" "+k.Label)))));
    }
}

public readonly record struct CompanionInputDecision(bool Block,string? Action=null);
public sealed class CompanionAcknowledgements
{
    private long seen;
    public long Value {get;private set;}
    public void Observe(long sequence)=>seen=Math.Max(seen,sequence);
    public void Complete(bool queued){if(!queued) Value=seen;}
}
public sealed class CompanionTransition
{
    private string session="";
    private long sequence,at;
    public void Begin(string peer,long id,long now){session=peer;sequence=id;at=now;}
    public bool Pending(string peer,long acknowledged,long now)
    {
        if(session!=peer || acknowledged>=sequence || now-at>=CompanionProtocol.LeaseMilliseconds) {session="";return false;}
        return session.Length>0;
    }
}
public sealed class CompanionInputRouter
{
    private readonly Dictionary<int,bool> held=[];
    private static bool Movement(int key)=>key is 0x57 or 0x41 or 0x53 or 0x44 or 0x20 or 0x10 or 0x11 or 0xA0 or 0xA1 or 0xA2 or 0xA3;
    public void Reset()=>held.Clear();
    public void Disarm(IEnumerable<int> keys){foreach(int key in keys) held[key]=false;}
    public CompanionInputDecision Route(int key,bool down,bool shift,bool active,bool typing,bool menu,
        IReadOnlyList<CompanionShortcut> shortcuts,IReadOnlyCollection<int> switchKeys)
    {
        if(!active)
        {
            // A passed press still owns a passed release after reconnection.
            // Never reinterpret its OS autorepeat as a fresh training action.
            if(key>=0) {if(down) held[key]=false;else held.Remove(key);}
            return default;
        }
        if(!down) {bool blocked=held.Remove(key,out var prior)&&prior;return new(blocked);}
        if(key>=0 && held.TryGetValue(key,out var old))
            return new(old,old && !typing && key is 0x26 or 0x28 ? shortcuts.FirstOrDefault(k=>k.Key==key&&k.Shift==shift)?.Action : null);
        var shortcut=typing || Movement(key) ? null : shortcuts.FirstOrDefault(k=>k.Key==key&&k.Shift==shift);
        bool block=shortcut is not null || (!typing && menu && !Movement(key) && switchKeys.Contains(key));
        if(key>=0) held[key]=block;
        return new(block,shortcut?.Action);
    }
}
