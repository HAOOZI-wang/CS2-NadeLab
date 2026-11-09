using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private sealed class FlashPlanState
    {
        public required FlashObserver[] Observers;
        public required Dictionary<uint,uint> Pawns;
        public required int Epoch,Revision;
        public required uint Owner,OwnerPawn;
        public FlashSearchResult? Result;
        public FlashCandidateTrials? Trials;
    }
    private sealed class FlashSearchJob(int slot,ulong steam,FlashPlanState plan,NativeWorld world,FlashEffectSearch search)
    {
        public readonly int Slot=slot;
        public readonly ulong Steam=steam;
        public readonly FlashPlanState Plan=plan;
        public readonly NativeWorld World=world;
        public readonly FlashEffectSearch Search=search;
        public readonly long Started=Stopwatch.GetTimestamp();
        public double WorkMs,PeakMs;
        public int PublishedCount;
    }
    private FlashSearchJob? flashJob;
    private static string FlashRoleLabel(FlashRole role)=>role switch
    {FlashRole.MustFlash=>"要闪",FlashRole.Protect=>"不闪",_=>"不参与"};

    private void CancelFlashSearch() {flashJob?.Search.Dispose();flashJob=null;}

    // This is a distinct objective. No fabricated detonation Target is put in
    // Session or Arm; the shared Candidate list is only the preparation adapter.
    private void OpenFlashMenu(CCSPlayerController? player)
    {
        if(!Check(player,out _)) return;
        var p=player!;var s=Get(p);
        var workflow=ResolveWorkflow(p,s);
        if(workflow.Step==WorkflowStep.Region) {OpenRegionEditor(p);return;}
        if(workflow.Step==WorkflowStep.Loading)
        {OpenDummyLayouts(p);return;}
        if(s.Kind!=GrenadeKind.Flash) SelectGrenade(p,GrenadeKind.Flash);
        if(!s.FlashMode)
        {
            Invalidate(p.Slot,s);s.FlashMode=true;
            s.Target=null;s.TargetPreset=null;s.Start=null;s.RequestedStart=null;
        }
        var mine=OwnedDummies(p);
        bool hasRequired=mine.Any(d=>d.SearchRole==FlashRole.MustFlash);
        var currentJob=flashJob?.Slot==p.Slot ? flashJob : null;
        var menu=WorkflowPanel(p,"按假人找闪",out var Add);
        menu.Back=OpenMenu;
        menu.Feedback=()=>"固定当前站位，按假人要求找闪；候选仍需手投。";
        if(currentJob is not null)
        {
            menu.Feedback=()=>ReferenceEquals(flashJob,currentJob)
                ? $"已检查{currentJob.Search.Evaluated}种投法 · 已复核{currentJob.Search.ReviewedCandidateCount}种"
                : $"搜索已结束 · 已有{s.Result?.Matches.Count ?? 0}种";
            Add("采用路线",PrimaryAction,unavailable:()=>s.Result?.Matches.Count is not >0,hint:"采用已复核路线；暂无路线时请稍候。");
            Add("路线列表",who=>OpenCandidateList(who),unavailable:()=>s.Result?.Matches.Count is not >0);
            Add("停止搜索",who=>{if(ReferenceEquals(flashJob,currentJob)) StopPlayerSearch(who);},unavailable:()=>!ReferenceEquals(flashJob,currentJob));
        }
        else if(mine.Length==0)
        {
            menu.Feedback=()=>"先放置假人，或加载已保存的组合。";
            Add("放置假人",OpenEffectTarget);
            Add("加载假人组合",OpenDummyLayouts);
        }
        else if(!hasRequired)
        {
            menu.Feedback=()=>"至少将一个假人设为要闪，再到投掷位置搜索。";
            Add("设置假人要求",who=>OpenFlashRequirements(who));
        }
        else if(s.Result?.Matches is {Count:>0} matches)
        {
            menu.Feedback=()=>$"当前投法{Math.Max(1,s.Selected)}/{matches.Count} · "+
                (s.FlashPlan?.Trials?.Summary(s.Selected) ?? "预测候选，仍需手投验证");
            Add("准备当前投法",PrimaryAction,hint:"落地停稳后自动回位、对准并切出闪光弹。");
            Add("投法列表 · 按编号选择",who=>OpenCandidateList(who),hint:"每页5种，选择条目后准备手投。");
            Add("下一投法",who=>NextCandidate(who,null!),hint:"优先跳过本轮匹配实投已失败的投法；当前方案仍可重新准备。 ");
        }
        else Add("从当前位置找投法",StartFlashSearch,hint:"先落地停稳；按所有参与假人的位置与朝向搜索。");
        if(mine.Length>0)
        {
            if(hasRequired || currentJob is not null) Add("假人要求",who=>OpenFlashRequirements(who));
            Add("复位我的全部假人",ResetFlashDummies,currentJob is not null,hint:"等待观测结束；恢复原姿态和血量，保留要求。");
            Add("假人组合：保存 / 加载",OpenDummyLayouts);
            Add("放置 / 管理假人",OpenEffectTarget);
        }
        if(s.Result is not null || CurrentFlashVerification(p) is not null) Add("查看预测与本次实投",ShowFlashDetails);
        Add("更多：换站位 / 指定爆点",OpenFlashTools);
        Add("我的道具库：录制 / 收藏",OpenPersonalLibrary);
        menu.Open(p);
    }
    private void OpenFlashRequirements(CCSPlayerController? player,int? selectedLabel=null)
    {
        if(!Check(player,out _)) return;
        var p=player!;
        if(dummyLayoutLoad is { } loading && loading.Owner==p.EntityHandle.Raw && loading.Steam==p.SteamID)
        {OpenDummyLayouts(p);return;}
        if(!Get(p).FlashMode) {OpenFlashMenu(p);return;}
        var mine=OwnedDummies(p);
        selectedLabel ??= SelectedDummy(p)?.LabelIndex;
        var menu=WorkflowPanel(p,"假人要求",out var Add);
        menu.Back=OpenFlashMenu;
        menu.Feedback=()=>"至少一个要闪；修改要求后需重新搜索。";
        foreach(var d in mine)
        {
            uint handle=d.Handle;
            Add($"{DummyLabel(d)}：{FlashRoleLabel(d.SearchRole)}",who=>OpenFlashRoleMenu(who,handle));
        }
        if(mine.Length==0)
        {
            Add("先放置假人",OpenEffectTarget);
            Add("加载假人组合",OpenDummyLayouts);
        }
        Add("返回寻闪",OpenFlashMenu);
        menu.Open(p);
        if(selectedLabel is { } label && panels.TryGetValue(p.Slot,out var panel) && ReferenceEquals(panel.Menu,menu))
            panel.Selected=Math.Max(0,Array.FindIndex(mine,d=>d.LabelIndex==label));
    }
    private void OpenFlashRoleMenu(CCSPlayerController p,uint handle)
    {
        if(!Check(p,out _) || !effectDummies.TryGetValue(handle,out var d) || !DummyOwnedBy(p,d)) return;
        SelectDummy(p,d);
        var menu=WorkflowPanel(p,$"{DummyLabel(d)} · 闪光要求",out var Add,()=>DummyOwnedBy(p,d));
        menu.Back=who=>OpenFlashRequirements(who,d.LabelIndex);
        menu.Feedback=()=>$"当前要求：{FlashRoleLabel(d.SearchRole)}；按爆炸时的朝向计算。";
        var roles=new[]{FlashRole.MustFlash,FlashRole.Protect,FlashRole.Ignore};
        foreach(var role in roles)
            Add("设为"+FlashRoleLabel(role),who=>
            {
                if(!DummyOwnedBy(who,d)) return;
                if(d.SearchRole!=role)
                {
                    var s=Get(who);Invalidate(who.Slot,s);s.FlashMode=true;d.SearchRole=role;
                    UpdateDummyMarker(d);Say(who,$"{DummyLabel(d)}设为{FlashRoleLabel(role)}；修改要求后重新搜索。");
                }
                OpenFlashRequirements(who,d.LabelIndex);
            },hint:role==FlashRole.MustFlash ? "实投致盲事件至少1秒；不等于全白1秒。" :
                role==FlashRole.Protect ? "筛选眼位遮挡候选；实投不得观察到致盲。" : "不作为本次搜索的要求，也不判定是否达标。");
        menu.Open(p);
        if(panels.TryGetValue(p.Slot,out var panel) && ReferenceEquals(panel.Menu,menu)) panel.Selected=Array.IndexOf(roles,d.SearchRole);
    }
    private void ResetFlashDummies(CCSPlayerController p)
        =>ResetOwnedDummies(p,true);
    private void ResetOwnedDummies(CCSPlayerController p,bool showFlashMenu=false)
    {
        if(!Check(p,out _)) return;
        if(DummyLayoutEntitiesBusy || job is not null || flashJob is not null || DummyLayoutEffectsBusy ||
            flashVerification.Values.Any(round=>!round.Trial.Complete && round.Trial.ReleasedAt is not null) ||
            Utilities.FindAllEntitiesByDesignerName<CBaseCSGrenadeProjectile>(GrenadeKind.Flash.Projectile()).Any(g=>g.IsValid))
        {Say(p,"请等投掷观测、搜索或假人创建结束，再复位。");if(showFlashMenu) OpenFlashMenu(p);return;}
        var mine=OwnedDummies(p);
        if(mine.Length==0) {Say(p,"还没有你的假人，请先放置。");return;}
        foreach(var dummy in mine) if(DummyOwnedBy(p,dummy)) ResetDummy(dummy);
        var session=Get(p);session.Arm=null;session.Pending=null;session.FlightCheck=null;session.Snapshot=null;session.PreparationRevision++;
        session.CueStage=ReleaseStage.None;session.CueUntil=0;session.PreparationIssue=null;
        Say(p,$"已复位{mine.Length}个假人；要求保留。原姿态与身份未变时，按使用键准备即可继续使用原投法。");
        if(showFlashMenu) OpenFlashMenu(p);
    }
    private void OpenFlashTools(CCSPlayerController p)
    {
        if(!Check(p,out _)) return;
        var menu=WorkflowPanel(p,"寻闪 · 更多操作",out var Add);menu.Back=OpenFlashMenu;
        Add("开启 / 退出飞行",ToggleTrainingFlight);
        Add("当前位置重搜",StartFlashSearch,hint:"保留假人要求，从当前站位重新搜索。");
        Add("切回指定爆点",who=>{ExitFlashMode(who);TargetByKind(who);},hint:"结束按假人寻闪，改为选择爆点找投法。");
        Add("更换道具",OpenGrenadeMenu);
        Add("切换地图",OpenMapMenu);
        Add("按键检测",OpenInputCheck);
        Add("使用指引",ShowControls);
        Add("返回寻闪",OpenFlashMenu);menu.Open(p);
    }
    private void ExitFlashMode(CCSPlayerController p)
    {var s=Get(p);if(s.FlashMode) {Invalidate(p.Slot,s);s.Target=null;s.TargetPreset=null;s.Start=null;s.RequestedStart=null;}}

    private bool FlashPlanCurrent(CCSPlayerController owner,FlashPlanState plan,out string reason)
    {
        reason="假人或玩家状态已变化，请重新搜索";
        if(!Practice || plan.Epoch!=grenadeEpoch || owner.EntityHandle.Raw!=plan.Owner ||
            owner.PlayerPawn.Value is not {IsValid:true,LifeState:0} source || source.EntityHandle.Raw!=plan.OwnerPawn ||
            !sessions.TryGetValue(owner.Slot,out var s) || s.SteamId!=owner.SteamID || !s.FlashMode ||
            s.Kind!=GrenadeKind.Flash || s.Revision!=plan.Revision || !ReferenceEquals(s.FlashPlan,plan)) return false;
        foreach(var observer in plan.Observers)
        {
            if(!effectDummies.TryGetValue(observer.Id,out var d) || !DummyOwnedBy(owner,d) || !d.Ready || d.ResetRequested || d.SearchRole!=observer.Role ||
                d.Bot.PlayerPawn.Value is not {IsValid:true,LifeState:0,AbsOrigin:not null} pawn ||
                !plan.Pawns.TryGetValue(d.Handle,out uint expected) || pawn.EntityHandle.Raw!=expected)
            {reason=observer.Label+"状态已变化，请复位后重新搜索";return false;}
            var eye=NativeWorld.FromNative(pawn.AbsOrigin)+new Vector3(pawn.ViewOffset.X,pawn.ViewOffset.Y,pawn.ViewOffset.Z);
            if(Vector3.Distance(eye,observer.Eye)>2 || SceneAdvisor.ViewTurn(observer.View,new(pawn.EyeAngles.X,pawn.EyeAngles.Y,ThrowMode.Left))>1)
            {
                if(flashVerificationCheckPending)
                {
                    var movement=pawn.MovementServices?.As<CCSPlayer_MovementServices>();
                    Say(null,$"FLASHVERIFY POSE REJECT: expectedEye={observer.Eye}; eye={eye}; feet={NativeWorld.FromNative(pawn.AbsOrigin)}; heldFeet={d.PositionedFeet}; offset={pawn.ViewOffset.X}/{pawn.ViewOffset.Y}/{pawn.ViewOffset.Z}; eyeView={pawn.EyeAngles.X}/{pawn.EyeAngles.Y}; nativeView={pawn.V_angle.X}/{pawn.V_angle.Y}; move={pawn.MoveType}; duck={movement?.DuckAmount}/{movement?.Ducking}; now={Server.CurrentTime}; trial={CurrentFlashVerification(owner)?.Trial.Results().FirstOrDefault()?.Reason}.");
                }
                reason=observer.Label+"位置或朝向已变化，请重新搜索";return false;
            }
        }
        // Newly participating dummies also invalidate a prior subset result.
        if(OwnedDummies(owner).Count(d=>d.SearchRole!=FlashRole.Ignore)!=plan.Observers.Length) return false;
        reason="";return true;
    }
    private void StartFlashSearch(CCSPlayerController? player)
    {
        if(!Check(player,out var pawn)) return;
        var p=player!;var s=Get(p);
        if(DummyLayoutLoadActive || job is not null || flashJob is not null || flashSearchCheckPending || flashVerificationCheckPending) {Say(p,"正在搜索、加载假人或诊断，请先完成或取消。");return;}
        var targets=OwnedDummies(p).Where(d=>d.SearchRole!=FlashRole.Ignore).ToArray();
        if(!targets.Any(d=>d.SearchRole==FlashRole.MustFlash)) {Say(p,"先将至少一个假人设为“要闪”。");OpenFlashRequirements(p);return;}
        if(!Standing(pawn)) {Say(p,"从菜单退出飞行并在投掷位置站立停稳，再开始搜索。");return;}
        foreach(var d in targets)
            if(!d.Ready || d.ResetRequested || d.Bot.PlayerPawn.Value is not {IsValid:true,LifeState:0})
            {Say(p,$"{DummyLabel(d)}尚未就绪，请先复位。");return;}
        if(s.Kind!=GrenadeKind.Flash) SelectGrenade(p,GrenadeKind.Flash);
        s.Start=null;StartCommand(p,null,quiet:true);
        if(s.Start is null) {s.FlashMode=true;return;}
        s.FlashMode=true;s.Target=null;s.TargetPreset=null;
        var observers=targets.Select(d=>
        {
            var target=d.Bot.PlayerPawn.Value!;
            return new FlashObserver(d.Handle,DummyLabel(d),NativeWorld.FromNative(target.AbsOrigin!)+new Vector3(target.ViewOffset.X,target.ViewOffset.Y,target.ViewOffset.Z),
                new(target.EyeAngles.X,target.EyeAngles.Y,ThrowMode.Left),d.SearchRole);
        }).ToArray();
        var plan=new FlashPlanState {Observers=observers,Pawns=targets.ToDictionary(d=>d.Handle,d=>d.Bot.PlayerPawn.Raw),
            Epoch=grenadeEpoch,Revision=s.Revision,Owner=p.EntityHandle.Raw,OwnerPawn=pawn.EntityHandle.Raw};
        physics=physics with {PlayerGravity=CounterStrikeSharp.API.Modules.Cvars.ConVar.Find("sv_gravity")?.GetPrimitiveValue<float>() ?? physics.PlayerGravity,
            JumpImpulse=CounterStrikeSharp.API.Modules.Cvars.ConVar.Find("sv_jump_impulse")?.GetPrimitiveValue<float>() ?? physics.JumpImpulse};
        s.FlashPlan=plan;s.SearchPhysics=ProfileFor(s);
        IReadOnlyList<Aim> personalHints=[];
        try
        {
            if(!p.IsBot && p.SteamID!=0) personalHints=PersonalFlashHints.Select(PersonalLibrary.Read(p.SteamID,Server.MapName),
                s.SearchPhysics,s.Start.Value,Server.MapName,worldVersion?.ToJsonString());
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            Logger.LogWarning(ex,"Personal flash hints unavailable; continuing ordinary search without changing the collection");
            Say(p,"道具库暂时无法读取，本次继续常规搜索；收藏文件保留。");
        }
        var world=new NativeWorld(pawn);
        var search=new FlashEffectSearch(world,s.SearchPhysics,s.Start.Value,new FlashObjective(observers),
            (center,observer)=>FlashSearchSight(pawn,center,observer),
            new FlashSearchOptions {PreferredView=s.ReferenceView,MaxCandidates=4000,SeedAims=personalHints},ActionFilter(pawn,s.Start.Value));
        flashJob=new(p.Slot,p.SteamID,plan,world,search);
        ClosePanel(p);Say(p,"固定当前站位搜索静投和原地跳投；同时检查所有假人，包含反弹。保护对象仅返回遮挡候选，须手投核验。");
        if(personalHints.Count>0) Say(p,"优先检查此站位附近的个人收藏角度；按当前假人要求重新核验。");
    }
    private PredictionVisibility FlashSearchSight(CCSPlayerPawn source,Vector3 center,FlashObserver observer)
    {
        // Five nearby eye rays reject thin edge slivers. This is a geometric
        // heuristic, not CS2's native visibility/effect calculation.
        int clear=0;
        foreach(var offset in new[]{Vector3.Zero,new Vector3(3,0,0),new Vector3(-3,0,0),new Vector3(0,3,0),new Vector3(0,-3,0)})
        {
            var ray=PredictionSight(source,center,observer.Eye+offset,DummyFlashSight);
            if(!ray.Valid) return new(0,5,false);
            if(ray.Clear) clear++;
        }
        return new(clear,5);
    }
    private void FlashPrimary(CCSPlayerController p)
    {
        var workflow=ResolveWorkflow(p,Get(p));
        if(workflow.Primary==WorkflowAction.ReviewSearch && flashJob is { } current && current.Slot==p.Slot)
        {
            if(current.Search.PublishedCandidates.Count>0)
            {PublishFlashResults(current,Get(p),current.Search.PublishedResult());PrepareThrow(p,Math.Max(1,Get(p).Selected).ToString(CultureInfo.InvariantCulture));}
            else Say(p,$"按假人找闪：已试{current.Search.Evaluated}个投法；菜单可取消。");
        }
        else if(workflow.Primary==WorkflowAction.Prepare) PrepareThrow(p,Math.Max(1,Get(p).Selected).ToString(CultureInfo.InvariantCulture));
        else if(workflow.Primary==WorkflowAction.FlashSearch) StartFlashSearch(p);
    }
    private bool RecheckFlashEffects(CCSPlayerController p,Session s,Candidate candidate)
    {
        if(s.FlashPlan is not { } plan || !FlashPlanCurrent(p,plan,out _) || p.PlayerPawn.Value is not {IsValid:true} pawn) return false;
        var objective=new FlashObjective(plan.Observers);
        foreach(var observer in plan.Observers)
            if(!objective.Evaluate(candidate.Trajectory.Endpoint,observer,FlashSearchSight(pawn,candidate.Trajectory.Endpoint,observer)).GeometryAccepted)
            {Say(p,observer.Label+"的当前遮挡或预计效果已变化，请重新搜索。");return false;}
        return true;
    }
    private void TickFlashSearch()
    {
        foreach(var (slot,s) in sessions.ToArray())
        {
            if(s.FlashPlan is not { } plan) continue;
            var p=Utilities.GetPlayerFromSlot(slot);
            if(p?.IsValid!=true || p.SteamID!=s.SteamId) {Invalidate(slot,s);continue;}
            if(!FlashPlanCurrent(p,plan,out string reason)) {Invalidate(slot,s);s.FlashMode=true;Say(p,reason);}
        }
        if(flashJob is not { } current) return;
        var owner=Utilities.GetPlayerFromSlot(current.Slot);
        if(owner?.IsValid!=true || owner.SteamID!=current.Steam) {CancelFlashSearch();return;}
        var session=Get(owner);
        try
        {
            if(Stopwatch.GetElapsedTime(current.Started).TotalSeconds>30) current.Search.RequestFinish();
            var watch=Stopwatch.StartNew();int units=0;
            while(watch.Elapsed.TotalMilliseconds<searchBudgetMs && units++<SearchUnitLimit && current.Search.Advance()) { }
            current.WorkMs+=watch.Elapsed.TotalMilliseconds;current.PeakMs=Math.Max(current.PeakMs,watch.Elapsed.TotalMilliseconds);
            if(current.PublishedCount!=current.Search.PublishedCandidates.Count)
            {
                current.PublishedCount=current.Search.PublishedCandidates.Count;
                PublishFlashResults(current,session,current.Search.PublishedResult());
            }
            if(!current.Search.Done) return;
            var result=current.Search.Result!;
            PublishFlashResults(current,session,result);
            lastSearchDiagnostics=new(current.World.TraceCount,current.World.HitCount,current.World.SolidStartCount,current.WorkMs,current.PeakMs);
            QueueFlashSearchTelemetry(current,session,result);
            CancelFlashSearch();
            int count=session.Result!.Matches.Count;
            Say(owner,count>0 ? $"已有{count}个预测候选；“不闪”仅通过遮挡筛选，全部须手投核验。" : "本次未找到同时符合全部要求的候选，不代表理论无解。可检查假人朝向、改变要求或换站位重搜。");
            // The player may already be throwing a published candidate.
        }
        catch(Exception ex) {Logger.LogError(ex,"Flash objective search failed");CancelFlashSearch();Say(owner,"寻闪查询失败，已停止；原有假人保留，可重新搜索。");}
    }
    private void ShowFlashDetails(CCSPlayerController p)
    {
        var s=Get(p);
        ShowDetails(p,"按假人找闪 · 预测与实投",()=>
        {
            Say(p,"要闪：粗估下限至少1秒；不闪：5处眼位遮挡筛选。两者均未校准，不表示全白时长或保证安全。");
            if(s.FlashPlan?.Result?.Matches.ElementAtOrDefault(s.Selected-1) is { } candidate)
                foreach(var effect in candidate.Effects) Say(p,FlashPredictionRow(effect));
            foreach(var line in FlashVerificationLines(p)) Say(p,line);
            if(CurrentFlashVerification(p) is null && s.FlashPlan?.Trials?.Measurements(s.Selected) is { } past)
            {
                Say(p,s.FlashPlan.Trials.Summary(s.Selected)!);
                foreach(var row in past) Say(p,"上次 "+row.Label+"："+FlashMeasurementText(row));
            }
            Say(p,"按假人固定朝向计算；投掷者自身未自动保护。可放置一个不闪假人表示爆炸时己方站位。");
        });
    }
    private static string FlashPredictionRow(FlashObserverEvaluation effect)=>effect.Observer.Label+" "+FlashRoleLabel(effect.Observer.Role)+" · "+
        (effect.Observer.Role==FlashRole.Protect ? "眼位遮挡，待实投" : effect.Estimate.Text);
    private string? FlashRosterContext(CCSPlayerController owner,EffectDummy dummy,string? preview)
    {
        var s=Get(owner);if(!s.FlashMode) return null;
        var candidate=s.FlashPlan?.Result?.Matches.ElementAtOrDefault(s.Selected-1);
        var effect=candidate?.Effects.FirstOrDefault(e=>e.Observer.Id==dummy.Handle);
        var measurement=CurrentFlashVerification(owner)?.Trial.Results().FirstOrDefault(row=>row.Id==dummy.Handle);
        return DummyRoster.FlashContext(dummy.SearchRole,measurement,effect?.Estimate,preview);
    }
    private bool RenderFlashHud(CCSPlayerController p,Session s,float now,WorkflowResolution workflow)
    {
        if(!workflow.Flash || workflow.Step is WorkflowStep.Placement or WorkflowStep.Loading or WorkflowStep.Region) return false;
        bool searching=workflow.Step==WorkflowStep.Searching;
        string body="按假人找闪\n设置要闪与不闪对象，在投掷位置站稳后搜索";
        if(searching) body="按假人找闪\n等待结果，或打开菜单查看/采用投法";
        else if(workflow.Step==WorkflowStep.Preparing) body="寻闪 · 准备投掷\n停稳等待，上方提示就绪后再蓄力手投";
        else if(workflow.Step==WorkflowStep.PreparationBlocked) body="寻闪 · 重新准备\n按上方原因调整后，再次就位";
        else if(s.Result?.Matches.ElementAtOrDefault(s.Selected-1) is { } candidate)
            body=$"寻闪 · 投法{s.Selected}/{s.Result.Matches.Count}\n{PanelText.Line(ThrowInstructions.Compact(candidate.Aim),36)}\n{ThrowOperation(candidate.Aim)}";
        else if(s.FlashPlan?.Result is not null) body="按假人找闪\n调整要求或换站位后，再次搜索";
        SendBottomHud(p,s,body,now,keys:WorkflowKeys.Text(workflow));
        return true;
    }
}
