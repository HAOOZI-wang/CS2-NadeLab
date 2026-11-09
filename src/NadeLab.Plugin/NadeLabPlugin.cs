using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using NadeLab.Core;
using CsTrace = CounterStrikeSharp.API.Modules.Utils.Trace;
using TraceOptions = CounterStrikeSharp.API.Modules.Utils.TraceOptions;

namespace NadeLab.Plugin;

[MinimumApiVersion(376)]
public sealed partial class NadeLabPlugin : BasePlugin
{
    public override string ModuleName => "NadeLab";
    public override string ModuleVersion => NadeLabVersion.Current;
    public override string ModuleAuthor => "NadeLab contributors";
    public override string ModuleDescription => "Multi-map grenade inverse search and real-throw measurements";
    private static readonly JsonSerializerOptions Json = new()
    {
        IncludeFields=true,WriteIndented=false,
        TypeInfoResolver=NadeLabJsonContext.Default
    };
    private readonly Dictionary<int, Session> sessions = [];
    private readonly Dictionary<uint, Flight> flights = [];
    private PhysicsProfile physics = new();
    private Telemetry? telemetry;
    private SearchJob? job;
    private bool loaded;
    private static bool trainingEnabled;
    private JsonObject? worldVersion;
    private WindowsTiming? windowsTiming;
    private string DataPath => Path.Combine(ModuleDirectory,"data");

    public override void Load(bool hotReload)
    {
        trainingEnabled=false;
        // A profile is opt-in and is never automatically labeled calibrated.
        var profilePath = Path.Combine(ModuleDirectory,"physics.json");
        if (File.Exists(profilePath))
        {
            try { physics = JsonSerializer.Deserialize<PhysicsProfile>(File.ReadAllText(profilePath),Json) ?? new(); physics.Validate(); }
            catch (Exception ex) { Logger.LogError(ex,"Invalid physics.json; using uncalibrated defaults."); physics = new(); }
        }
        telemetry = new(Logger);
        if (OperatingSystem.IsWindows())
        {
            AddTimer(10,()=> {if(Practice) windowsTiming?.EnsurePolicy();},TimerFlags.REPEAT);
        }
        loaded = true;
        AddCommand("css_nl_training","Server-console training opt-in: 1 / 0",(p,c)=>
        {
            if(p is not null) {Say(p,"训练开关由服务器管理员在服务端配置中设置。");return;}
            if(c.ArgCount!=2 || c.ArgByIndex(1) is not ("0" or "1"))
            {Say(null,$"TRAINING: enabled={trainingEnabled}; practice={Practice}; use css_nl_training 1/0.");return;}
            if(c.ArgByIndex(1)=="0" && (dummyCheckPending || panelCheckPending || placementCheck is not null || effectStressCheck is not null || recentImpactCheck is not null))
            {Say(null,"TRAINING: wait for the current empty-server diagnostic to finish before disabling training.");return;}
            trainingEnabled=c.ArgByIndex(1)=="1";
            if(!trainingEnabled) {RestoreTrainingProtection();Reset();}
            SyncTrainingProtection();
            Say(null,$"TRAINING: enabled={trainingEnabled}; practice={Practice}.");
        });
        ReadWorldVersion();
        ReadFailureEvidence();
        // Migrate the old built-in profile to the measured half-tick integrator.
        // Explicit custom profiles keep their authored step size.
        if(physics.Id=="mirage-partial-v3-unvalidated") physics=physics with {Id="mirage-partial-v4-jump-latch-unvalidated",StepSeconds=1f/128};
        ReadRouteLibrary();
        WarmManagedPaths();
        AddCommand("css_nl_help","NadeLab commands",(p,c) => ShowControls(p));
        AddPlayerCommand("css_n","NadeLab next step",(p,args) => PrimaryAction(p));
        AddCommand("css_nl_reposition","Search from current position, keep target",(p,c)=>QueuePanelOrWorkflowAction(p,ActivatePanel,Reposition));
        AddPlayerCommand("css_nl_continue","Continue the existing search",(p,args)=>ContinueSearch(p));
        AddPlayerCommand("css_nl_objective","Mark the intended window and viewing pose",(p,args)=>OpenObjectiveEditor(p));
        AddPlayerCommand("css_nl_area","Adjust the allowed landing region",(p,args)=>OpenRegionEditor(p));
        AddPlayerCommand("css_nl_retarget","Select a new target under the crosshair",(p,args)=>Retarget(p));
        AddCommand("css_nl_step","NadeLab next step / key binding",(p,c)=>QueuePanelOrWorkflowAction(p,current=>MovePanel(current,-1),PrimaryAction));
        AddPlayerCommand("css_nl","Open in-game NadeLab menu",(p,args) => TogglePanel(p));
        AddPlayerCommand("css_nadelab","Open in-game NadeLab menu",(p,args) => TogglePanel(p));
        AddPlayerCommand("css_nl_record","Record one freely chosen manual smoke throw",(p,args)=>RecordManualThrow(p));
        AddPlayerCommand("css_nl_coverage","Review the last real smoke coverage",(p,args)=>OpenSmokeReview(p));
        AddCommand("css_nl_selftest","Server-console current-map native collision diagnostic",NativeSelfTest);
        AddCommand("css_nl_replay","Server-console replay of captured search/throws",ReplayCommand);
        RegisterBenchmark();
        AddCommand("css_nl_standcheck","Server-console player hull placement diagnostic",StandingDiagnostic);
        AddCommand("css_nl_spawncheck","Read-only diagnostic of every numbered spawn",SpawnTravelDiagnostic);
        AddCommand("css_nl_preparecheck","Server-console bot view and placement diagnostic",PreparationDiagnostic);
        AddCommand("css_nl_pathcheck","Read-only forward run-up player-hull diagnostic",PreparationPathDiagnostic);
        AddPlayerCommand("css_nl_smoke","Give a training smoke",(p,args) => { if (Check(p,out _)) p!.GiveNamedItem("weapon_smokegrenade"); });
        AddPlayerCommand("css_nl_target","Pick a smoke target",TargetArguments);
        AddPlayerCommand("css_nl_start","Record a standing throw position",(p,args)=>StartCommand(p,null));
        AddPlayerCommand("css_nl_solve","Search smoke lineups",(p,args)=>Solve(p,args.FirstOrDefault() ?? "quick"));
        AddPlayerCommand("css_nl_solution","Preview and position a candidate",(p,args)=>Preview(p,args.FirstOrDefault() ?? ""));
        AddCommand("css_nl_verify","Arm one manual throw measurement",VerifyCommand);
        AddPlayerCommand("css_nl_throw","Position, give smoke and arm the selected manual throw",(p,args) =>
        { if (Check(p,out _)) PrepareThrow(p!,args.FirstOrDefault() ?? Math.Max(1,Get(p!).Selected).ToString(CultureInfo.InvariantCulture)); });
        AddCommand("css_nl_next","Show the next candidate on the HUD",(p,c)=>QueuePanelOrWorkflowAction(p,current=>MovePanel(current,1),current=>NextCandidate(current,null!)));
        AddCommand("css_nl_hud","Show or hide the in-game throw HUD",HudCommand);
        AddCommand("css_nl_save","Save last successful verified lineup",SaveCommand);
        AddCommand("css_nl_status","Search status",(p,c) => Say(p,flashJob is not null ? $"按假人找闪：已计算{flashJob.Search.Evaluated}个候选。" : job is null ? "当前无搜索任务。" : $"{job.Search.Phase}，已计算 {job.Search.Evaluated} 个候选；物理配置 {physics.Id}。"));
        AddCommand("css_nl_perf","Plugin timing and native collision counters",(p,c) => Say(p,PerformanceReport()));
        AddCommand("css_nl_cancel","Cancel your search",(p,c) => { if (p != null && (job?.Slot == p.Slot || flashJob?.Slot == p.Slot)) { CancelSearch(); Say(p,"搜索已取消。"); } });
        AddPlayerCommand("css_nl_flash","Find flash throws by dummy requirements",(p,args)=>OpenFlashMenu(p));
        RegisterListener<Listeners.OnTick>(Tick);
        RegisterListener<Listeners.OnMapEnd>(Reset);
        RegisterListener<Listeners.OnMapStart>(_ => { Reset(); ReadWorldVersion(); ReadFailureEvidence(); ReadImpactArchive(); });
        RegisterListener<Listeners.OnClientDisconnect>(Disconnect);
        RegisterListener<Listeners.OnPlayerButtonsChanged>(PracticeButtons);
        RegisterEventHandler<EventGrenadeThrown>(OnThrown);
        RegisterEventHandler<EventSmokegrenadeDetonate>(OnSmoke);
        RegisterGrenadeSupport();
        RegisterRecentImpacts();
        RegisterFlightPreview();
        RegisterSpawnMarkers();
        RegisterPersonalLibrary();
        RegisterNativeControls();
        RegisterCompanion();
        RegisterPlacement();
        RegisterPanelControls();
        RegisterPanelDisplay();
        RegisterPanelDiagnostic();
        RegisterGrenadeBody();
        RegisterLifetimeDiagnostic();
        RegisterEffectStressDiagnostic();
        RegisterEffectPreviewDrawing();
        RegisterDummyDiagnostic();
        RegisterListener<Listeners.OnServerPostEntityThink>(HoldAnimatingDummyPoses);
        RegisterPredictionDiagnostic();
        RegisterFlashSearchDiagnostic();
        RegisterFlashVerificationDiagnostic();
        RegisterDummyLayouts();
        RegisterEventHandler<EventPlayerSpawn>((ev,info)=>
        {Server.NextFrame(()=> {if(loaded) SyncTrainingProtection();});return HookResult.Continue;});
        AddTimer(1,SyncTrainingProtection,TimerFlags.REPEAT);
        AddCommand("css_nl_protectionstatus","Show training damage protection",(p,c)=>
        {if(p is null) foreach(var who in Utilities.GetPlayers()) if(who.PlayerPawn.Value is {IsValid:true} pawn) Say(null,$"PROTECTION: slot={who.Slot}; alive={pawn.LifeState==0}; takesDamage={pawn.TakesDamage}; health={pawn.Health}; practice={Practice}.");});
        Logger.LogInformation("NadeLab {Version} loaded; css_nl_training 1, supported map and sv_cheats required. Physics: {Profile}, calibrated: {Calibrated}",ModuleVersion,physics.Id,physics.Calibrated);
    }
    public override void Unload(bool hotReload)
    {
        inputCompanion?.Dispose();inputCompanion=null;ClearDummyBatch();
        loaded = false; trainingEnabled=false; RestoreTrainingProtection(); Reset(); telemetry?.Dispose(); telemetry = null;
        managedWarmup?.GetAwaiter().GetResult(); managedWarmup=null;
        windowsTiming?.Dispose(); windowsTiming=null;
    }
    private void CancelSearch() { job?.Search.Dispose(); job = null; CancelFlashSearch(); }
    private void ReadWorldVersion()
    {
        try
        {
            string inf = Path.Combine(GameContentRoot,"steam.inf");
            var path=InstalledMapVpk(Server.MapName);
            var map=path is null ? null : new FileInfo(path);
            worldVersion = new() { ["GameVersion"]=File.Exists(inf) ? File.ReadAllText(inf) : "unknown",
                ["MapBytes"]=map?.Exists==true ? map.Length : 0, ["MapLastWriteUtc"]=map?.Exists==true ? map.LastWriteTimeUtc.ToString("O") : null,
                ["MapName"]=Server.MapName };
        }
        catch (Exception ex) { Logger.LogWarning(ex,"Could not read map/build metadata"); worldVersion = new() { ["GameVersion"]="unknown" }; }
    }
    private void Reset() { StopPreparationContinuationDiagnostic();ClearSpawnMarkers();RestoreMenuEquipment();menuEquipWindows.Clear();ClearDummyBatch();StopDummyLayoutDiagnostic();StopDummyLayoutLoad();StopFlashVerificationDiagnostic();StopFlashSearchDiagnostic();ResetMapSelection();StopEffectStressDiagnostic();StopPlacementDiagnostic();RestorePlacements();RestoreGrenadeBodies();panels.Clear();ClearRecentImpacts();ClearPersonalLibrary();ClearDummyPredictions();ClearEffectPreviews();ClearPreviews(); CancelSearch(); ClearHuds();ClearPanelDisplays(); foreach(var s in sessions.Values) s.Continuation?.Search.Dispose(); sessions.Clear(); flights.Clear(); ClearGrenadeState(); queuedPlayers.Clear(); benchmarkRouteRun=null;benchmarkRouteLibrary=new(); }
    private void Disconnect(int slot)
    {
        flightPreviewClients.Remove(slot);
        RestoreMenuEquipment(slot);
        menuEquipWindows.Remove(slot);
        ClearDummyBatch(slot);
        StopDummyLayoutLoad(slot);
        ClearFlashVerification(slot);
        RemoveRecentImpacts(slot);
        RemoveDummyPredictions(slot);
        RestoreGrenadeBody(slot);panels.Remove(slot);ClearPanelDisplay(slot);
        RemovePlayerEffectDummies(slot);
        if (job?.Slot == slot || flashJob?.Slot == slot) CancelSearch();
        ClearEffectPreview(slot);
        ClearPreview(slot);
        if(sessions.Remove(slot,out var disconnected)) disconnected.Continuation?.Search.Dispose();
        foreach (var id in flights.Where(x => x.Value.Slot == slot).Select(x => x.Key).ToArray()) flights.Remove(id);
        foreach (var id in grenadeFlights.Where(x=>x.Value.Slot==slot).Select(x=>x.Key).ToArray()) grenadeFlights.Remove(id);
    }
    private void Say(CCSPlayerController? p,string message,bool feedback=true)
    {
        // Bots have no client console. Calling the native per-client console
        // printer for a fake client can crash the dedicated server.
        if(p?.IsValid!=true || p.IsBot) {Server.PrintToConsole($"[NadeLab] {message}");return;}
        if(informationPlayer==p.Slot && informationCapture is not null) {informationCapture.Add(message);return;}
        if(feedback) HudNotice(Get(p),message,5);
        // A read-only console journal retains details; no chat flood or typing
        // is required to navigate the training workflow.
        p.PrintToConsole($"[NadeLab] {message}");
    }
    private static bool Practice => trainingEnabled && MapCatalog.IsSupported(Server.MapName) && ConVar.Find("sv_cheats")?.GetPrimitiveValue<bool>() == true;
    private bool Check(CCSPlayerController? p, out CCSPlayerPawn pawn,bool allowHeldGrenade=false,bool allowPlacement=false)
    {
        pawn = null!;
        if (!Practice) { Say(p,"训练尚未开启：请让管理员加载 nadelab_practice.cfg，再进入支持的地图。"); return false; }
        if (p?.IsValid != true || p.IsBot || p.PlayerPawn.Value is not { IsValid:true } value || value.LifeState != 0)
        { Say(p,"需要存活的真人玩家。"); return false; }
        if(!allowPlacement && sessions.TryGetValue(p.Slot,out var session) && session.SteamId==p.SteamID && session.Placement is not null)
        {Say(p,"正在道具定位：使用键确认、换弹键取消返回，菜单开关范围。其他训练操作请退出定位后使用。");return false;}
        if(!allowHeldGrenade && ThrowInProgress(p)) {Say(p,"道具正在蓄力或出手，请完成投掷后再操作训练功能。");return false;}
        pawn = value; return true;
    }
    private Session Get(CCSPlayerController p)
    {
        if (!sessions.TryGetValue(p.Slot,out var s) || s.SteamId != p.SteamID)
            sessions[p.Slot] = s = new() { SteamId = p.SteamID };
        return s;
    }
    private void NativeSelfTest(CCSPlayerController? p,CommandInfo c)
    {
        if (p != null) { Say(p,"此诊断只允许服务器控制台调用。"); return; }
        if(Utilities.GetPlayers().Any(player=>!player.IsBot) || !BenchmarkIdle)
        {Say(null,"SELFTEST FAIL: requires an empty, idle training server.");return;}
        if (!Practice) { Say(null,"SELFTEST FAIL: supported training map and sv_cheats 1 required."); return; }
        try
        {
            var world=new NativeWorld(null);
            var spawns=Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("info_player_terrorist")
                .Concat(Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("info_player_counterterrorist"))
                .Where(x=>x.IsValid && x.AbsOrigin is not null).Take(32);
            foreach(var spawn in spawns)
            {
                var origin=NativeWorld.FromNative(spawn.AbsOrigin!);
                var floor=world.Sweep(origin+new Vector3(0,0,72),origin-new Vector3(0,0,256),0);
                if(!floor.Hit || floor.StartSolid || floor.Normal.Z<.7f) continue;
                var feet=floor.Position+new Vector3(0,0,.1f);
                var trajectory=SmokeSimulation.Run(world,physics,new(feet),new(75,spawn.AbsRotation?.Y ?? 0,ThrowMode.Right));
                if(trajectory.Status!=SimulationStatus.Rested) continue;
                WarmNativeDrawing(feet);
                Say(null,$"SELFTEST PASS: native {Server.MapName} floor Z={floor.Position.Z:F2}; simulated smoke {trajectory.Status}, {trajectory.Bounces} contacts, endpoint=({trajectory.Endpoint.X:F1},{trajectory.Endpoint.Y:F1},{trajectory.Endpoint.Z:F1}). This does not verify real smoke physics.");
                return;
            }
            Say(null,$"SELFTEST FAIL: {Server.MapName} has no supported spawn with a valid native smoke trace.");
        }
        catch (Exception ex) { Logger.LogError(ex,"NadeLab native selftest failed"); Say(null,"SELFTEST FAIL: "+ex.Message); }
    }
    private static bool Standing(CCSPlayerPawn pawn) => pawn.MoveType==MoveType_t.MOVETYPE_WALK && (pawn.Flags & 1) != 0 &&
        NativeWorld.FromNative(pawn.AbsVelocity).Length() <= 1 && Upright(pawn);
    private void Invalidate(int slot, Session s)
    {
        ClearDummyBatch(slot);
        ClearPersonalActivity(slot);
        ClearFlashVerification(slot);
        if (job?.Slot == slot || flashJob?.Slot == slot) CancelSearch();
        s.FlashMode=false;s.FlashPlan=null;
        ClearPreview(slot);
        ClearEffectPreview(slot);s.LastEffectCenter=null;s.NextEffectAt=0;s.FirePreviewCells=null;
        s.Continuation?.Search.Dispose();s.Continuation=null;
        s.Result = null; s.Arm = null; s.Pending=null; s.FlightCheck=null; s.PreparationIssue=null; s.CueStage=ReleaseStage.None; s.CueUntil=0; s.LastVerified = null; s.SmokeReview=null; s.RegionDraft=null; s.ObjectiveCorner=null; s.RegionSelection=0; s.Selected = 0; s.HudNotice = null; s.Revision++; s.PreparationRevision++;
    }
    private void TargetArguments(CCSPlayerController? p,string[] args)
    {
        float? radius=null;
        if(args.Length>1)
        {
            if(!float.TryParse(args[1],NumberStyles.Float,CultureInfo.InvariantCulture,out float value) || !float.IsFinite(value) || value is <1 or >256)
            {Say(p,"半径应为1..256u。"); return;}
            radius=value;
        }
        TargetCommand(p,null,args.FirstOrDefault(),radius);
    }
    private void TargetCommand(CCSPlayerController? p,CommandInfo? c,string? explicitMethod=null,float? explicitRadius=null)
    {
        if (!Check(p,out var pawn)) return;
        if(Get(p!).Kind!=GrenadeKind.Smoke) {PickGrenadeTarget(p!,explicitMethod);return;}
        string method = explicitMethod ?? (c?.ArgCount > 1 ? c.ArgByIndex(1).ToLowerInvariant() : "aim");
        bool mirage=Server.MapName=="de_mirage";
        if(method=="vip" && !mirage) {Say(p,"VIP窗口预设只用于Mirage；本图请用准星选择地面。");return;}
        if (method is not ("aim" or "feet" or "vip" or "point")) { Say(p,"!nl_target：准星封烟区域；!nl_target vip：VIP区域；!nl_target point：精确落点。"); return; }
        float radius = explicitRadius ?? 32;
        if (c?.ArgCount > 2 && (!float.TryParse(c.ArgByIndex(2),NumberStyles.Float,CultureInfo.InvariantCulture,out radius) || !float.IsFinite(radius) || radius is < 1 or > 256))
        { Say(p,"半径应为 1..256 的有限数值（游戏单位）。"); return; }
        Vector3 point;
        bool precise=method=="point" || explicitRadius.HasValue || c?.ArgCount>2;
        if (method == "vip") point=SmokeTargets.MirageVip.Position;
        else if (method == "feet") point = NativeWorld.FromNative(pawn.AbsOrigin!);
        else
        {
            var eye = NativeWorld.FromNative(pawn.AbsOrigin!) + new Vector3(0,0,pawn.ViewOffset.Z);
            var hit = CsTrace.TraceShape(NativeWorld.ToNative(eye),pawn.EyeAngles,pawn,
                new TraceOptions { InteractsWith = Masks.SolidBrushOnly | Contents.PhysicsProp });
            if (!hit.DidHit() || hit.IsAllSolid) { Say(p,"准星未命中有效表面，或眼位在实体内部。"); return; }
            point = NativeWorld.FromNative(hit.EndPos);
            if(!precise && !(mirage && SmokeTargets.NearMirageVip(point)) && hit.Normal.Z<.7f)
            {Say(p,mirage ? "通用封烟区域请瞄准附近地面；VIP窗口可直接瞄准窗口或从菜单选VIP烟。" : "封烟区域请瞄准有支撑的地面。"); return;}
        }
        Target target=precise ? new(point,radius) : mirage && (method=="vip" || SmokeTargets.NearMirageVip(point)) ? SmokeTargets.MirageVip : SmokeTargets.Around(point);
        var world=new NativeWorld(pawn);
        if(target.IsArea)
        {
            var floor=world.Sweep(target.Position+new Vector3(0,0,48),target.Position-new Vector3(0,0,96),2);
            if(!floor.Hit || floor.StartSolid || floor.Normal.Z<.7f) {Say(p,"封烟区域中心没有有效地面支撑，请改选窗内地面。"); return;}
            target=target with {Position=floor.Position-new Vector3(0,0,2)};
        }
        var s = Get(p!); Invalidate(p!.Slot,s); s.Target = target; s.TargetPreset=target;
        Say(p,target.AreaHalfSize is { } size ? $"{target.Label}：允许地面范围约 {size.X*2:F0}×{size.Y*2:F0}u，高差±{size.Z:F0}u；不要求贴近准星，封视野须看实投。" :
            $"精确落点 ({point.X:F1},{point.Y:F1},{point.Z:F1})，半径 {radius:F1}u。");
    }
    private void StartCommand(CCSPlayerController? p,CommandInfo? c,bool quiet=false)
    {
        if(!quiet && p?.IsValid==true && Get(p).FlashMode) {StartFlashSearch(p);return;}
        if (!Check(p,out var pawn)) return;
        if (!Standing(pawn)) { Say(p,$"起点需关闭noclip、站立停稳；当前速度 {NativeWorld.FromNative(pawn.AbsVelocity).Length():F1}u/s、落地 {((pawn.Flags&1)!=0 ? "是" : "否")}、站立 {(Upright(pawn) ? "是" : "否")}。"); return; }
        var s = Get(p!); Invalidate(p!.Slot,s);
        var feet=NativeWorld.FromNative(pawn.AbsOrigin!);
        if(!TryStandingPosition(pawn,feet,out var supported)) {Say(p,"站位的完整玩家碰撞体无安全支撑，请在台阶上站稳或稍移位置再试。"); return;}
        if(supported.Z-feet.Z>3) {Say(p,"当前脚位尚需较大台阶修正，请落稳后重新记录，避免模拟起点与实际不一致。"); return;}
        // Safe teleport clearance is not the physical grounded throw origin.
        // Gravity returns a raised preparation pose to these actual feet.
        s.Start = new(feet,pawn.ViewOffset.Z,ThrowOrigin.Preparation); s.RequestedStart=s.Start;
        s.ReferenceView = new(pawn.EyeAngles.X,pawn.EyeAngles.Y,ThrowMode.Left);
        if(!quiet) Say(p,$"操作起点已记录 ({s.Start.Value.Feet.X:F1},{s.Start.Value.Feet.Y:F1},{s.Start.Value.Feet.Z:F1})。站投原地执行；助跑投从这里向前100u，到标记处松键或起跳松键。");
    }
    private void SolveCommand(CCSPlayerController? p,CommandInfo? c)
        => Solve(p,c?.ArgCount > 1 ? c.ArgByIndex(1).ToLowerInvariant() : "quick");
    private void Solve(CCSPlayerController? p,string mode,bool keepResults=false)
    {
        if (!Check(p,out var pawn)) return;
        var s = Get(p!);
        if(s.FlashMode) {StartFlashSearch(p);return;}
        if(s.RegionDraft is not null) {OpenRegionEditor(p);return;}
        if (s.Start is null || s.Target is null) { Say(p,"先记录 !nl_target 和 !nl_start。"); return; }
        if (DummyLayoutLoadActive || job != null || flashJob is not null) { Say(p,"服务器正在搜索或加载假人；完成或取消后再试。"); return; }
        if(mode=="wide" && s.Continuation is not null) {ContinueSearch(p);return;}
        if (mode is not ("quick" or "wide" or "nearby")) { Say(p,"用法：!nl_solve [quick|wide|nearby]"); return; }
        bool wide = mode == "wide";
        s.RequestedStart ??= s.Start;
        s.Start=s.RequestedStart;
        physics=physics with {PlayerGravity=ConVar.Find("sv_gravity")?.GetPrimitiveValue<float>() ?? physics.PlayerGravity,
            JumpImpulse=ConVar.Find("sv_jump_impulse")?.GetPrimitiveValue<float>() ?? physics.JumpImpulse};
        s.Continuation?.Search.Dispose();s.Continuation=null;
        if(!keepResults)
        {s.Result = null; s.Arm = null; s.Pending=null; s.FlightCheck=null; s.PreparationIssue=null; s.CueStage=ReleaseStage.None; s.CueUntil=0; s.LastVerified = null; s.SmokeReview=null; s.Selected = 0; s.HudNotice = null;}
        var world = new NativeWorld(pawn);
        if(!keepResults || s.SearchPhysics is null) s.SearchPhysics=ProfileFor(s);
        var searchOptions=wide ? new SearchOptions {YawSpan=180,YawStep=15,MaxCandidates=7000,Exhaustive=true,IncludeActions=true} :
            new SearchOptions {IncludeActions=true,MaxCandidates=6000};
        searchOptions=searchOptions with {ForwardSpeed=SmokeRunSpeed(pawn,s.Kind),AirForwardSpeed=AirForwardSpeed(),PreferredView=s.ReferenceView};
        searchOptions=searchOptions with {SeedAims=SearchSeedAims(p,s.Start.Value,s.Target.Value,s.ReferenceView,s.SearchPhysics,searchOptions)};
        job = new(p!.Slot,p.SteamID,s.Revision,world,new(world,s.SearchPhysics,s.Start.Value,s.Target.Value,
            searchOptions,canRunUp:ActionFilter(pawn,s.Start.Value),runUpSteps:IncrementalRunUpFilter(pawn,s.Start.Value),rejectedByEvidence:s.Kind==GrenadeKind.Smoke ? FailureFilter(s.Start.Value) : null,
                executionSpace:ExecutionSpaceFilter(pawn,s.SearchPhysics)),wide ? 40 : 20) {AutomaticExpansion=!wide && mode!="nearby",OwnerIdentity=ActionIdentity(p)};
        if(mode=="nearby" && (job.AlternativeStart=NearbyAlternative(pawn,s.Start.Value,s.Target.Value)) is not null) job.TimeLimit=8;
        Say(p,mode=="nearby" ? "本次允许在附近更换站位，找到后会明确提示。" : s.Start.Value.Origin==ThrowOrigin.Preparation ?
            "本次固定你记录的操作起点；助跑投从这里向前，不再后退另找起跑位置。" : "这是旧记录：固定原出手基准，助跑点仍在后方；换站位重搜可使用新的操作起点模式。");
        Say(p,$"开始{(wide ? "研究搜索" : "渐进搜索")}；{(job.AlternativeStart is not null ? "原位8秒，必要时补搜附近安全站位20秒" : $"最多{(wide || mode=="quick" ? 40 : 20)}秒分阶段拓展")}，随后复核候选；!nl查看/取消。覆盖仍须实投确认。");
    }
    private Candidate? Candidate(CCSPlayerController p,string number)
    {
        var session=Get(p);
        if(session.FlashMode && (session.FlashPlan is not { } plan || !FlashPlanCurrent(p,plan,out _)))
        {Say(p,"寻闪对象已变化，请从当前位置重新搜索。");return null;}
        if(Get(p).RegionDraft is not null) {OpenRegionEditor(p);return null;}
        var result = Get(p).Result;
        if (result == null || !int.TryParse(number,out int id) || id < 1 || id > result.Matches.Count)
        { Say(p,"请用 !nl_solution 编号 或 !nl_verify 编号，编号来自已完成的搜索结果。"); return null; }
        return result.Matches[id - 1];
    }
    private void SolutionCommand(CCSPlayerController? p,CommandInfo c)
        => Preview(p,c.ArgCount > 1 ? c.ArgByIndex(1) : "");
    private bool Preview(CCSPlayerController? p,string number)
    {
        if (!Check(p,out var pawn)) return false;
        ClosePanel(p!);
        var s = Get(p!);
        ClearPreparationAttempt(p!,s);
        if(Candidate(p!,number) is not { } candidate)
            return BlockPreparation(p!,s,"候选或任务已变化，请确认当前候选后重新准备。");
        if(p!.PlayerPawn.Value?.MoveType==MoveType_t.MOVETYPE_NOCLIP)
            return BlockPreparation(p,s,"先从菜单退出飞行并落地，再准备手投记录。");
        if(s.FlashMode && !RecheckFlashEffects(p,s,candidate))
            return BlockPreparation(p,s,"寻闪对象或遮挡已变化，请重新搜索后再准备。");
        SelectCandidate(p!,int.Parse(number,CultureInfo.InvariantCulture));
        if(s.Start is not { } start) return BlockPreparation(p,s,"原站位已失效，请换站位重搜后再准备。");
        var feet=start.Feet;
        if(candidate.Aim.NeedsRunUp || candidate.Aim.EffectiveAction==ThrowAction.AirForwardJump)
        {
            var runUp=FindPreparation(pawn,start,candidate.Aim,s.SearchPhysics ?? ProfileFor(s));
            if(runUp is null) return BlockPreparation(p,s,"操作路线受阻、缺少支撑或移动参数已变化，请重新搜索/换静止投。");
            feet=runUp.Value;
        }
        if (!ReturnToStart(p!,pawn,feet)) return BlockPreparation(p,s,"站位复位未完成；先落地停稳，并检查原站位是否被遮挡。");
        if(!TryAlignPreparation(p,pawn,s,candidate.Aim)) return false;
        RefreshSelectedGuides(p!);
        Say(p,$"方案角度：上下 {candidate.Aim.Pitch:F2}° / 水平 {candidate.Aim.Yaw:F2}°；{candidate.Aim.Instruction}，{RouteLabel(candidate)}。");
        Say(p,$"方案基准点 {s.Start.Value.Feet.X:F1}, {s.Start.Value.Feet.Y:F1}, {s.Start.Value.Feet.Z:F1}"+
            (candidate.Aim.NeedsRunUp ? $"；当前在助跑起点，经过松键线执行。" : "；当前位置为该投法的起始站位。"));
        if(s.FlashMode) Say(p,"这是预测候选；不闪对象仅经过遮挡筛选，手投后逐个查看本次实测。");
        else Say(p,SceneAdvisor.Explain(s.Start.Value,s.Target!.Value,candidate,s.Result!.Matches));
        Say(p,ExecutionHint(candidate.Aim,start));
        return true;
    }
    private void VerifyCommand(CCSPlayerController? p,CommandInfo c)
    {
        if(!Check(p,out _,allowHeldGrenade:true)) return;
        Get(p!).FlightCheck=null;
        ArmVerification(p,c.ArgCount > 1 ? c.ArgByIndex(1) : "");
    }
    private bool ArmVerification(CCSPlayerController? p,string number)
    {
        // Recording does not move the player or switch weapons. A pin pulled
        // during the stable preparation window must not suppress measurement.
        if (!Check(p,out _,allowHeldGrenade:true) || Candidate(p!,number) is not { } candidate) return false;
        var s = Get(p!);
        if(p!.PlayerPawn.Value?.MoveType==MoveType_t.MOVETYPE_NOCLIP) {Say(p,"先从菜单退出飞行并落地，再准备手投记录。");return false;}
        if(s.FlashMode && !RecheckFlashEffects(p,s,candidate)) return false;
        SelectCandidate(p!,int.Parse(number,CultureInfo.InvariantCulture));
        if(s.FlightCheck is not {Check.Done:true,Check.Accepted:true} checkedFlight || !CurrentFlightCheck(p,s,checkedFlight))
        {s.Arm=null;BeginFlightCheck(p,s,candidate,true);return false;}
        s.Pending=null; s.PreparationIssue=null; s.CueStage=ReleaseStage.None; s.CueUntil=0;
        BindPreparation(p,s);
        s.Arm = new(candidate,s.Start!.Value,s.Target,s.Revision,Server.CurrentTime + 90,Physics:s.SearchPhysics ?? ProfileFor(s),EffectObjective:s.FlashMode);
        if(s.FlashMode && (s.FlashPlan is not { } plan || !BeginFlashVerification(p!,plan.Observers,plan.Pawns)))
        {s.Arm=null;return false;}
        s.ReleaseDelay=null; s.LastThrowTime=0;
        Say(p,$"已开始90秒手投记录：{candidate.Aim.Instruction}。实际出手点、角度、速度需匹配提示。");
        return true;
    }
    private void SaveCommand(CCSPlayerController? p,CommandInfo c)
        => SaveLineup(p,c.ArgCount>1 ? c.ArgByIndex(1) : "");
    private void SaveLineup(CCSPlayerController? p,string name)
    {
        if (!Check(p,out _)) return;
        var s = Get(p!);
        if (s.LastVerified == null || name.Length==0) { Say(p,"需要先完成动作匹配且命中的手投；可从 !nl 菜单保存。"); return; }
        if (name.Length is < 1 or > 48 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(".."))
        { Say(p,"名称应为 1..48 个字符，不含路径或非法文件名字符。"); return; }
        var path = Path.Combine(DataPath,"lineups",$"{name}-{Guid.NewGuid():N}.json");
        if (telemetry!.Write(path,s.LastVerified,false)) Say(p,"验证记录已排队保存：" + Path.GetFileName(path));
        else Say(p,"保存队列已满；请稍后重试。");
    }

    private void TickCore()
    {
        if (!Practice) { if (job != null || sessions.Count > 0) Reset(); return; }
        TickNativeControls();
        TickGrenadeFlights();
        TickFlashVerification();
        TickFlashSearch();
        HoldEffectPose();
        TickPreparedFlights();
        TickPreparationAndCues();
        TickReleaseCues();
        TickPlacementAndEffects();
        TickGrenadeBodies();
        TickEffectPreviewDrawing();
        TickPreviews();
        RenderHuds();
        // Snapshot actual grenade strength BEFORE release and copy values, not native wrappers.
        foreach (var pair in sessions)
        {
            if(pair.Value.Arm is null) continue;
            var p = Utilities.GetPlayerFromSlot(pair.Key);
            if (p?.IsValid != true || p.SteamID != pair.Value.SteamId || p.PlayerPawn.Value is not { IsValid:true } pawn) continue;
            if (pawn.WeaponServices?.ActiveWeapon.Value is { IsValid:true } weapon && weapon.DesignerName==pair.Value.Kind.Weapon())
            {
                var grenade = weapon.As<CBaseCSGrenade>();
                if(grenade.ThrowTime>Server.CurrentTime && grenade.ThrowTime!=pair.Value.LastThrowTime)
                {
                    pair.Value.LastThrowTime=grenade.ThrowTime;
                    pair.Value.ReleaseDelay=grenade.ThrowTime-Server.CurrentTime;
                }
                pair.Value.Snapshot = new(NativeWorld.FromNative(pawn.AbsOrigin!),pawn.ViewOffset.Z,
                    new(pawn.EyeAngles.X,pawn.EyeAngles.Y,grenade.ThrowStrength > .75f ? ThrowMode.Left : grenade.ThrowStrength > .25f ? ThrowMode.Both : ThrowMode.Right),grenade.ThrowStrength,
                    NativeWorld.FromNative(pawn.AbsVelocity),Standing(pawn),Server.CurrentTime,GroundedUpright(pawn),(pawn.Flags&1)!=0,grenade.JumpThrow);
            }
        }
        foreach (var id in flights.Keys.ToArray())
        {
            var f = flights[id];
            if (!f.Projectile.IsValid || Server.CurrentTime - f.StartTime > 20) { flights.Remove(id); continue; }
            if (f.Projectile.AbsOrigin is { } pos && f.Points.Count < 1280)
                f.Points.Add(new(Server.CurrentTime - f.StartTime,NativeWorld.FromNative(pos)));
        }
        if (job is not { } current) return;
        var owner=current.Slot<0 ? null : Utilities.GetPlayerFromSlot(current.Slot);
        Session session;
        if (current.ConsoleSession is { } consoleSession)
        {
            if(Utilities.GetPlayers().Any(player=>!player.IsBot)) {CancelSearch();return;}
            session=consoleSession;
        }
        else
        {
            if (owner?.IsValid != true || owner.SteamID != current.SteamId || owner.PlayerPawn.Value is not { IsValid:true } ownerPawn || ownerPawn.LifeState != 0 ||
                !sessions.TryGetValue(current.Slot,out session!) || session.Revision != current.Revision ||
                current.OwnerIdentity is { } identity && !identity.Matches(ActionIdentity(owner)))
            { CancelSearch(); return; }
        }
        try
        {
            if (Stopwatch.GetElapsedTime(current.StartedAt).TotalSeconds>=current.TimeLimit) current.Search.RequestFinish();
            var watch = Stopwatch.StartNew();
            string measuredPhase=current.Search.Phase;
            int units = 0;
            while (watch.Elapsed.TotalMilliseconds < searchBudgetMs && units++ < SearchUnitLimit && current.Search.Advance()) { }
            double elapsed = watch.Elapsed.TotalMilliseconds;
            current.TotalWorkMs += elapsed;
            current.PeakWorkMs = Math.Max(current.PeakWorkMs,elapsed);
            current.PhaseWork[measuredPhase]=current.PhaseWork.GetValueOrDefault(measuredPhase)+elapsed;
            PublishLiveSearch(current,session,owner);
            if(current.FirstRecommendationSeconds is null && current.Search.ReviewedRecommendedCount>0)
                current.FirstRecommendationSeconds=current.ActiveSeconds;
            if(current.FirstUsableSeconds is null && current.Search.ReviewedCandidateCount>0)
                current.FirstUsableSeconds=current.ActiveSeconds;
            if(!current.FirstCandidateAnnounced && current.Search.ReviewedCandidateCount>0)
            {
                current.FirstCandidateAnnounced=true;
                Say(owner,"已有可用路线，可采用或继续等待更多方案；较难投法也会列出。");
            }
            if (current.Search.Done)
            {
                var publishWatch=Stopwatch.StartNew();
                if(ContinueNearby(current,session,owner)) return;
                if(current.AutomaticExpansion && !SearchPresentation.HasUsable(session.Result) && !SearchPresentation.HasUsable(current.Search.Result) && ExpandJob(current,session,owner)) return;
                if(current.SearchedStart is { } searched && SearchPresentation.HasUsable(current.Search.Result))
                {
                    session.Start=searched;
                    Say(owner,"已找到附近安全站位的方案；按使用键自动到新站位并对准，无需重选目标。");
                }
                var raw=current.Search.Result!;
                RememberRoutes(current,session,raw);
                if(owner is null) session.Result=SearchPresentation.ForPlayer(raw);
                else MergeSearchResults(session,raw);
                var result=session.Result!;
                lastSearchDiagnostics = new(current.PriorTraceCount+current.World.TraceCount,current.PriorHitCount+current.World.HitCount,current.PriorSolidCount+current.World.SolidStartCount,
                    current.TotalWorkMs,current.PeakWorkMs);
                QueueSearchTelemetry(current,session,raw);
                if(owner is null || session.Selected<1) session.Selected = result.Matches.Count > 0 ? 1 : 0;
                session.NextHudAt = 0;
                Say(owner,$"已有{result.Matches.Count}种投法：{SearchPresentation.RecommendedCount(result)}种优先尝试，{SearchPresentation.LowToleranceCount(result)}种低容错；效果须手投验证。");
                for (int i = 0; owner is null && i < result.Matches.Count; i++)
                {
                    var m = result.Matches[i];
                    Say(owner,$"{i+1}: {m.Aim.Instruction}，pitch {m.Aim.Pitch:F2} / yaw {m.Aim.Yaw:F2}，误差 {m.Error:F1}，{RouteLabel(m)}，模拟扰动 {m.RobustHits}/{m.RobustTrials}");
                }
                if(result.Matches.Count==0)
                {
                    var explanation=SearchExplanation.For(raw,session.Target!.Value);
                    Say(owner,explanation.Summary+" "+explanation.NextStep);
                }
                if(owner is not null && current.Search.CanContinue)
                {current.CompletedSeconds+=Stopwatch.GetElapsedTime(current.StartedAt).TotalSeconds;current.Paused=true;session.Continuation?.Search.Dispose();session.Continuation=current;job=null;}
                else CancelSearch();
                // Background completion never reopens a menu or prepares a
                // different route while the player is already practicing.
                peakPublishMs=Math.Max(peakPublishMs,publishWatch.Elapsed.TotalMilliseconds);
            }
            else if (Server.CurrentTime - current.LastReport > 5)
            { current.LastReport = Server.CurrentTime; Say(owner,$"搜索 {current.Search.Phase}：已计算 {current.Search.Evaluated} 个候选。",feedback:false); }
        }
        catch (Exception ex) { Logger.LogError(ex,"NadeLab search failed"); Say(owner,"地图碰撞查询或搜索失败，请查看服务器日志；任务已停止。"); CancelSearch(); }
    }

    private HookResult OnThrown(EventGrenadeThrown e,GameEventInfo info)
    {
        var p = e.Userid;
        if(!loaded || !Practice || p?.IsValid!=true) return HookResult.Continue;
        var actualKind=Enum.GetValues<GrenadeKind>().Where(k=>e.Weapon==k.Weapon() || "weapon_"+e.Weapon==k.Weapon()).Cast<GrenadeKind?>().FirstOrDefault();
        if(actualKind is null) return HookResult.Continue;
        ObserveFlashVerificationRelease(p,actualKind.Value);
        recentGrenadeReleases.RemoveAll(r=>Server.CurrentTime-r.At>30);
        recentGrenadeReleases.Add(new(p.Slot,p.SteamID,actualKind.Value,Server.CurrentTime));
        if(sessions.TryGetValue(p.Slot,out var active))
        {
            SyncPreparationLifetime(p,active);
            active.FlightCheck=null;active.Pending=null;active.PreparationRevision++;
            active.LastEffect=null;
            if(actualKind!=active.Kind || (active.Arm is { } armed && (armed.Physics?.Kind ?? GrenadeKind.Smoke)!=actualKind))
            {active.Arm=null;active.Pending=null;active.Snapshot=null;active.LastVerified=null;active.SmokeReview=null;active.ThrowSequence++;ClearPreview(p.Slot);return HookResult.Continue;}
        }
        if(e.Weapon is not ("smokegrenade" or "weapon_smokegrenade")) return OnOtherThrown(e);
        if (!loaded || !Practice || p?.IsValid != true || e.Weapon is not ("smokegrenade" or "weapon_smokegrenade") ||
            !sessions.TryGetValue(p.Slot,out var s)) return HookResult.Continue;
        // Every smoke invalidates feedback on the previous smoke, including an
        // unarmed throw. Slow older flights still go to history, never to this UI.
        int sequence=++s.ThrowSequence;
        ClearPreview(p.Slot);
        s.LastVerified=null; s.SmokeReview=null;
        if(p.PlayerPawn.Value?.MoveType==MoveType_t.MOVETYPE_NOCLIP) {s.Arm=null;s.Pending=null;s.Snapshot=null;return HookResult.Continue;}
        if(s.Arm is not { } arm) return HookResult.Continue;
        s.Arm = null;
        if (flights.Count >= 16) { Say(p,"当前实投记录已达并发上限，请稍后重试。"); return HookResult.Continue; }
        if (Server.CurrentTime > arm.Expires || arm.Revision != s.Revision) { Say(p,"验证等待已过期，请重新 !nl_verify。"); return HookResult.Continue; }
        var snapshot = s.Snapshot;
        float? releaseDelay=s.ReleaseDelay;
        int slot = p.Slot; ulong steam = p.SteamID;
        float releaseTime = Server.CurrentTime;
        Server.NextFrame(() =>
        {
            if (!loaded || !Practice || !sessions.TryGetValue(slot,out var current) || current.SteamId != steam || current.Revision != arm.Revision) return;
            var owner = Utilities.GetPlayerFromSlot(slot);
            var pawn = owner?.PlayerPawn.Value;
            if (pawn?.IsValid != true) return;
            var projectile = Utilities.FindAllEntitiesByDesignerName<CSmokeGrenadeProjectile>("smokegrenade_projectile")
                .Where(g => g.IsValid && g.Thrower.Value?.Index == pawn.Index && g.CreateTime >= releaseTime - .1f && !flights.ContainsKey(g.Index))
                .OrderByDescending(g => g.CreateTime).FirstOrDefault();
            if (projectile == null) { Say(owner,"未找到对应投掷物，未记录验证结果。"); return; }
            bool matched = !arm.ObservationOnly && Verification.Matches(arm.Start,arm.Candidate.Aim,snapshot,releaseTime,arm.Physics ?? physics);
            if(matched && arm.Candidate.Aim.Airborne)
            {
                var launch=new SmokeSimulation(new NativeWorld(pawn),arm.Physics ?? physics,arm.Start,arm.Candidate.Aim,recordPoints:false);
                matched=Verification.MatchesJumpLaunch(launch.Position,launch.Velocity,NativeWorld.FromNative(projectile.InitialVelocity),
                    NativeWorld.FromNative(projectile.AbsOrigin!),1f/64);
            }
            bool guided=false;ThrowStart? guidedStart=null;Aim? guidedAim=null;
            if(!matched && !arm.ObservationOnly && Verification.TryGuidedSetup(arm.Start,arm.Candidate,snapshot,releaseTime,out var adjustedStart,out var adjustedAim,arm.Physics ?? physics))
            {
                var launch=new SmokeSimulation(new NativeWorld(pawn),arm.Physics ?? physics,adjustedStart,adjustedAim,recordPoints:false);
                guided=Verification.MatchesJumpLaunch(launch.Position,launch.Velocity,NativeWorld.FromNative(projectile.InitialVelocity),NativeWorld.FromNative(projectile.AbsOrigin!),1f/64,(arm.Physics ?? physics).Gravity);
                if(guided) {guidedStart=adjustedStart;guidedAim=adjustedAim;}
            }
            flights[projectile.Index] = new(projectile,slot,steam,releaseTime,arm,snapshot,matched,
                NativeWorld.FromNative(projectile.InitialVelocity),NativeWorld.FromNative(projectile.AbsOrigin!),sequence)
                {ReleaseDelay=releaseDelay,FirstObservedVelocity=NativeWorld.FromNative(projectile.AbsVelocity),GuidedMatched=guided,GuidedStart=guidedStart,GuidedAim=guidedAim,OwnerIsBot=owner!.IsBot};
            if(current.ThrowSequence==sequence) HudNotice(current,"烟雾已出手，正在记录实际飞行…",20);
        });
        return HookResult.Continue;
    }
    private HookResult OnSmoke(EventSmokegrenadeDetonate e,GameEventInfo info)
    {
        if (!flights.Remove((uint)e.Entityid,out var f)) return HookResult.Continue;
        if(f.Arm.EffectObjective || f.Arm.Target is not { } target) return HookResult.Continue;
        var actual = new Vector3(e.X,e.Y,e.Z);
        float targetError = Vector3.Distance(actual,target.Position);
        float? modelError = f.Arm.ObservationOnly ? null : Vector3.Distance(actual,f.Arm.Candidate.Trajectory.Endpoint);
        bool targetHit=target.Accepts(new NativeWorld(null),actual);
        bool success = f.Matched && targetHit;
        var executionIssues=f.Arm.ObservationOnly ? [] : Verification.ExplainMismatch(f.Arm.Start,f.Arm.Candidate.Aim,f.Snapshot,f.StartTime,f.Arm.Physics ?? physics).ToList();
        if(!f.Arm.ObservationOnly && !f.Matched && executionIssues.Count==0)
            executionIssues.Add("实际生成位置/初速度与模型不符，需校准");
        if(f.Matched && !targetHit && modelError>40 && !f.Arm.Candidate.Aim.Airborne)
        {
            failedLaunches.Add((f.Arm.Start,f.Arm.Candidate.Aim));
            if(failedLaunches.Count>128) failedLaunches.RemoveAt(0);
        }
        var record=new RecordedThrow(Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow.ToString("O"),Server.MapName,f.Arm.Physics ?? physics,worldVersion?.DeepClone() as JsonObject,
            f.Arm,f.Snapshot,f.InitialVelocity,f.FirstObservedPosition,f.FirstObservedVelocity,actual,Server.CurrentTime-f.StartTime,
            f.Matched,targetHit,targetError,modelError,f.ReleaseDelay,executionIssues.ToArray(),f.Points.ToArray(),GuidedMatched:f.GuidedMatched,GuidedStart:f.GuidedStart,GuidedAim:f.GuidedAim,
            PlayerSteamId:f.SteamId,PlayerIsBot:f.OwnerIsBot);
        bool queued=telemetry!.Write(Path.Combine(DataPath,"throws.jsonl"),()=>SerializeThrow(record));
        if(queued) RememberActual(record);
        var p = Utilities.GetPlayerFromSlot(f.Slot);
        if (p?.IsValid == true && p.SteamID == f.SteamId && sessions.TryGetValue(f.Slot,out var s) && s.Revision == f.Arm.Revision && s.ThrowSequence==f.Sequence)
        {
            s.LastVerified = success || record.GuidedLandingSuccess ? ()=>SerializeThrow(record) : null;
            s.SmokeReview=new(record,s.Revision,Server.CurrentTime,queued);
            string verdict=f.Arm.ObservationOnly ? targetHit ? "自由实投落点有效" : "自由实投落点无效" :
                f.GuidedMatched ? targetHit ? "按释放区完成手投，落点有效（独立记录）" : "按释放区完成手投但落点无效，需复核此区域" :
                !f.Matched ? string.Join("；",executionIssues.Take(2)) : success ? "落点有效，可从菜单保存" : "动作匹配但落点无效，已记录模型偏差";
            verdict+=queued ? "；记录已排队" : "；记录队列满，菜单可重试";
            string distanceLabel=target.IsArea ? $"{(targetHit ? "落在有效区域" : "落在区域外")} · 离中心 {targetError:F1}u" : $"实投误差 {targetError:F1}u";
            HudNotice(s,$"{distanceLabel} · {verdict}",15);
            Say(p,$"实投：{distanceLabel}；{verdict}。"+(target.IsArea ? "烟雾展开后，!nl 菜单可记录封满或漏缝。" : "")+(modelError is { } error ? $"{(f.GuidedMatched ? "与名义预测差" : "模型误差")} {error:F1}u。" : ""));
        }
        return HookResult.Continue;
    }
    private sealed class Session
    {
        public bool FlashMode;
        public FlashPlanState? FlashPlan;
        public GrenadeKind Kind; public PhysicsProfile? SearchPhysics; public EffectPose? EffectPose; public string? LastEffect;
        public PlacementDraft? Placement;
        public bool EffectEnabled,LastEffectValid,LastEffectArea;
        public float NextEffectAt,LastEffectBuildAt,FirePreviewAt;
        public Vector3? LastEffectCenter;
        public Vector3 FirePreviewCenter;
        public Vector3[]? FirePreviewCells;
        public int FirePreviewRevision,FirePreviewSequence;
        public int RegionSelection; public Vector3? ObjectiveCorner; public Vector3 ObjectiveNormal;
        public ulong SteamId; public int Revision; public ThrowStart? Start,RequestedStart; public Target? Target,TargetPreset,RegionDraft;
        public int ThrowSequence;
        public SearchJob? Continuation;
        public SearchResult? Result; public Arm? Arm; public ThrowObservation? Snapshot; public Func<string>? LastVerified; public SmokeReview? SmokeReview;
        public bool HudEnabled = true, HudVisible, PreviewEnabled, GuideEnabled=true, FlightPreviewEnabled=true;
        public int Selected;
        public float NextHudAt, HudNoticeUntil, NextReleaseCueAt;
        public string? HudNotice;
        public float NextFeedbackAt,TimingNoticeUntil;
        public bool FeedbackUrgent;
        public string? TimingNotice;
        public string? LastHudText,LastHudKeys;
        public float LastHudSentAt;
        public float LastThrowTime;
        public float? ReleaseDelay;
        public Aim? ReferenceView;
        public int PreparationRevision;
        public PreparationLifetime PreparationLifetime = new();
        public float NextPracticeButtonAt;
        public bool LegacyControls;
        public readonly MenuHold MenuHold=new();
        public uint MenuPawn;
        public int WheelUpEvents,WheelDownEvents,WheelMenuMoves;
        public PendingPreparation? Pending;
        public PreparationFlightCheck? FlightCheck;
        public string? PreparationIssue;
        public ReleaseStage CueStage;
        public float CueUntil;
    }
    private sealed record PendingPreparation(int Selected,int Revision,Vector3 Destination,float Since)
    {
        public PreparationState Stability {get;}=new();
    }
    private sealed record Arm(Candidate Candidate,ThrowStart Start,Target? Target,int Revision,float Expires,bool ObservationOnly=false,PhysicsProfile? Physics=null,bool EffectObjective=false);
    private sealed class SearchJob(int slot,ulong steamId,int revision,NativeWorld world,LineupSearch search,double timeLimit=15)
    {
        public int Slot=slot; public ulong SteamId=steamId; public int Revision=revision; public NativeWorld World=world;
        public LineupSearch Search=search; public float LastReport=Server.CurrentTime;
        public double TotalWorkMs,PeakWorkMs;
        public long StartedAt=Stopwatch.GetTimestamp();
        public long OverallStartedAt=Stopwatch.GetTimestamp();
        public double TimeLimit=timeLimit;
        public double CompletedSeconds;
        public bool Paused;
        public double ActiveSeconds=>CompletedSeconds+(Paused ? 0 : Stopwatch.GetElapsedTime(StartedAt).TotalSeconds);
        public long PriorTraceCount,PriorHitCount,PriorSolidCount;
        public bool AutomaticExpansion,Expanded;
        public double? FirstRecommendationSeconds;
        public double? FirstUsableSeconds;
        public string? BenchmarkCaseId,BenchmarkRunId,BenchmarkPassId;
        public bool FirstCandidateAnnounced;
        public int PublishedCount;
        public Session? ConsoleSession;
        public PlayerActionIdentity? OwnerIdentity;
        public Dictionary<string,double> PhaseWork=new();
        public ThrowStart? AlternativeStart,SearchedStart;
    }
    private sealed record Sample(float Time,Vector3 Position);
    private sealed class Flight(CSmokeGrenadeProjectile projectile,int slot,ulong steam,float startTime,Arm arm,ThrowObservation? snapshot,
        bool matched,Vector3 initialVelocity,Vector3 firstObservedPosition,int sequence)
    {
        public int Sequence=sequence;
        public bool GuidedMatched,OwnerIsBot;public ThrowStart? GuidedStart;public Aim? GuidedAim;
        public CSmokeGrenadeProjectile Projectile=projectile; public int Slot=slot; public ulong SteamId=steam;
        public float StartTime=startTime; public Arm Arm=arm; public ThrowObservation? Snapshot=snapshot; public bool Matched=matched;
        public Vector3 InitialVelocity=initialVelocity; public Vector3 FirstObservedPosition=firstObservedPosition;
        public Vector3 FirstObservedVelocity;
        public float? ReleaseDelay;
        public List<Sample> Points=[];
    }
}

