using System.Globalization;
using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Menu;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private void SelectCandidate(CCSPlayerController p,int id)
    {
        if(Get(p).Selected!=id) {ClearFlashVerification(p);Get(p).FlightCheck=null;Get(p).PreparationRevision++;}
        var s=Get(p); s.Selected=id; s.HudNotice=null; s.NextHudAt=0; s.LastHudText=null; s.Pending=null; s.PreparationIssue=null;
        if (s.Arm is { } arm && s.Result?.Matches.ElementAtOrDefault(id-1)!=arm.Candidate) s.Arm=null;
    }
    private void NextCandidate(CCSPlayerController? p,CommandInfo c)
    {
        if(p?.IsValid==true && sessions.TryGetValue(p.Slot,out var placing) && placing.Placement is not null) {FinishPlacement(p,false);return;}
        if(p?.IsValid==true && CurrentFlashVerification(p) is {Trial.Complete:false,Trial.ReleasedAt:not null})
        {Say(p,"本次闪光仍在观测；汇总完所有假人后再切换投法。");return;}
        if (!Check(p,out _)) return;
        var s=Get(p!);
        if(s.RegionDraft is not null) {OpenRegionEditor(p);return;}
        if (s.Result?.Matches is not {Count:>0} matches) {Say(p,s.FlashMode ? "在寻闪菜单设置假人要求，在投掷位置站稳后按使用键搜索。" : "先用使用键选目标，再到出手位置按使用键搜索。"); return;}
        int next=s.Selected%matches.Count+1;
        if(s.FlashMode && s.FlashPlan?.Trials is { } trials)
        {
            if(trials.Next(s.Selected) is not { } available)
            {Say(p,"没有其他未失败投法；保留当前方案。使用键可重试，菜单可从当前位置重搜。");return;}
            next=available;
        }
        ClosePanel(p!); SelectCandidate(p!,next);
        if(p!.PlayerPawn.Value?.MoveType==MoveType_t.MOVETYPE_NOCLIP)
        {RefreshSelectedGuides(p);Say(p,$"飞行观察：已切换到投法{s.Selected}/{matches.Count}，从菜单退出飞行后按使用键就位。");return;}
        PrepareThrow(p!,s.Selected.ToString(CultureInfo.InvariantCulture));
    }
    private void PrepareThrow(CCSPlayerController p,string number)
    {
        if (!Check(p,out var pawn)) return;
        ClosePanel(p);
        if (!Preview(p,number)) return;
        ClearFlashVerification(p);
        var s=Get(p); s.Arm=null; s.Snapshot=null; s.PreparationIssue=null;
        ClearEffectPreview(p.Slot);s.LastEffectCenter=null;
        BindPreparation(p,s);
        GiveSelectedGrenade(p,s.Kind);
        EquipSmoke(p);
        BeginFlightCheck(p,s,s.Result!.Matches[s.Selected-1],false);
        s.Pending=new(s.Selected,s.Revision,NativeWorld.FromNative(pawn.AbsOrigin!),Server.CurrentTime);
        s.CueStage=ReleaseStage.None; s.CueUntil=0;
        HudNotice(s,"正在确认脚位、视角和眼高稳定；先别移动。蹲投请按Ctrl蹲稳。",1);
    }
    private void HudCommand(CCSPlayerController? p,CommandInfo c)
    {
        if (!Check(p,out _)) return;
        string mode=c.ArgCount>1 ? c.ArgByIndex(1).ToLowerInvariant() : "on";
        if (mode is not ("on" or "off")) {Say(p,"!nl_hud [on|off]"); return;}
        var s=Get(p!); s.HudEnabled=mode=="on"; s.NextHudAt=0; s.LastHudText=null;
        if (!s.HudEnabled && s.HudVisible) {SendPanelText(p!,""); s.HudVisible=false;}
    }
    private static void HudNotice(Session s,string text,float seconds)
    {s.HudNotice=text; s.HudNoticeUntil=Server.CurrentTime+seconds; s.NextHudAt=0;s.FeedbackUrgent=true;}
    private void RenderNotice(CCSPlayerController player,Session s,float now)
    {
        if(RenderInputCheckNotice(player)) return;
        bool menuOpen=HasPanel(player);
        string? timing=s.Arm is not null && s.CueStage!=ReleaseStage.None && now<s.TimingNoticeUntil && !menuOpen ? s.TimingNotice : null;
        string? blocker=menuOpen ? null : s.PreparationIssue ?? (s.Pending is not null ? "正在确认站位和视角；准备完成前请勿投掷。" : null);
        if(!menuOpen && s.Placement is { } draft)
            blocker=draft.ReturnIssue ?? (!draft.Point.Valid ? draft.Point.Reason : null);
        if(!s.FeedbackUrgent && timing is null && blocker is null && now<s.NextFeedbackAt) return;
        s.FeedbackUrgent=false;s.NextFeedbackAt=now+.5f;
        string? notice=s.HudNotice is { } message && now<s.HudNoticeUntil ? message : null;
        string? context=MenuFeedback(player) ?? EffectPreviewCaption(player,s);
        var feedback=PanelFeedback.Compose(timing,blocker,notice,context,WorkflowFeedback(player,s,now));
        SendNoticeText(player,feedback);
    }
    private string? WorkflowFeedback(CCSPlayerController player,Session s,float now)
    {
        if(DummyBatchHud(player) is { } batch) return batch;
        if(PersonalLibraryFeedback(player,now) is { } personal) return personal;
        var workflow=ResolveWorkflow(player,s);
        if(workflow.Step==WorkflowStep.Placement) return EffectPreviewCaption(player,s) ?? "当前位置可用；确认后返回原站位。";
        if(workflow.Step==WorkflowStep.Loading && dummyLayoutLoad is { } loading)
            return $"组合{loading.LayoutSlot} · 已就位{loading.Next}/{loading.Members.Length}个假人";
        if(workflow.Step==WorkflowStep.Region) return "区域草稿尚未应用；原目标保留。";
        if(workflow.Step==WorkflowStep.Preparing) return "准备中；等站位、视角和道具确认后再投掷。";
        if(workflow.Step==WorkflowStep.PreparationBlocked) return s.PreparationIssue;
        if(workflow.Step==WorkflowStep.Searching && !workflow.Flash && job is not null)
            return $"已有{s.Result?.Matches.Count ?? 0}种 · 继续寻找中";
        if(workflow.Step==WorkflowStep.Searching && workflow.Flash && flashJob is not null)
            return $"已有{s.Result?.Matches.Count ?? 0}种 · 继续寻闪中";
        if(s.FlashMode && CurrentFlashVerification(player) is {Trial.Complete:true}) return FlashVerificationSummary(player);
        if(s.FlashMode && CurrentFlashVerification(player) is {Trial.ReleasedAt:not null,Trial.Complete:false})
            return "本次实投观测中 · 等待全体假人结果";
        if(s.Arm is {ObservationOnly:true} recording && now<recording.Expires)
            return $"自由实投记录中 · 剩余{recording.Expires-now:F0}秒";
        if(s.Result?.Matches.ElementAtOrDefault(s.Selected-1) is { } candidate && player.PlayerPawn.Value is {IsValid:true} pawn)
        {
            var aim=candidate.Aim;
            string ready=s.Arm is {ObservationOnly:false} arm && now<arm.Expires ? "已就绪" : "待就位";
            string evidence=SearchPresentation.Label(candidate);
            if(s.FlashMode) evidence=s.FlashPlan?.Trials?.Summary(s.Selected) ?? "预测候选 · 待实投";
            if(s.Kind==GrenadeKind.Smoke && s.SmokeReview is { } review && review.Revision==s.Revision &&
                review.Record.Arm.Revision==s.Revision && review.Record.Physics.Kind==GrenadeKind.Smoke &&
                !review.Record.Arm.ObservationOnly && !review.Record.Arm.EffectObjective &&
                ReferenceEquals(review.Record.Arm.Candidate,candidate) && s.Start is { } start && review.Record.Arm.Start==start &&
                s.Target is { } target && review.Record.Arm.Target is { } recordedTarget && recordedTarget.SameDefinition(target))
            {
                var record=review.Record;
                evidence=record.Success ? "匹配实投 · 落点有效 · 封烟未确认" :
                    record.GuidedLandingSuccess ? "释放区实投 · 落点有效 · 封烟未确认" :
                    $"{(record.Matched ? "匹配实投" : record.GuidedMatched ? "释放区实投" : "实投动作未匹配")} · {(record.TargetHit ? "落点有效" : "落点无效")} · 封烟未确认";
                // A player's observation stays scoped to that view. It never
                // turns a valid landing into verified smoke coverage.
                evidence+=record.Feedback?.Verdict switch
                {
                    "sealed"=>" · 人工反馈：本视角封满",
                    "leaking"=>" · 人工反馈：本视角漏缝",
                    _=>""
                };
            }
            string status=$"{ready} · {evidence}";
            if(job?.Slot==player.Slot || flashJob?.Slot==player.Slot) status+=$" · 已有{s.Result.Matches.Count}种，继续寻找中";
            if(s.Arm is not null && aim.NeedsRunUp)
            {
                var velocity=NativeWorld.FromNative(pawn.AbsVelocity);
                status+=$" · 速度{new Vector2(velocity.X,velocity.Y).Length():F0}/{aim.ForwardSpeed:F0}";
            }
            return status;
        }
        if(s.Result is {Matches.Count:0}) return "本轮未找到可用投法，不代表理论无解。";
        if(s.FlashMode) return "固定当前站位；至少设置一个要闪对象，候选须手投验证。";
        return s.Target is null ? "目标未选定。" : s.Start is null ? "目标已保留，尚未记录投掷站位。" : "目标与投掷站位已记录。";
    }
    private static string ThrowOperation(Aim aim)=>ThrowInstructions.Operation(aim);
    private void ClearHuds()
    {
        
        panels.Clear();
        foreach(var (slot,s) in sessions)
        {
            var p=Utilities.GetPlayerFromSlot(slot);
            if(p?.IsValid==true && p.SteamID==s.SteamId && s.HudVisible) SendPanelText(p,"");
            s.HudVisible=false;
        }
    }
    private void RenderHuds()
    {
        float now=Server.CurrentTime;
        foreach(var (slot,s) in sessions)
        {
            if(now<s.NextHudAt) continue;
            s.NextHudAt=now+.15f;
            var p=Utilities.GetPlayerFromSlot(slot);
            if(p?.IsValid!=true || p.SteamID!=s.SteamId) continue;
            if(p.PlayerPawn.Value is not {IsValid:true,LifeState:0} pawn)
            {ClosePanel(p);ClearPanelDisplay(p.Slot);s.HudVisible=false;continue;}
            RenderNotice(p,s,now);
            SendDummyPanelText(p,DummyRosterText(p));
            if(RenderPanel(p,s,now)) continue;
            if(DummyBatchHud(p) is { } batchHud)
            {SendBottomHud(p,s,batchHud,now,keys:CompanionKeys(p,s,false,false),nativeKeys:"使用 记录/放置 · 换弹 取消/结束\n长按静步+换弹 菜单");continue;}
            if(!s.HudEnabled && s.Placement is null) continue;
            if(RenderPersonalLibraryHud(p,s,now)) continue;
            var workflow=ResolveWorkflow(p,s);
            if(RenderFlashHud(p,s,now,workflow)) continue;
            string text;
            if(workflow.Step==WorkflowStep.Placement)
                text=$"{s.Kind.Label()} · 爆点定位\n移动道具，确认爆点后返回\n切换视角观察假人预测";
            else if(workflow.Step==WorkflowStep.Loading)
                text="假人组合 · 加载中\n打开组合菜单查看进度或停止加载";
            else if(workflow.Step==WorkflowStep.Region)
                text="落点区域编辑\n打开编辑菜单，选择应用或取消";
            else if(pawn.MoveType==MoveType_t.MOVETYPE_NOCLIP)
                text=$"{s.Kind.Label()} · 观察路线\n移动观察；退出飞行落地后继续准备";
            else if(workflow.Step==WorkflowStep.Preparing)
                text=$"{s.Kind.Label()} · 准备投掷\n停稳等待；蹲投按Ctrl蹲稳\n上方提示就绪后再蓄力手投";
            else if(workflow.Step==WorkflowStep.PreparationBlocked)
                text=$"{s.Kind.Label()} · 重新准备\n按上方原因调整后，再次就位";
            else if(workflow.Step==WorkflowStep.Recording)
                text=$"{s.Kind.Label()} · 自由手投\n按你的方法投掷，记录真实动作与落点";
            else if(workflow.Primary==WorkflowAction.Prepare && s.Result?.Matches is {Count:>0} matches && s.Selected>=1 && s.Selected<=matches.Count && s.Start is not null)
            {
                var candidate=matches[s.Selected-1];var aim=candidate.Aim;
                text=$"{s.Kind.Label()} · 投法{s.Selected}/{matches.Count}\n"+
                    PanelText.Line(ThrowInstructions.Compact(aim),36)+"\n"+ThrowOperation(aim);
            }
            else
            {
                string step=workflow.Step==WorkflowStep.Searching ? "等待结果，或打开菜单查看/采用投法" :
                    s.Result is {Matches.Count:0} ? "继续搜索，或调整目标后重试" :
                    s.Target is null ? s.Kind.Timed() ? "变为道具，移动到希望的爆点后确认" : "准星瞄准地面，确定目标" :
                    s.Start is null ? "到投掷位置站稳，记录站位并搜索" : "沿用站位搜索，或换站位保留目标";
                text=$"{s.Kind.Label()} · {step}";
            }
            SendBottomHud(p,s,text,now,keys:WorkflowKeys.Text(workflow));
        }
    }
}
