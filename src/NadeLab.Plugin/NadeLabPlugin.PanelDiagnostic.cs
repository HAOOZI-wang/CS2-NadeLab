using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Modules.Utils;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private bool panelCheckPending;
    private string? panelCheckReport;
    private void RegisterPanelDiagnostic()=>AddCommand("css_nl_panelcheck","Empty-server panel state and CustomHUD native checks",PanelDiagnostic);

    private void PanelDiagnostic(CCSPlayerController? caller,CommandInfo command)
    {
        if(caller is not null || !loaded || !Practice || Utilities.GetPlayers().Any(p=>!p.IsBot))
        {Say(caller,"PANELCHECK refused: server console, Mirage practice and zero humans required.");return;}
        if(command.ArgCount==2 && command.ArgByIndex(1)=="status")
        {
            string report=panelCheckPending ? "PANELCHECK pending" : panelCheckReport ?? "PANELCHECK not run in this plugin session";
            // The native console truncates a single long message. Keep each
            // diagnostic line separate so RCON can receive the final verdict.
            foreach(string line in report.Split('\n')) Say(null,line);
            return;
        }
        if(panelCheckPending || placementCheck is not null || effectStressCheck is not null || job is not null || flashJob is not null || flashSearchCheckPending || flashVerificationCheckPending || DummyLayoutLoadActive || flights.Count>0 || grenadeFlights.Count>0 || infernos.Count>0)
        {Say(null,"PANELCHECK refused: wait for active diagnostics/searches/throws.");return;}
        if(panelDisplayText.Count>0 || panelNoticeText.Count>0 || panelDummyText.Count>0 || panelCueText.Count>0 || panelFallbackSlots.Count>0 || panels.Count>0)
        {Say(null,"PANELCHECK refused: close existing panels/HUDs first; diagnostic does not replace active displays.");return;}
        var player=Utilities.GetPlayers().FirstOrDefault(p=>p.IsBot && p.IsValid && p.PlayerPawn.Value is {IsValid:true,LifeState:0} && !ThrowInProgress(p));
        if(player is null) {Say(null,"PANELCHECK refused: one existing living idle bot is required.");return;}
        sessions.TryGetValue(player.Slot,out var original);
        if(original?.Placement is not null || original?.Arm is not null || original?.Pending is not null || original?.Continuation is not null)
        {Say(null,"PANELCHECK refused: selected bot has active session state.");return;}
        panels.TryGetValue(player.Slot,out var originalPanel);
        panelMemory.TryGetValue(player.Slot,out var originalMemory);
        int epoch=grenadeEpoch;uint controller=player.EntityHandle.Raw,pawn=player.PlayerPawn.Raw;
        var sandbox=new Session {SteamId=player.SteamID,HudEnabled=false,Target=new(new(100,200,300),32)};
        var target=sandbox.Target;int passed=0,failed=0;var details=new List<string>();
        panelCheckReport=null;
        uint? nativeHandle=null;
        var persistentSpawnHandles=spawnMarkers.Select(m=>m.Handle).ToArray();
        int cleanupFailures=panelDisplayCleanupFailures;
        int acknowledgementsBefore=panelAcknowledged,deliveryFailuresBefore=panelDeliveryFailures;
        var observedOwners=new List<(int Slot,uint Controller,uint Pawn)> {(player.Slot,controller,pawn)};
        void CheckState(bool condition,string label)
        {if(condition) passed++;else failed++;details.Add($"{(condition ? "PASS" : "FAIL")}: {label}");}
        try
        {
            sessions[player.Slot]=sandbox;panels.Remove(player.Slot);panelMemory.Remove(player.Slot);
            int activated=0;bool backed=false;
            var menu=NewPanel("diagnostic","diagnostic/main");menu.IsRoot=true;
            menu.AddMenuOption("first",(who,option)=>activated++);
            menu.AddMenuOption("disabled",(who,option)=>activated+=100,true);
            menu.AddMenuOption("last",(who,option)=>activated++);
            OpenPanelCore(player,menu);
            CheckState(CurrentPanel(player,out var first) && first.Selected==0,"open selects first item");
            CheckState(persistentSpawnHandles.Length==(TrainingSpawns(false).Count+TrainingSpawns(true).Count)*5 && persistentSpawnHandles.Length>0 && spawnMarkerQueue.Count==0 && ungroundedSpawns.Count==0 && spawnMarkers.All(m=>m.Entity.IsValid),"all competitive ground spawn markers persist before menu opens");
            OpenPanelCore(player,menu);
            CheckState(persistentSpawnHandles.SequenceEqual(spawnMarkers.Select(m=>m.Handle)),"changing menu preserves shared spawn markers without recreation");
            ClosePanel(player);
            CheckState(persistentSpawnHandles.SequenceEqual(spawnMarkers.Select(m=>m.Handle)),"closing menu preserves shared spawn markers");
            OpenPanelCore(player,menu);CurrentPanel(player,out first);
            CheckState(PanelInputMatches(player,first,0),"queued input accepts the original panel and selected item");
            MovePanel(player,-1);
            CheckState(CurrentPanel(player,out var last) && last.Selected==2,"previous wraps to last item");
            CheckState(!PanelInputMatches(player,first,0),"queued input rejects a selection changed before execution");
            OpenPanelCore(player,menu);
            CheckState(CurrentPanel(player,out var remembered) && remembered.Selected==2,"replacement remembers the selected stable item");
            CheckState(!PanelInputMatches(player,first,0) && !PanelInputMatches(player,null,-1),"replacement panel rejects old panel input and previously queued workflow input");
            CurrentPanel(player,out var replacement);ClosePanel(player);
            CheckState(!PanelInputMatches(player,replacement,0) && PanelInputMatches(player,null,-1),"closed panel rejects stale confirmation while ordinary workflow focus is valid");
            OpenPanelCore(player,menu);MovePanel(player,-1);
            CheckState(CurrentPanel(player,out var disabled) && disabled.Selected==1 && sandbox.Target==target,"navigation preserves target");
            ActivatePanel(player);
            CheckState(activated==0 && HasPanel(player),"disabled action is not invoked");
            MovePanel(player,1);ActivatePanel(player);ActivatePanel(player);
            CheckState(activated==1 && !HasPanel(player) && sandbox.Target==target,"confirmation consumes panel once without changing target");
            OpenPanelCore(player,menu);BackPanel(player);
            CheckState(!HasPanel(player),"root back closes panel");
            var child=NewPanel("child","diagnostic/child");child.Back=_=>backed=true;OpenPanelCore(player,child);BackPanel(player);
            CheckState(backed && !HasPanel(player),"child back invokes explicit parent callback");
            bool available=false;int adopted=0;
            var growing=NewPanel("live search","diagnostic/live");
            growing.AddMenuOption("adopt",(_,_)=>adopted++,unavailable:()=>!available);
            OpenPanelCore(player,growing);CurrentPanel(player,out var waiting);
            ActivatePanel(player);
            CheckState(adopted==0 && ReferenceEquals(panels[player.Slot],waiting),"empty live result keeps action disabled in the current panel");
            available=true;
            CheckState(!growing.Options[0].IsDisabled && ReferenceEquals(panels[player.Slot],waiting),"new result enables the existing panel without replacing its input identity");
            ActivatePanel(player);
            CheckState(adopted==1 && !HasPanel(player),"newly enabled action executes without reopening the menu");
            var oldCandidate=new Candidate(new(-25,0,ThrowMode.Left),new(new(100,0,2),SimulationStatus.Rested,3,0,[]),0,19,19);
            var newCandidate=oldCandidate with {Aim=new(-30,10,ThrowMode.Left)};
            var practiceSession=new Session {Result=new("diagnostic",false,1,false,[oldCandidate],[]),Selected=1};
            practiceSession.Arm=new(oldCandidate,new(new(0,0,0)),null,0,Server.CurrentTime+90);
            practiceSession.Pending=new(1,0,new(0,0,0),Server.CurrentTime);
            var oldArm=practiceSession.Arm;var oldPending=practiceSession.Pending;
            MergeSearchResults(practiceSession,new("diagnostic",false,2,false,[newCandidate],[]));
            CheckState(practiceSession.Result.Matches.Count==2 && practiceSession.Selected==1 && ReferenceEquals(practiceSession.Result.Matches[0],oldCandidate) &&
                ReferenceEquals(practiceSession.Arm,oldArm) && ReferenceEquals(practiceSession.Pending,oldPending),"live append preserves selected route, armed throw and pending preparation");
            sandbox.Result=practiceSession.Result;sandbox.Start=new(new(0,0,0));sandbox.SearchPhysics=new();sandbox.Selected=1;
            var checking=new PreparationFlightCheck(ActionIdentity(player),sandbox.Revision,sandbox.PreparationRevision,1,sandbox.Start.Value,sandbox.SearchPhysics,sandbox.Target,oldCandidate,
                new(new AnalyticWorld(),sandbox.SearchPhysics,sandbox.Start.Value,oldCandidate,sandbox.Target),Server.CurrentTime,true);
            CheckState(CurrentFlightCheck(player,sandbox,checking),"fresh flight check binds to the current preparation generation");
            sandbox.PreparationRevision++;sandbox.FlightCheck=checking;sandbox.Arm=oldArm;sandbox.Pending=oldPending;
            TickPreparedFlights();
            CheckState(sandbox.FlightCheck is null && ReferenceEquals(sandbox.Arm,oldArm) && ReferenceEquals(sandbox.Pending,oldPending),"cancelled flight callback cannot clear or replace a newer preparation/recording");
            sandbox.Arm=null;sandbox.Pending=null;sandbox.Result=null;sandbox.Start=null;sandbox.SearchPhysics=null;sandbox.Selected=0;
            OpenPanelCore(player,menu);sandbox.Revision++;
            CheckState(!CurrentPanel(player,out _) && !panels.ContainsKey(player.Slot),"old revision closes panel");
            var identity=ActionIdentity(player);
            panels[player.Slot]=new(menu,identity with {Pawn=pawn^1u},sandbox.Revision);
            CheckState(!CurrentPanel(player,out _) && !panels.ContainsKey(player.Slot),"old pawn identity closes panel without replacing native pawn");
            panels[player.Slot]=new(menu,identity with {Epoch=epoch+1},sandbox.Revision);
            CheckState(!CurrentPanel(player,out _),"old map epoch closes panel");
            panels[player.Slot]=new(menu,identity with {Session=new object()},sandbox.Revision);
            CheckState(!CurrentPanel(player,out _),"old session identity closes panel");
            var text=NewPanel("pages","diagnostic/pages");text.TextPages=["first","second"];OpenPanelCore(player,text);MovePanel(player,1);
            CheckState(CurrentPanel(player,out var page) && page.Selected==1 && sandbox.Target==target,"detail page navigation preserves target");
            CheckState(player.EntityHandle.Raw==controller && player.PlayerPawn.Raw==pawn && grenadeEpoch==epoch,"diagnostic leaves native identity and epoch unchanged");

            CheckState(PanelResourceInstalled,"compiled layout is installed on the server (not proof of client installation)");
            if(!PanelResourceInstalled) throw new InvalidOperationException("Install compiled resources on the server before native CustomHUD checks.");
            int progressCount=1;
            var separated=NewPanel("操作区验证","diagnostic/feedback");separated.IsRoot=true;
            separated.Feedback=()=>$"已检查{progressCount}种";
            separated.AddMenuOption("执行操作",(_,_)=>{},hint:"此操作只影响当前组合");
            separated.AddMenuOption("查看进度",(_,_)=>{});
            OpenPanelCore(player,separated);sandbox.HudNotice=null;
            RenderPanel(player,sandbox,Server.CurrentTime);
            string commandText=panelDisplayText[player.Slot].Text;
            HudNotice(sandbox,"保存成功",5);RenderNotice(player,sandbox,Server.CurrentTime);
            CheckState(panelDisplayText[player.Slot].Text==commandText && !commandText.Contains("保存成功") &&
                panelNoticeText[player.Slot].Text.Contains("保存成功") && panelNoticeText[player.Slot].Text.Contains("此操作只影响当前组合"),
                "transient feedback and selected option context share only the notice channel; commands stay unchanged");
            sandbox.HudNotice=null;MovePanel(player,1);RenderNotice(player,sandbox,Server.CurrentTime);
            CheckState(panelNoticeText[player.Slot].Text.Contains("已检查1种"),"selected option without a hint uses menu feedback");
            progressCount=2;sandbox.FeedbackUrgent=true;RenderNotice(player,sandbox,Server.CurrentTime);
            CheckState(CurrentPanel(player,out var live) && ReferenceEquals(live.Menu,separated) && live.Selected==1 &&
                panelNoticeText[player.Slot].Text.Contains("已检查2种") && panelDisplayText[player.Slot].Text==commandText,
                "live progress updates independently without replacing menu or selection");
            ClosePanel(player);sandbox.PreparationIssue="需要站稳";HudNotice(sandbox,"旧成功消息",5);
            RenderNotice(player,sandbox,Server.CurrentTime);
            CheckState(panelNoticeText[player.Slot].Text.Contains("未就绪") && !panelNoticeText[player.Slot].Text.Contains("旧成功消息"),
                "unmet preparation condition replaces stale success feedback outside menus");
            sandbox.PreparationIssue=null;sandbox.HudNotice=null;
            // Use the throwing custom-only route, not SendPanelText's fallback:
            // a successful plain center message cannot pass this native test.
            ClearPanelDisplays();
            const string nativeText="NadeLab 原生面板验证\n烟雾 / 闪光 / HE · E确认 R返回";
            int before=panelDisplayWrites;
            SendCustomPanelText(player,nativeText);
            var layout=ExistingPanelLayout() ?? throw new InvalidOperationException("CustomHUD handle was not retained.");
            nativeHandle=layout.EntityHandle.Raw;
            CheckState(layout.IsValid && layout.DesignerName=="custom_hud_layout" && layout.StrLayout==PanelLayoutResource && !layout.Observable,
                $"native entity/compiled layout/observable fields agree (handle={nativeHandle})");
            CheckState(panelDisplayWrites==before+1 && panelDisplayText.TryGetValue(player.Slot,out var firstWrite) &&
                firstWrite.Controller==controller && firstWrite.Text==nativeText && firstWrite.Keys=="","first owner text invokes native setters once and caches exact bounded plain text with an empty footer");
            CheckState(!layout.IsInputCaptureEnabled(player),"native input capture remains disabled");
            SendCustomPanelText(player,nativeText);
            CheckState(panelDisplayWrites==before+1 && ExistingPanelLayout()?.EntityHandle.Raw==nativeHandle,"identical owner/text reuses entity with no duplicate writes");
            SendCustomPanelText(player,nativeText+"\n更新");
            CheckState(panelDisplayWrites==before+2 && ExistingPanelLayout()?.EntityHandle.Raw==nativeHandle,"changed text updates the same native entity once");
            var other=Utilities.GetPlayers().FirstOrDefault(p=>p.Slot!=player.Slot && p.IsBot && p.IsValid &&
                p.PlayerPawn.Value is {IsValid:true,LifeState:0} && !ThrowInProgress(p) &&
                (!sessions.TryGetValue(p.Slot,out var candidate) || (candidate.Placement is null && candidate.Pending is null && candidate.Arm is null)));
            if(other is not null)
            {
                observedOwners.Add((other.Slot,other.EntityHandle.Raw,other.PlayerPawn.Raw));
                SendCustomPanelText(other,"第二槽位独立文字");
                SendCustomPanelText(other,"第二槽位独立文字");
                CheckState(panelDisplayWrites==before+3 && panelDisplayText.Count==2 &&
                    panelDisplayText[player.Slot].Text==nativeText+"\n更新" && panelDisplayText[other.Slot].Text=="第二槽位独立文字" &&
                    !layout.IsInputCaptureEnabled(other),"two owner slots retain independent text/cache and deduplicate independently (native pixel isolation unverified)");
            }
            else details.Add("INFO: one idle bot only; second-slot comparison skipped, no bot was created.");
            int shortcutWrites=panelDisplayWrites;
            const string initialKeys="E确定 · R返回\nV飞行 · F1关闭";
            const string changedKeys="E确定 · R关闭\nV飞行 · F1关闭";
            SendCustomPanelText(player,nativeText+"\n更新",keys:initialKeys);
            var oldKeysDelivery=panelLatestDelivery[(player.Slot,PanelChannel.Main)];
            CheckState(panelDisplayWrites==shortcutWrites+1 && panelDisplayText[player.Slot].Text==nativeText+"\n更新" &&
                panelDisplayText[player.Slot].Keys==initialKeys && oldKeysDelivery.Delivery.Keys==initialKeys,
                "shortcut-only change updates the main delivery and cache without replacing body text");
            SendCustomPanelText(player,nativeText+"\n更新",keys:initialKeys);
            CheckState(panelDisplayWrites==shortcutWrites+1,"identical body and shortcuts are deduplicated together");
            SendCustomPanelText(player,nativeText+"\n更新",keys:changedKeys);
            var newKeysDelivery=panelLatestDelivery[(player.Slot,PanelChannel.Main)];
            CheckState(panelDisplayWrites==shortcutWrites+2 && panelDisplayText[player.Slot].Keys==changedKeys &&
                !CurrentPanelDelivery(oldKeysDelivery.Sequence,oldKeysDelivery.Delivery,out _,out _) &&
                !CurrentPanelDelivery(newKeysDelivery.Sequence,newKeysDelivery.Delivery with {Keys=initialKeys},out _,out _) &&
                CurrentPanelDelivery(newKeysDelivery.Sequence,newKeysDelivery.Delivery,out _,out _),
                "retries accept only the current body and shortcut pair, rejecting stale footer content");
            string fallback=PanelFallbackText(nativeText+"\n更新",changedKeys);
            CheckState(fallback.EndsWith(changedKeys) && System.Text.Encoding.UTF8.GetByteCount(fallback)<=480 && fallback.Count(c=>c=='\n')<=5,
                "plain center fallback retains all shortcuts within the original network and line budget");
            SendCustomPanelText(player,"独立反馈：假人受到伤害",channel:PanelChannel.Notice);
            CheckState(panelNoticeText[player.Slot].Text=="独立反馈：假人受到伤害" && panelNoticeText[player.Slot].Keys=="" &&
                panelDisplayText[player.Slot].Text==nativeText+"\n更新" && panelDisplayText[player.Slot].Keys==changedKeys,
                "notification channel does not replace the controls text");
            int separateWrites=panelDisplayWrites;
            SendCustomPanelText(player,"独立反馈：假人受到伤害",channel:PanelChannel.Notice);
            CheckState(panelDisplayWrites==separateWrites,"unchanged notification is independently deduplicated");
            OpenPanelCore(player,menu);sandbox.HudNotice="动作反馈不应替换菜单";sandbox.HudNoticeUntil=Server.CurrentTime+5;
            RenderPanel(player,sandbox,Server.CurrentTime+2);
            CheckState(panelDisplayText[player.Slot].Text.Contains("first") && !panelDisplayText[player.Slot].Text.Contains(sandbox.HudNotice) &&
                !panelDisplayText[player.Slot].Text.Contains("F1") && panelDisplayText[player.Slot].Keys.Contains("请从启动器连接输入模块") &&
                !panelDisplayText[player.Slot].Keys.Contains("F1"),
                "active feedback preserves menu choices and native action footer");
            sandbox.LegacyControls=true;
            RenderPanel(player,sandbox,Server.CurrentTime+4);
            CheckState(panelDisplayText[player.Slot].Text.Contains("first") && panelDisplayText[player.Slot].Keys.Contains("F1"),
                "explicit legacy mode retains its own shortcut footer");
            sandbox.LegacyControls=false;
            OpenPanelCore(player,InputCheckMenu());
            CheckState(RenderInputCheckNotice(player) && panelNoticeText[player.Slot].Text.Contains("输入模块未连接") &&
                panelNoticeText[player.Slot].Text.Contains("请用启动器") && !panelNoticeText[player.Slot].Text.Contains("Shift+"),
                "native input diagnostic truthfully requests the missing companion without obsolete Shift navigation");
            var visibleMain=panelLatestDelivery[(player.Slot,PanelChannel.Main)];
            ClosePanel(player);
            var hiddenMain=panelLatestDelivery[(player.Slot,PanelChannel.Main)];
            CheckState(!hiddenMain.Delivery.Show && hiddenMain.Delivery.Text=="" && hiddenMain.Delivery.Keys=="" &&
                !panelDisplayText.ContainsKey(player.Slot) && CurrentPanelDelivery(hiddenMain.Sequence,hiddenMain.Delivery,out _,out _) &&
                !CurrentPanelDelivery(visibleMain.Sequence,visibleMain.Delivery,out _,out _),
                "closing the main window clears body and shortcuts together and supersedes visible retries");
            CheckState(panelNoticeText.ContainsKey(player.Slot),"closing controls preserves the independent notification");
            ClearPanelChannel(player.Slot,PanelChannel.Notice);
            CheckState(!panelNoticeText.ContainsKey(player.Slot),"notification can expire independently");
            var hidden=panelLatestDelivery[(player.Slot,PanelChannel.Notice)];
            CheckState(!hidden.Delivery.Show && panelDeliveries.ContainsKey(hidden.Sequence) &&
                CurrentPanelDelivery(hidden.Sequence,hidden.Delivery,out _,out _),"hidden notification remains pending and retryable without visible-text cache");
            SendCustomPanelText(player,"更新后的通知",channel:PanelChannel.Notice);
            CheckState(!CurrentPanelDelivery(hidden.Sequence,hidden.Delivery,out _,out _),"superseded hide cannot retry over a newer notification");
            var latestNotice=panelLatestDelivery[(player.Slot,PanelChannel.Notice)];
            CheckState(!CurrentPanelDelivery(latestNotice.Sequence,latestNotice.Delivery with {Pawn=pawn^1u},out _,out _) &&
                !CurrentPanelDelivery(latestNotice.Sequence,latestNotice.Delivery with {Epoch=epoch+1},out _,out _) &&
                !CurrentPanelDelivery(latestNotice.Sequence,latestNotice.Delivery with {Layout=latestNotice.Delivery.Layout^1u},out _,out _),
                "notification retries reject stale pawn, map, and layout identities");
            ClearPanelChannel(player.Slot,PanelChannel.Notice);
            CheckState(!panelDisplayText.ContainsKey(player.Slot) && !panelFallbackSlots.Contains(player.Slot) &&
                !sandbox.HudVisible && !HasPanel(player) && !layout.IsInputCaptureEnabled(player),"closing panel clears owner cache/capture even with ordinary HUD disabled");
            int closedWrites=panelDisplayWrites;
            SendCustomPanelText(player,nativeText+"\n更新");
            CheckState(panelDisplayWrites==closedWrites+1 && ExistingPanelLayout()?.EntityHandle.Raw==nativeHandle &&
                panelDisplayText[player.Slot].Keys=="" && !CurrentPanelDelivery(hiddenMain.Sequence,hiddenMain.Delivery,out _,out _),
                "reopening plain text clears old shortcuts and supersedes the prior hide without recreating entity");
            string roster=DummyRoster.Format(Enumerable.Range(0,DummyRoster.PlayerLimit)
                .Select(i=>new DummyRosterItem(i,i==7,100,100,0,0,Prediction:"预闪0.0–6.0s")))!;
            SendCustomPanelText(player,roster,PanelChannel.Dummies);
            CheckState(panelDummyText[player.Slot].Text==roster && panelDisplayText[player.Slot].Text==nativeText+"\n更新",
                "all eight targets and predictions occupy an independent third native channel");
            int rosterWrites=panelDisplayWrites;
            SendCustomPanelText(player,roster,PanelChannel.Dummies);
            CheckState(panelDisplayWrites==rosterWrites,"unchanged multi-target roster is deduplicated");
            ClearPanelChannel(player.Slot,PanelChannel.Notice);ClearPanelMain(player.Slot);
            CheckState(panelDummyText.ContainsKey(player.Slot),"closing menu and expiring notices preserves the full target roster");
            ClearPanelChannel(player.Slot,PanelChannel.Dummies);
            CheckState(!panelDummyText.ContainsKey(player.Slot) && !panelLatestDelivery[(player.Slot,PanelChannel.Dummies)].Delivery.Show,
                "removing the last target can hide only its third HUD channel");
            SendCustomPanelText(player,"W+Shift → 松鼠标\n[■■□□] 50u · 准备",PanelChannel.Cue);
            int cueWrites=panelDisplayWrites;
            SendCustomPanelText(player,"W+Shift → 松鼠标\n[■■□□] 50u · 准备",PanelChannel.Cue);
            CheckState(panelDisplayWrites==cueWrites && panelCueText.ContainsKey(player.Slot),"separate release cue deduplicates unchanged content");
            panelCueText.Remove(player.Slot); // Native write may have succeeded despite missing acknowledgement.
            ClearPanelChannel(player.Slot,PanelChannel.Cue);
            CheckState(!panelLatestDelivery[(player.Slot,PanelChannel.Cue)].Delivery.Show,"cue hides uncertain native visibility after its text cache is lost");
            details.Add("INFO: "+PanelDisplayDiagnostics);
        }
        catch(Exception ex) {failed++;details.Add($"FAIL: {ex.GetType().Name}: {ex.Message}");}
        finally
        {
            if(grenadeEpoch==epoch && (nativeHandle is null || failed>0))
            {
                ClearPanelDisplays();
                CheckState(panelLayout is null && panelDisplayText.Count==0 && panelFallbackSlots.Count==0 && panelDisplayWrites==0,
                    "global clear forgets all display owners/handles/writes");
                CheckState(panelDisplayCleanupFailures==cleanupFailures,"native clear completed without a caught cleanup error");
            }
            if(grenadeEpoch==epoch && player.IsValid && player.EntityHandle.Raw==controller && player.PlayerPawn.Raw==pawn &&
                sessions.TryGetValue(player.Slot,out var active) && ReferenceEquals(active,sandbox))
            {
                
                panels.Remove(player.Slot);
                if(original is not null) sessions[player.Slot]=original;else sessions.Remove(player.Slot);
                if(originalPanel is not null) {panels[player.Slot]=originalPanel;}
                if(originalMemory is not null) panelMemory[player.Slot]=originalMemory;else panelMemory.Remove(player.Slot);
            }
        }
        void Report()
        {
            panelCheckReport=string.Join("\n",details.Select(detail=>"PANELCHECK DETAIL "+detail))+
                $"\nPANELCHECK {(failed==0 ? "PASS" : "FAIL")}: passed={passed}; failed={failed}; native state only; client pixels and input unverified.";
            foreach(var detail in details) Say(null,"PANELCHECK DETAIL "+detail);
            Say(null,$"PANELCHECK {(failed==0 ? "PASS" : "FAIL")}: passed={passed}; failed={failed}; native entity/API and managed write/cache checks only; client pixels, client text values, per-viewer visibility and key bindings unverified.");
        }
        if(nativeHandle is not { } removed || failed>0) {Report();return;}
        // UTIL_Remove is deferred. Inspect only the captured serial-bearing
        // handle later; normal HUD work may create a new entity in the meantime.
        panelCheckPending=true;
        try
        {
            AddTimer(.5f,()=>
            {
                try
                {
                    bool guard=loaded && Practice && grenadeEpoch==epoch && !Utilities.GetPlayers().Any(p=>!p.IsBot) &&
                        observedOwners.All(o=>Utilities.GetPlayerFromSlot(o.Slot) is {IsValid:true} owner && owner.IsBot &&
                            owner.EntityHandle.Raw==o.Controller && owner.PlayerPawn.Raw==o.Pawn);
                    if(!guard) {CheckState(false,"deferred native check aborted: world/zero-human/pawn guard changed");panelCheckPending=false;Report();return;}
                    CheckState(panelDeliveries.Count==0 && panelAcknowledged>acknowledgementsBefore && panelDeliveryFailures==deliveryFailuresBefore,
                        $"official cs_script acknowledged all text/visibility updates (completed={panelAcknowledged-acknowledgementsBefore}, pending={panelDeliveries.Count})");
                    CheckState(ExistingPanelLayout() is {IsValid:true} native && observedOwners.All(o=>!native.IsInputCaptureEnabled(Utilities.GetPlayerFromSlot(o.Slot)!)),
                        "native input capture remains disabled after actual script delivery");
                    var removedEntities=new uint?[]{removed,panelScript?.Raw,panelInbox?.Raw}.OfType<uint>().ToArray();
                    ClearPanelDisplays();
                    CheckState(panelLayout is null && panelDisplayText.Count==0 && panelNoticeText.Count==0 && panelDummyText.Count==0 && panelCueText.Count==0 && panelFallbackSlots.Count==0 && panelDisplayWrites==0 && panelDeliveries.Count==0,
                        "global clear forgets all displays and deliveries");
                    CheckState(panelDisplayCleanupFailures==cleanupFailures,"native clear completed without a caught cleanup error");
                    AddTimer(.15f,()=>
                    {
                        try
                        {
                            CheckState(grenadeEpoch==epoch && removedEntities.All(h=>new CHandle<CBaseEntity>(h).Value is not {IsValid:true}),
                                "deferred native removal completed for layout, script and inbox full handles");
                            CheckState(grenadeEpoch==epoch && persistentSpawnHandles.All(h=>new CHandle<CBaseEntity>(h).Value is {IsValid:true}),
                                "clearing menu HUD does not remove persistent ground spawn markers");
                        }
                        catch(Exception ex) {CheckState(false,"deferred native removal: "+ex.Message);}
                        finally {panelCheckPending=false;Report();}
                    });
                }
                catch(Exception ex) {CheckState(false,"deferred cleanup: "+ex.GetType().Name+": "+ex.Message);ClearPanelDisplays();panelCheckPending=false;Report();}
            });
        }
        catch(Exception ex) {panelCheckPending=false;CheckState(false,"cleanup timer: "+ex.Message);Report();}
    }
}
