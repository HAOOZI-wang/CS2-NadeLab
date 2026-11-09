using System.Globalization;
using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using NadeLab.Core;
using CsTrace=CounterStrikeSharp.API.Modules.Utils.Trace;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private static string RouteLabel(Candidate c) => c.Trajectory.WallBounces==0 ? "直投（不撞墙）" : $"撞墙/顶面 {c.Trajectory.WallBounces} 次";
    private WorkflowResolution ResolveWorkflow(CCSPlayerController player,Session s)
    {
        bool flash=s.FlashMode;
        bool searching=flash ? flashJob?.Slot==player.Slot : job?.Slot==player.Slot;
        bool reviewed=flash ? flashJob?.Slot==player.Slot && flashJob.Search.ReviewedCandidateCount>0 :
            job?.Slot==player.Slot && job.Search.ReviewedCandidateCount>0;
        float now=Server.CurrentTime;
        return WorkflowModel.Resolve(new(
            Placement:s.Placement is not null,
            Loading:dummyLayoutLoad is { } loading && loading.Owner==player.EntityHandle.Raw && loading.Steam==player.SteamID,
            RegionDraft:s.RegionDraft is not null,Flash:flash,Searching:searching,ReviewedCandidate:reviewed,
            HasTarget:s.Target is not null,HasOrigin:s.Start is not null,HasCandidate:s.Result?.Matches.Count>0,
            Continuation:s.Continuation is not null,Preparing:s.Pending is not null,PreparationBlocked:s.PreparationIssue is not null,
            Ready:s.Arm is {ObservationOnly:false} ready && now<ready.Expires,
            Recording:s.Arm is {ObservationOnly:true} recording && now<recording.Expires,
            Flying:player.PlayerPawn.Value?.MoveType==MoveType_t.MOVETYPE_NOCLIP));
    }
    private bool MenuIsCurrent([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] CCSPlayerController? player,Session opened,int revision,int epoch)
    {
        if(player is null || !Check(player,out _)) return false;
        if(grenadeEpoch==epoch && ReferenceEquals(Get(player),opened) && opened.Revision==revision) return true;
        Say(player,"菜单对应的状态已变化，请重新打开菜单。");return false;
    }
    private void PrimaryAction(CCSPlayerController? p)
    {
        if(p?.IsValid==true && sessions.TryGetValue(p.Slot,out var placing) && placing.Placement is not null) {FinishPlacement(p,true);return;}
        if (!Check(p,out _)) return;
        ClosePanel(p!);
        var s=Get(p!);
        // Resolve at execution time. Queued actions and menus retain their
        // existing identity/revision/epoch checks; no snapshot is an authority
        // to execute an action against a later player/session.
        var workflow=ResolveWorkflow(p!,s);
        switch(workflow.Primary)
        {
            case WorkflowAction.OpenLoading: OpenDummyLayouts(p);break;
            case WorkflowAction.EditRegion: OpenRegionEditor(p);break;
            case WorkflowAction.SelectTarget: TargetByKind(p!);break;
            case WorkflowAction.CaptureOrigin:
                StartCommand(p,null);
                if(s.Start is not null) Solve(p,"quick");
                break;
            case WorkflowAction.ReviewSearch when workflow.Flash: FlashPrimary(p!);break;
            case WorkflowAction.ReviewSearch:
                if(job is not { } current || current.Slot!=p!.Slot) return;
                if(current.Search.ReviewedCandidateCount>0)
                {
                    PublishLiveSearch(current,s,p);
                    if(s.Result?.Matches.Count>0) PrepareThrow(p!,Math.Max(1,s.Selected).ToString(CultureInfo.InvariantCulture));
                    else Say(p,"正在复核路线，完成后即可采用。");
                }
                else Say(p,$"正在{PhaseLabel(current.Search.Phase)}，已计算 {current.Search.Evaluated} 个候选；打开菜单可取消。");
                break;
            case WorkflowAction.Prepare: PrepareThrow(p!,Math.Max(1,s.Selected).ToString(CultureInfo.InvariantCulture));break;
            case WorkflowAction.FlashSearch: StartFlashSearch(p);break;
            case WorkflowAction.ContinueSearch: ContinueSearch(p);break;
            case WorkflowAction.Search: Solve(p,"quick");break;
        }
    }
    private static string PhaseLabel(string phase) => phase switch
    {
        "route-jump-bank"=>"探索原地跳投连续反弹","route-geometry"=>"分析周围通路与墙面","route-direct"=>"探索直飞路线","route-high"=>"探索高抛路线","route-opening"=>"探索标记面与入口方向","route-bank"=>"探索墙面反弹",
        "continue"=>"继续探索更多路线","high-arcs"=>"搜索高抛越障路线","prior-routes"=>"复查附近已有投法","direct"=>"比较各类投法","robust-refine"=>"细化更稳的角度","direct-refine"=>"细化射程","heading-refine"=>"细化目标方向","coarse"=>"找反弹路线",
        "refine"=>"细化路线","global"=>"补扫全方向","global-refine"=>"细化补扫结果",
        "near-refine"=>"细化接近落点的投法","resume-search"=>"候选不足，继续找路线","area-refine"=>"检查封烟区域内的稳定投法","compare-moving"=>"同一起点比较W投","precision"=>"提高落点精度","robustness"=>"复核候选",_=>phase
    };
    private delegate void AddWorkflowOption(string label,Action<CCSPlayerController> action,bool disabled=false,string? hint=null,string? key=null,Func<bool>? unavailable=null);
    private PanelMenu WorkflowPanel(CCSPlayerController p,string title,out AddWorkflowOption add,Func<bool>? stillCurrent=null,[System.Runtime.CompilerServices.CallerMemberName]string identity="")
    {
        var s=Get(p);int revision=s.Revision,epoch=grenadeEpoch;
        var menu=NewPanel(title,identity);menu.ExitButton=true;
        add=(label,action,disabled,hint,key,unavailable)=>menu.AddMenuOption(label,(who,_)=>
        {
            ClosePanel(who);QueueAction(who,current=>
            {
                if(!MenuIsCurrent(current,s,revision,epoch)) return;
                if(stillCurrent is not null && !stillCurrent()) {Say(current,"面板状态已变化，请重新打开菜单。");return;}
                action(current!);
            });
        },disabled,hint,key,unavailable);
        return menu;
    }
    private void OpenMenu(CCSPlayerController? p)
    {
        if(!Check(p,out _,allowPlacement:true)) return;
        var player=p!;var s=Get(player);var workflow=ResolveWorkflow(player,s);
        // The root never changes shape. Every callback resolves the current
        // task again; only the selection key survives a close/reopen.
        var menu=NewPanel("NadeLab · "+s.Kind.Label());menu.IsRoot=true;menu.AllowPlacement=true;
        void Add(string text,string key,Action<CCSPlayerController> action,bool disabled=false)=>
            menu.AddMenuOption(text,(who,option)=>QueueAction(who,current=>
            {
                if(current is null || !Check(current,out _,allowPlacement:true)) return;
                action(current);
            }),disabled,disabled ? "请先完成或取消当前定位，再使用此项。" : null,key);
        string task=HasDummyBatch(player) ? "继续连续放假人" : workflow.Step switch
        {
            WorkflowStep.Placement=>"继续爆点定位",WorkflowStep.Loading=>"查看组合加载",
            WorkflowStep.Region=>"继续区域编辑",_ when workflow.Flash=>"寻闪任务",
            WorkflowStep.Searching=>"查看搜索进度",_ when s.Result?.Matches.Count>0=>"查看 / 准备投法",
            _ when s.Target is null=>"选择目标",_ when s.Start is null=>"记录站位并搜索",_=>"继续寻找投法"
        };
        menu.Feedback=()=>"当前任务保留；目标、假人与收藏从固定入口进入。";
        Add("当前任务："+task,"task",OpenCurrentTask);
        Add("目标与站位","target",OpenTargetTools,s.Placement is not null);
        Add("假人与寻闪","dummies",OpenDummyTools);
        Add("我的道具库","library",OpenPersonalLibrary,s.Placement is not null);
        Add("切换地图","map",OpenMapMenu,s.Placement is not null);
        Add("其他设置","settings",OpenSettingsMenu,s.Placement is not null);
        menu.Open(player);
    }
    private void OpenCurrentTask(CCSPlayerController player)
    {
        if(!Check(player,out _,allowPlacement:true)) return;
        var s=Get(player);var workflow=ResolveWorkflow(player,s);
        if(HasDummyBatch(player)) {BeginDummyBatch(player);return;}
        if(workflow.Step==WorkflowStep.Placement) {OpenPlacementMenu(player);return;}
        if(workflow.Step==WorkflowStep.Loading) {OpenDummyLayouts(player);return;}
        if(workflow.Step==WorkflowStep.Region) {OpenRegionEditor(player);return;}
        if(workflow.Flash) {OpenFlashMenu(player);return;}
        if(workflow.Step==WorkflowStep.Searching) {OpenSearchTask(player);return;}
        if(s.Result?.Matches.Count>0) {OpenResultsMenu(player);return;}
        PrimaryAction(player);
    }
    private void OpenSearchTask(CCSPlayerController player)
    {
        var s=Get(player);var openedJob=job?.Slot==player.Slot ? job : null;
        if(openedJob is null) {OpenCurrentTask(player);return;}
        var menu=WorkflowPanel(player,"当前搜索",out var Add);menu.Back=OpenMenu;
        menu.Feedback=()=>ReferenceEquals(job,openedJob)
            ? PanelText.Line($"已有{s.Result?.Matches.Count ?? 0}种 · 继续寻找中",40)
            : $"搜索已结束 · 已有{s.Result?.Matches.Count ?? 0}种";
        Add("采用路线",PrimaryAction,hint:"采用已复核路线；搜索继续。暂无路线时请稍候。",unavailable:()=>s.Result?.Matches.Count is not >0);
        Add("路线列表",who=>OpenCandidateList(who),unavailable:()=>s.Result?.Matches.Count is not >0);
        Add("停止搜索",who=>
        {
            if(!ReferenceEquals(job,openedJob) || openedJob.Slot!=who.Slot || openedJob.SteamId!=who.SteamID || openedJob.Revision!=s.Revision)
            {Say(who,"该搜索已结束或不属于你；旧菜单不会取消新搜索。");return;}
            StopPlayerSearch(who);
        },unavailable:()=>!ReferenceEquals(job,openedJob));
        menu.Open(player);
    }
    private void OpenDummyTools(CCSPlayerController player)
    {
        if(!Check(player,out _,allowPlacement:true)) return;
        if(Get(player).Placement is not null) {OpenEffectTarget(player);return;}
        var menu=WorkflowPanel(player,"假人与寻闪",out var Add);menu.Back=OpenMenu;
        Add(HasDummyBatch(player) ? "继续连续放假人" : "连续放假人",BeginDummyBatch,key:"batch");
        Add("查看 / 复位 / 删除假人",OpenEffectTarget);
        Add("按假人找闪：要闪 / 不闪",OpenFlashMenu);
        Add("假人组合：保存 / 加载",OpenDummyLayouts);
        menu.Open(player);
    }
    private void OpenSettingsMenu(CCSPlayerController player)
    {
        if(!Check(player,out _)) return;
        var menu=WorkflowPanel(player,"其他设置",out var Add);menu.Back=OpenMenu;
        Add("开启 / 退出飞行",ToggleTrainingFlight);
        Add("玩家无敌：已开启",_=>{},true,"训练玩家免伤；测试假人正常掉血、受闪。",key:"protection");
        Add("换一种道具",OpenGrenadeMenu);
        Add("显示设置：范围 / 轨迹 / 提示",OpenDisplayMenu);
        Add("研究工具 / 实投证据",OpenResearchMenu);
        Add("检查菜单按键",OpenInputCheck);
        Add("新手指引 / 按键说明",ShowControls);
        menu.Open(player);
    }
    private void OpenPlacementMenu(CCSPlayerController player)
    {
        if(!Check(player,out _,allowPlacement:true)) return;
        var session=Get(player);var draft=session.Placement;if(draft is null) {OpenMenu(player);return;}
        PlacementMenu(player,session,draft).Open(player);
    }
    private PanelMenu PlacementMenu(CCSPlayerController player,Session session,PlacementDraft draft)
    {
        var menu=NewPanel(session.Kind.Label()+"定位");menu.Back=OpenMenu;menu.AllowPlacement=true;
        menu.Feedback=()=>"道具中心就是爆点；假人栏显示预估效果。";
        void Add(string text,Action<CCSPlayerController> action,string? hint=null)=>menu.AddMenuOption(text,(who,option)=>QueueAction(who,current=>
        {
            if(!Check(current,out _,allowPlacement:true) || !ReferenceEquals(Get(current!),session) || !ReferenceEquals(session.Placement,draft)) return;
            action(current!);
        }),hint:hint);
        Add("确认爆点并返回",who=>FinishPlacement(who,true));
        Add("选择 / 复位 / 删除假人",who=>OpenEffectTarget(who));
        Add(draft.Area ? "关闭效果预览" : "开启效果预览",who=>{ToggleEffectPreview(who);OpenPlacementMenu(who);},"预估效果不造成真实伤害或致盲。");
        Add("切换第一 / 第三人称",who=>{ToggleGrenadeBodyCamera(who);OpenPlacementMenu(who);});
        Add("检查菜单按键",OpenInputCheck);
        Add("取消定位并返回",who=>FinishPlacement(who,false),"放弃本次定位，返回进入定位前的位置。");
        return menu;
    }
    private void RestartWorkflow(CCSPlayerController p)
    {
        if(!Check(p,out _)) return;
        TargetByKind(p);
    }
    private void Retarget(CCSPlayerController? p)
    {
        if(!Check(p,out _)) return;
        ClearDummyBatch(p!.Slot);
        ClosePanel(p!);TargetByKind(p!);
    }
    private void Reposition(CCSPlayerController? p)
    {
        if(!Check(p,out _)) return;
        var s=Get(p!);
        var workflow=ResolveWorkflow(p!,s);
        if(workflow.Step==WorkflowStep.Loading) {OpenDummyLayouts(p);return;}
        if(workflow.Step==WorkflowStep.Region) {OpenRegionEditor(p);return;}
        if(workflow.Flash) {StartFlashSearch(p);return;}
        if(s.Target is null) {Say(p,"请先选择目标区域。"); return;}
        if(job is not null && job.Slot!=p!.Slot) {Say(p,"另一位玩家正在搜索，请稍后重试。"); return;}
        ClosePanel(p!);
        // StartCommand preserves the target; failed capture must not reuse an
        // old origin and silently search from the previous standing position.
        Invalidate(p!.Slot,s); s.Start=null;
        StartCommand(p,null);
        if(s.Start is not null)
        {
            Say(p,"已保留目标，使用你当前的新站位重新搜索。");
            Solve(p,"quick");
        }
    }
    private void OpenResultsMenu(CCSPlayerController p)
    {
        if (!Check(p,out _)) return;
        var s=Get(p);
        if (s.Result?.Matches is not {Count:>0} matches) { OpenMenu(p); return; }
        s.Selected=Math.Clamp(s.Selected,1,matches.Count);
        int selection=s.Selected;
        var result=s.Result;
        var candidate=matches[s.Selected-1];
        var menu=WorkflowPanel(p,$"投法 {selection}/{matches.Count}",out var Add,
            ()=>ReferenceEquals(s.Result?.Matches.ElementAtOrDefault(selection-1),candidate) && s.Selected==selection);
        menu.Back=OpenMenu;
        menu.Feedback=()=>PanelText.Line(SearchPresentation.Label(candidate)+(job?.Slot==p.Slot ? " · 继续寻找中" : ""),40);
        Add("采用路线",who=>PrepareThrow(who,selection.ToString(CultureInfo.InvariantCulture)),hint:"自动回位、对准并切出道具；搜索继续。由你蓄力手投。");
        Add("路线列表",who=>OpenCandidateList(who),hint:"每页5种；翻页保留当前投法。");
        Add("下一投法",who=>NextCandidate(who,null!),hint:"飞行时只切换观察；落地后可准备投掷。");
        if(job?.Slot==p.Slot) Add("停止搜索",StopPlayerSearch,hint:"保留已找到的全部路线。");
        else Add("继续寻找更多路线",ContinueSearch,s.Target is null,
            hint:"保留当前路线，继续探索其他角度与动作。");
        Add("本路线动作 / 实投证据",OpenEvidenceMenu);
        Add("目标与站位",OpenTargetTools);
        Add("返回训练菜单",OpenMenu);
        menu.Open(p);
    }
    private void OpenTargetTools(CCSPlayerController p)
    {
        if(!Check(p,out _)) return;
        var s=Get(p);var menu=WorkflowPanel(p,"目标与站位",out var Add);menu.Back=OpenMenu;
        Add("从当前位置重新搜索",Reposition,s.Target is null,hint:"先落地停稳；保留目标，改用当前站位。");
        Add("重选目标",TargetByKind,hint:s.Kind.Timed() ? "变为道具，移动到希望的空中爆点再确认。" : "用准星指向希望的地面落点。");
        Add("采用最近实投爆点",who=>AdoptRecentImpact(who),hint:"使用自己的当前道具真实爆点，保留已记录站位。");
        Add("调整允许落点范围",who=>OpenRegionEditor(who),s.Target is null);
        Add("T 出生位 · 按编号选择",who=>OpenSpawnPoints(who,false),hint:"仅列竞技出生位；地面绿框与编号常驻。选择后精确就位，保留目标。");
        Add("CT 出生位 · 按编号选择",who=>OpenSpawnPoints(who,true),hint:"仅列竞技出生位；地面绿框与编号常驻。选择后精确就位，保留目标。");
        if(s.Kind==GrenadeKind.Smoke && Server.MapName=="de_mirage")
        {
            Add("VIP窗口烟预设区域",SelectVipPreset);
        }
        Add("返回主面板",OpenMenu);
        menu.Open(p);
    }
    private void SelectVipPreset(CCSPlayerController p)
    {
        if(!Check(p,out var pawn) || Get(p).Kind!=GrenadeKind.Smoke) return;
        if(Server.MapName!="de_mirage") {Say(p,"VIP窗口预设仅适用于Mirage，请用准星选择本图目标。");return;}
        var target=SmokeTargets.MirageVip;
        var floor=new NativeWorld(pawn).Sweep(target.Position+Vector3.UnitZ*48,target.Position-Vector3.UnitZ*96,2);
        if(!floor.Hit || floor.StartSolid || floor.Normal.Z<.7f) {Say(p,"VIP预设中心没有有效支撑，原目标保留。");return;}
        var s=Get(p);Invalidate(p.Slot,s);s.Target=target with {Position=floor.Position-Vector3.UnitZ*2};s.TargetPreset=s.Target;
        Say(p,"已选择VIP窗口烟预设区域，封烟效果仍需实投确认。");OpenTargetTools(p);
    }
    private void OpenResearchMenu(CCSPlayerController p)
    {
        if(!Check(p,out _)) return;
        var s=Get(p);var menu=WorkflowPanel(p,"研究工具",out var Add);menu.Back=OpenSettingsMenu;
        Add(s.Continuation is null ? "搜索投法" : "继续寻找更多路线",ContinueSearch,s.Target is null);
        Add("本次搜索原因 / 建议",who=>ShowDetails(who,"搜索分析",()=>ExplainSearch(who)),s.Result is null);
        Add("记录自由实投",RecordManualThrow,s.Target is null,hint:"不必先找到投法；记录你的手投作对比。");
        Add("允许附近换站位搜索",who=>
        {
            var current=Get(who);if(current.Start is null) StartCommand(who,null);
            if(current.Start is not null) Solve(who,"nearby");
        },s.Target is null,hint:"允许工具偏离原站位，尝试附近可站位置。");
        Add("路线证据 / 实投反馈与保存",OpenEvidenceMenu);
        Add("显示设置：范围 / 轨迹 / 提示",OpenDisplayMenu);
        Add("检查菜单按键",OpenInputCheck);
        Add("返回主面板",OpenMenu);menu.Open(p);
    }
    private void OpenEvidenceMenu(CCSPlayerController p)
    {
        if(!Check(p,out _)) return;
        var s=Get(p);var result=s.Result;int selection=s.Selected;
        var candidate=result?.Matches.ElementAtOrDefault(selection-1);
        var menu=WorkflowPanel(p,"证据与保存",out var Add,()=>ReferenceEquals(s.Result?.Matches.ElementAtOrDefault(selection-1),candidate) && s.Selected==selection);
        menu.Back=OpenResearchMenu;
        Add("本路线动作 / 角度 / 执行建议",who=>
        {
            if(candidate is null || s.Start is null || s.Target is null) return;
            ShowDetails(who,"投法执行建议",()=>{Say(who,SceneAdvisor.Explain(s.Start.Value,s.Target.Value,candidate,result!.Matches));Say(who,ExecutionHint(candidate.Aim,s.Start.Value));});
        },candidate is null);
        Add("本路线实投证据",who=>{if(candidate is not null) ShowDetails(who,"路线证据",()=>Say(who,RouteEvidence(Get(who),candidate)));},candidate is null);
        if(s.Kind==GrenadeKind.Smoke) Add("本次封烟反馈 / 保存",OpenSmokeReview,s.SmokeReview is null);
        else
        {
            Add("效果假人 / 最近效果",OpenEffectTarget);
            Add("保存位置验证",who=>{SaveLineup(who,$"{Get(who).Kind}-position");OpenEvidenceMenu(who);},s.LastVerified is null,hint:"只保存位置验证；不代表致盲、伤害等效果达标。");
        }
        Add("返回研究工具",OpenResearchMenu);menu.Open(p);
    }
    private void OpenDisplayMenu(CCSPlayerController p)
    {
        if(!Check(p,out _)) return;
        var s=Get(p);var menu=WorkflowPanel(p,"显示设置",out var Add);menu.Back=OpenSettingsMenu;
        Add(s.Kind.Timed() ? "查看定位预览说明" : !EffectReference.HasAreaPreview(s.Kind) ? "效果参考" : s.EffectEnabled ? "关闭效果参考" : "开启效果参考",
            who=>{ToggleEffectPreview(who);OpenDisplayMenu(who);},!s.Kind.Timed() && !EffectReference.HasAreaPreview(s.Kind),
            hint:s.Kind.Timed() ? "闪光、手雷的效果预测在道具定位时使用。" : !EffectReference.HasAreaPreview(s.Kind) ? "此道具没有效果范围显示。" : "范围仅供观察参考，实际效果需要实投验证。");
        Add(s.HudEnabled ? "关闭投法提示" : "开启投法提示",who=>
        {s.HudEnabled=!s.HudEnabled;s.NextHudAt=0;s.LastHudText=null;if(!s.HudEnabled) {ClearPanelMain(who.Slot);s.HudVisible=false;}OpenDisplayMenu(who);});
        Add(s.GuideEnabled ? "关闭地面松键标记" : "开启地面松键标记",who=>{s.GuideEnabled=!s.GuideEnabled;RefreshSelectedGuides(who);OpenDisplayMenu(who);});
        Add(s.PreviewEnabled ? "关闭轨迹预览" : "开启轨迹预览",who=>{s.PreviewEnabled=!s.PreviewEnabled;RefreshSelectedGuides(who);OpenDisplayMenu(who);});
        Add(s.FlightPreviewEnabled ? "飞行小窗：开（A组原生）" : "飞行小窗：关",who=>{
            s.FlightPreviewEnabled=!s.FlightPreviewEnabled;
            SyncFlightPreview(who);
            OpenDisplayMenu(who);
        },hint:"A组：原生持续开启，不等待旧弹结束；旧弹仍可能打断画面，不保证跟踪最新一颗。");
        Add("返回其他设置",OpenSettingsMenu);menu.Open(p);
    }
    private bool ReturnToStart(CCSPlayerController p,CCSPlayerPawn pawn,Vector3 feet)
    {
        if (pawn.MoveType!=MoveType_t.MOVETYPE_WALK) { Say(p,"请先关闭 noclip，站立后再准备手投。"); return false; }
        if (Vector3.Distance(NativeWorld.FromNative(pawn.AbsOrigin!),feet)<=.25f && Standing(pawn))
        {
            pawn.Teleport(angles:new Vector3(0,pawn.EyeAngles.Y,0),velocity:Vector3.Zero);
            return true;
        }
        if(!TryStandingPosition(pawn,feet,out var position))
        { Say(p,"起点无法容纳站立玩家或脚下没有支撑，未执行回位；请重新记录安全站位。"); return false; }
        pawn.Teleport(position:position,angles:new Vector3(0,pawn.EyeAngles.Y,0),velocity:Vector3.Zero);
        return true;
    }
    private static bool TryStandingPosition(CCSPlayerPawn? pawn,Vector3 feet,out Vector3 position,bool fullHeight=false)
    {
        position=feet;
        if(pawn is not null && pawn.Collision is null) return false;
        var mins=fullHeight ? new CounterStrikeSharp.API.Modules.Utils.Vector(-16,-16,0) : pawn?.Collision?.Mins ?? new CounterStrikeSharp.API.Modules.Utils.Vector(-16,-16,0);
        var maxs=fullHeight ? new CounterStrikeSharp.API.Modules.Utils.Vector(16,16,72) : pawn?.Collision?.Maxs ?? new CounterStrikeSharp.API.Modules.Utils.Vector(16,16,72);
        var options=new TraceOptions {InteractsAs=Contents.Player,InteractsWith=Masks.PlayerSolid|Contents.PhysicsProp};
        bool CanStand(Vector3 proposed)
        {
            var pos=NativeWorld.ToNative(proposed);
            var occupancy=CsTrace.TraceHullShape(pos,pos,mins,maxs,pawn,options);
            if (occupancy.IsAllSolid || occupancy.DidHit()) return false;
            var support=CsTrace.TraceHullShape(pos,NativeWorld.ToNative(proposed-new Vector3(0,0,4)),mins,maxs,pawn,options);
            return support.DidHit() && !support.IsAllSolid && support.Normal.Z>=.7f;
        }
        if(SafePlacement.TryRaise(feet,CanStand,out position)) return true;
        var floor=CsTrace.TraceHullShape(NativeWorld.ToNative(feet+new Vector3(0,0,18)),NativeWorld.ToNative(feet-new Vector3(0,0,4)),mins,maxs,pawn,options);
        return SafePlacement.TryStepSupport(feet,new(floor.Fraction,NativeWorld.FromNative(floor.EndPos),NativeWorld.FromNative(floor.Normal),floor.IsAllSolid),CanStand,out position);
    }
    private void StandingDiagnostic(CCSPlayerController? p,CounterStrikeSharp.API.Modules.Commands.CommandInfo c)
    {
        if (p is not null || !Practice) return;
        bool testBots=c.ArgCount>1 && c.ArgByIndex(1)=="bots";
        if(testBots && (!BenchmarkIdle || Utilities.GetPlayers().Any(player=>!player.IsBot)))
        {Say(null,"STANDCHECK bots refused: empty idle practice server required.");return;}
        foreach (var player in Utilities.GetPlayers().Where(player=>testBots ? player.IsBot : !player.IsBot))
        {
            if (player.PlayerPawn.Value is not {IsValid:true} pawn || pawn.Collision is not { } collision) continue;
            var feet=NativeWorld.FromNative(pawn.AbsOrigin!);
            var lifted=NativeWorld.ToNative(feet+new Vector3(0,0,1));
            var options=new TraceOptions {InteractsAs=Contents.Player,InteractsWith=Masks.PlayerSolid|Contents.PhysicsProp};
            var trace=CsTrace.TraceHullShape(lifted,lifted,collision.Mins,collision.Maxs,pawn,options);
            Say(null,$"STANDCHECK: feet={feet}; body pitch={pawn.AbsRotation!.X:F1}; view pitch={pawn.EyeAngles.X:F1}; eye height={pawn.ViewOffset.Z:F1}; move={pawn.MoveType}; hull +1u solid={trace.IsAllSolid}, fraction={trace.Fraction:F3}.");
            if (testBots)
            {
                pawn.Teleport(angles:new Vector3(60,pawn.EyeAngles.Y,0),velocity:Vector3.Zero);
                bool returned=ReturnToStart(player,pawn,feet-new Vector3(0,0,.5f));
                Say(null,$"STANDCHECK bot return: {(returned && MathF.Abs(pawn.AbsRotation!.X)<.01f ? "PASS" : "FAIL")}; body pitch={pawn.AbsRotation!.X:F1}; feet change Z={pawn.AbsOrigin!.Z-feet.Z:F3}.");
            }
        }
    }
}

