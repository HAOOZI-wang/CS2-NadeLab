using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private void OpenSmokeReview(CCSPlayerController? p)
    {
        if(!Check(p,out _)) return;
        var s=Get(p!);
        if(s.SmokeReview is not { } review || review.Revision!=s.Revision || review.Record.Arm.EffectObjective || review.Record.Arm.Target is not { } target)
        {Say(p,"先完成一颗已开启记录的手投，再反馈封烟效果。"); return;}
        string status=review.Record.Feedback?.Verdict switch {"sealed"=>"已反馈封满","leaking"=>"已反馈漏缝",_=>"封烟效果未确认"};
        int revision=s.Revision,epoch=grenadeEpoch;
        var menu=NewPanel($"本次实投 · {status}");menu.ExitButton=true;
        void Add(string label,Action<CCSPlayerController> action,bool disabled=false)=>menu.AddMenuOption(label,(who,_)=>
        {
            ClosePanel(who);
            QueueAction(who,current=>
            {
                if(!MenuIsCurrent(current,s,revision,epoch)) return;
                if(Get(current).SmokeReview?.Record.Id!=review.Record.Id) {Say(current,"这颗烟的记录已切换，请重新打开菜单。"); return;}
                action(current);
            });
        },disabled);
        if(!review.Queued) Add("实投记录未入队：重试保存",who=>{EnsureThrowQueued(who);OpenSmokeReview(who);});
        Add("观察窗口：已封满（本视角）",who=>RecordCoverage(who,review.Record.Id,"sealed"));
        Add("观察窗口：还有漏缝",who=>RecordCoverage(who,review.Record.Id,"leaking"));
        Add("无法确定 / 撤回本次判断",who=>RecordCoverage(who,review.Record.Id,"unknown"));
        Add("保存实投与反馈",who=>SaveLineup(who,Server.MapName+"-"+DateTime.Now.ToString("yyyyMMdd-HHmmss",CultureInfo.InvariantCulture)),s.LastVerified is null);
        Add("返回投掷菜单",who=>OpenMenu(who));
        Say(p,"请在烟雾展开后，从实际需要封锁的视角观察；其他烟雾、爆炸或射击干扰时选无法确定。反馈只代表你观察到的效果。");
        if(target.Objective?.ObserverEye is not null) Say(p,"此目标有预设观察点；反馈会注明是否在该位置32u、视角10°以内。可从菜单开启飞行前往观察，其他视角也可记录。");
        menu.Open(p!);
    }
    private void RecordCoverage(CCSPlayerController p,string throwId,string verdict)
    {
        if(!Check(p,out var pawn)) return;
        var s=Get(p);
        if(s.SmokeReview is not { } review || review.Record.Id!=throwId || review.Revision!=s.Revision || review.Record.Arm.EffectObjective || review.Record.Arm.Target is not { } target) return;
        float age=Server.CurrentTime-review.DetonatedAt;
        if(verdict!="unknown" && (age<3 || age>18))
        {Say(p,age<3 ? "烟雾仍在展开，请爆开3秒后再观察反馈。" : "这颗烟已超过反馈时段，请重新手投后观察，避免把消散当作漏缝。"); return;}
        if(!EnsureThrowQueued(p)) return;
        review=s.SmokeReview!;
        var feedback=new SmokeFeedback(verdict,DateTimeOffset.UtcNow.ToString("O"),age,
            NativeWorld.FromNative(pawn.AbsOrigin!)+new Vector3(0,0,pawn.ViewOffset.Z),new(pawn.EyeAngles.X,pawn.EyeAngles.Y,ThrowMode.Left));
        if(target.Objective is {ObserverEye:not null} objective)
            feedback=feedback with {ObjectiveViewMatched=objective.SameView(feedback.ObserverEye,feedback.ObserverView)};
        var updated=review.Record with {Feedback=feedback};
        // Separate append-only evidence: no historical throw is rewritten, and
        // a player's report must never become an engine-verified coverage claim.
        bool queued=telemetry!.Write(Path.Combine(DataPath,"coverage-feedback.jsonl"),()=>new JsonObject
        {
            ["SchemaVersion"]=1,["ThrowId"]=updated.Id,["Map"]=updated.Map,["WorldVersion"]=updated.WorldVersion?.DeepClone(),
            ["Target"]=JsonSerializer.SerializeToNode(target,NadeLabJsonContext.Default.Target),
            ["Actual"]=JsonSerializer.SerializeToNode(updated.Actual,NadeLabJsonContext.Default.Vector3),
            ["Matched"]=updated.Matched,["TargetHit"]=updated.TargetHit,["CoverageVerified"]=false,
            ["Feedback"]=FeedbackNode(feedback)
        }.ToJsonString(Json));
        if(!queued) {Say(p,"记录队列已满，本次反馈未保存，请稍后重试。"); return;}
        s.SmokeReview=review with {Record=updated};
        RememberActual(updated);
        if(updated.Success || updated.GuidedLandingSuccess) s.LastVerified=()=>SerializeThrow(updated);
        Say(p,verdict switch {"sealed"=>"反馈已排队：本视角封满。", "leaking"=>"反馈已排队：存在漏缝；落点命中不等同于封烟成功。",_=>"撤回已排队，恢复覆盖未确认。"});
        if(feedback.ObjectiveViewMatched is { } same) Say(p,same ? "已归入预设观察视角的人工反馈。" : "当前偏离预设观察视角：已单独记为其他视角反馈。");
        OpenSmokeReview(p);
    }
    private bool EnsureThrowQueued(CCSPlayerController p)
    {
        var s=Get(p);
        if(s.SmokeReview is not { } review || review.Record.Arm.EffectObjective || review.Record.Arm.Target is null) return false;
        if(review.Queued) return true;
        var snapshot=review.Record;
        if(!telemetry!.Write(Path.Combine(DataPath,"throws.jsonl"),()=>SerializeThrow(snapshot)))
        {Say(p,"记录队列仍然满，实投和反馈暂未保存，请稍后重试。");return false;}
        s.SmokeReview=review with {Queued=true};
        RememberActual(snapshot);
        Say(p,"实投记录已重新排队。");
        return true;
    }
}
