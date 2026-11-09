using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private sealed class PlacementCheckRun
    {
        public required int Slot,Epoch;
        public required uint Controller,Pawn;
        public required ulong Steam;
        public required Vector3 Feet,Velocity;
        public required Aim View;
        public required MoveType_t Move,ActualMove;
        public required float Attack;
        public required bool BotActive;
        public bool? AiActive;
        public uint? Weapon;
        public Session? OriginalSession;
        public required Session Sandbox;
        public Vector3 Base,Air,Marker;
        public float TestAttack;
        public Target SentinelTarget;
        public ThrowStart SentinelStart;
        public ThrowStart? ConfirmedStart;
        public Target? ConfirmedFlash;
        public GrenadeBodyState? LastBody;
        public readonly Dictionary<uint,string> RemovedCameraHandles=[];
        public CBeam[] Beams=[];
        public uint[] BeamHandles=[];
        public long CreatedBeforeMove;
        public int WaitingStep=-1;
        public float WaitingSince;
        public int Passed,Failed;
        public readonly List<string> Details=[];
    }
    private PlacementCheckRun? placementCheck;

    private void RegisterPlacementDiagnostic()
    {
        AddCommand("css_nl_placementcheck","Empty-server placement and beam reuse diagnostic",PlacementDiagnostic);
        RegisterListener<Listeners.OnMapEnd>(()=>
        {if(placementCheck is { } run) FinishPlacementDiagnostic(run,"map ended",false);});
        RegisterListener<Listeners.OnClientDisconnect>(slot=>
        {if(placementCheck is { } run && run.Slot==slot) FinishPlacementDiagnostic(run,"test bot disconnected",false);});
    }

    // Call before normal Reset/Unload cleanup if a diagnostic is still running.
    private void StopPlacementDiagnostic()
    {if(placementCheck is { } run) FinishPlacementDiagnostic(run,"plugin reset",true);}

    private void PlacementDiagnostic(CCSPlayerController? caller,CommandInfo command)
    {
        if(caller is not null || !Practice || Utilities.GetPlayers().Any(p=>!p.IsBot))
        {Say(caller,"PLACEMENTCHECK refused: server console, Mirage practice and zero humans required.");return;}
        if(placementCheck is not null || effectStressCheck is not null || job is not null || flashJob is not null || flashSearchCheckPending || flashVerificationCheckPending || DummyLayoutLoadActive || grenadeFlights.Count>0 || flights.Count>0 || infernos.Count>0)
        {Say(null,"PLACEMENTCHECK refused: wait for active diagnostics/searches/throws to finish.");return;}
        var owner=Utilities.GetPlayers().FirstOrDefault(p=>p.IsBot && p.IsValid && p.PlayerPawn.Value is {IsValid:true,LifeState:0} && !ThrowInProgress(p));
        if(owner?.PlayerPawn.Value is not {IsValid:true,LifeState:0} pawn)
        {Say(null,"PLACEMENTCHECK refused: one existing living idle bot is required; no bots are created by this command.");return;}
        sessions.TryGetValue(owner.Slot,out var original);
        if(original?.Placement is not null || original?.Continuation is not null || original?.Arm is not null || original?.Pending is not null)
        {Say(null,"PLACEMENTCHECK refused: selected bot has an active session operation.");return;}
        var run=new PlacementCheckRun
        {
            Slot=owner.Slot,Epoch=grenadeEpoch,Controller=owner.EntityHandle.Raw,Pawn=pawn.EntityHandle.Raw,Steam=owner.SteamID,
            Feet=NativeWorld.FromNative(pawn.AbsOrigin!),Velocity=NativeWorld.FromNative(pawn.AbsVelocity),
            View=new(pawn.EyeAngles.X,pawn.EyeAngles.Y,ThrowMode.Left),Move=pawn.MoveType,ActualMove=pawn.ActualMoveType,
            Attack=NextAttack(pawn),BotActive=pawn.BotAllowActive,AiActive=pawn.Bot?.AllowActive,
            Weapon=pawn.WeaponServices?.ActiveWeapon.Value is {IsValid:true} weapon ? weapon.EntityHandle.Raw : null,
            OriginalSession=original,Sandbox=new Session {SteamId=owner.SteamID,HudEnabled=false,GuideEnabled=false}
        };
        placementCheck=run;
        sessions[run.Slot]=run.Sandbox;
        Say(null,$"PLACEMENTCHECK START: bot slot={run.Slot}; pawn={run.Pawn}; epoch={run.Epoch}; native state only, not client rendering/transmit proof.");
        PlacementDiagnosticStep(run,0);
    }

    private void SchedulePlacementDiagnostic(PlacementCheckRun run,int step,float seconds)
    {AddTimer(seconds,()=>PlacementDiagnosticStep(run,step),TimerFlags.STOP_ON_MAPCHANGE);}

    private void PlacementDiagnosticStep(PlacementCheckRun run,int step)
    {
        if(!ReferenceEquals(placementCheck,run)) return;
        if(!loaded || !Practice || grenadeEpoch!=run.Epoch || Utilities.GetPlayers().Any(p=>!p.IsBot))
        {FinishPlacementDiagnostic(run,"practice/epoch/zero-human guard changed",true);return;}
        var owner=Utilities.GetPlayerFromSlot(run.Slot);
        if(owner is not {IsValid:true,IsBot:true} || owner.EntityHandle.Raw!=run.Controller || owner.SteamID!=run.Steam ||
            owner.PlayerPawn.Value is not {IsValid:true,LifeState:0} pawn || pawn.EntityHandle.Raw!=run.Pawn ||
            !sessions.TryGetValue(run.Slot,out var current) || !ReferenceEquals(current,run.Sandbox))
        {FinishPlacementDiagnostic(run,"controller/pawn/session identity changed",true);return;}
        var s=run.Sandbox;
        void Require(bool condition,string detail)
        {if(!condition) throw new InvalidOperationException(detail);}
        void Check(bool condition,string detail)
        {if(condition) run.Passed++;else run.Failed++;run.Details.Add($"{(condition ? "PASS" : "FAIL")}: {detail}");}
        void Returned(string label)
        {
            Check(s.Placement is null && Vector3.Distance(NativeWorld.FromNative(pawn.AbsOrigin!),run.Base)<4,$"{label}: returned to saved feet and exited placement");
            Check(pawn.MoveType==MoveType_t.MOVETYPE_WALK && pawn.ActualMoveType==MoveType_t.MOVETYPE_WALK,$"{label}: WALK movement restored");
            Check(MathF.Abs(NextAttack(pawn)-run.TestAttack)<.01f,$"{label}: saved future NextAttack restored");
            BodyRestored(label);
        }
        void BodyRestored(string label)
        {
            Require(run.LastBody is not null,"body restoration check has no captured entry state");
            var body=run.LastBody!;
            // UTIL_Remove queues native destruction. The managed owner must
            // disappear now; verify each full native handle after later ticks.
            run.RemovedCameraHandles.TryAdd(body.CameraHandle,label);
            Check(!grenadeBodies.ContainsKey(run.Slot),$"{label}: managed body state removed immediately");
            Check(GuardGrenadeBodyNoclip(owner)==HookResult.Continue,$"{label}: native V/noclip is available again outside placement");
            Check(pawn.CBodyComponent?.SceneNode?.GetSkeletonInstance().ModelState.ModelName==body.Model &&
                Vector3.Distance(new(pawn.ViewOffset.X,pawn.ViewOffset.Y,pawn.ViewOffset.Z),body.ViewOffset)<.01f &&
                pawn.CameraServices?.ViewEntity.Raw==body.ViewEntity,$"{label}: original model, eye offset and view entity restored");
            Check(pawn.WeaponServices is { } restored && restored.PreventWeaponPickup==body.PreventPickup &&
                restored.As<CCSPlayer_WeaponServices>().DisableAutoDeploy==body.DisableAutoDeploy &&
                (body.ActiveWeapon==uint.MaxValue || restored.ActiveWeapon.Raw==body.ActiveWeapon),
                $"{label}: original held weapon, pickup and auto-deploy state restored");
            Check(body.Weapons.Values.All(saved=>new CHandle<CBasePlayerWeapon>(saved.Handle).Value is {IsValid:true} weapon &&
                BodyOwnsWeapon(body,weapon) && weapon.RenderMode==saved.RenderMode && weapon.Render==saved.Render && weapon.Effects==saved.Effects),
                $"{label}: exact inventory handles and original weapon rendering retained");
        }
        void BodyEntered(GrenadeKind kind)
        {
            Require(grenadeBodies.TryGetValue(run.Slot,out var body),"grenade body state was not created");
            run.LastBody=body;
            Check(body!.Kind==kind && body.Camera.IsValid && pawn.CameraServices?.ViewEntity.Raw==body.Camera.EntityHandle.Raw,
                $"{kind}: dedicated native camera selected");
            Check(body.Camera.DesignerName==BodyCameraClass && (body.Camera.Spawnflags&BodyCameraSpawnFlags)==BodyCameraSpawnFlags,
                $"{kind}: camera is a transmitted model-free info_target");
            Check(pawn.CBodyComponent?.SceneNode?.GetSkeletonInstance().ModelState.ModelName==(kind==GrenadeKind.Flash ? FlashBodyModel : HeBodyModel),
                $"{kind}: actual pawn model switched to grenade");
            var inventory=pawn.WeaponServices;
            Require(inventory is not null && body.Weapons.Count>0,"body diagnostic requires an equipped bot with inventory weapons");
            Check(inventory!.ActiveWeapon.Raw==uint.MaxValue && inventory.PreventWeaponPickup &&
                inventory.As<CCSPlayer_WeaponServices>().DisableAutoDeploy,
                $"{kind}: held weapon cleared and automatic deployment/pickup blocked");
            Check(inventory.MyWeapons.Where(h=>h.Value is {IsValid:true}).All(h=>body.Weapons.ContainsKey(h.Raw) &&
                h.Value!.RenderMode==RenderMode_t.kRenderNone && (h.Value.Effects&BodyHiddenBits)==BodyHiddenBits),
                $"{kind}: every carried gun/grenade retains its handle but is hidden");
            // Reproduce the old bug: deployment opens rendering for a weapon
            // already in the cache. The repair must run again without saving
            // the mutated rendering as its new restoration state.
            var savedWeapon=body.Weapons.Values.First();
            var changedWeapon=new CHandle<CBasePlayerWeapon>(savedWeapon.Handle).Value!;
            int originalClip=changedWeapon.Clip1;
            changedWeapon.RenderMode=RenderMode_t.kRenderNormal;changedWeapon.Effects&=~BodyHiddenBits;
            SetBodyActiveWeapon(inventory,savedWeapon.Handle);
            HideBodyWeapons(pawn,body);
            Check(inventory.ActiveWeapon.Raw==uint.MaxValue && changedWeapon.RenderMode==RenderMode_t.kRenderNone &&
                (changedWeapon.Effects&BodyHiddenBits)==BodyHiddenBits && body.Weapons[savedWeapon.Handle]==savedWeapon && changedWeapon.Clip1==originalClip,
                $"{kind}: switching/redeploying a cached weapon is repaired without losing ammo or restoration state");
            Check(!body.ThirdPerson && Vector3.Distance(NativeWorld.FromNative(body.Camera.AbsOrigin!),NativeWorld.FromNative(pawn.AbsOrigin!))<.01f,
                $"{kind}: initial first-person camera at grenade centre");
            var origin=NativeWorld.FromNative(pawn.AbsOrigin!);var before=PlacementPoint(pawn,kind);
            Check(GuardGrenadeBodyNoclip(owner)==HookResult.Handled,$"{kind}: V/noclip is blocked while placing a grenade");
            SetMovement(pawn,MoveType_t.MOVETYPE_WALK,MoveType_t.MOVETYPE_WALK);
            KeepGrenadeBodiesFlying();
            Check(pawn.MoveType==MoveType_t.MOVETYPE_NOCLIP && pawn.ActualMoveType==MoveType_t.MOVETYPE_NOCLIP &&
                NativeWorld.FromNative(pawn.AbsOrigin!)==origin,$"{kind}: external movement change is corrected without moving grenade centre");
            ToggleGrenadeBodyCameraCore(owner,pawn);
            Check(body.ThirdPerson && pawn.CameraServices?.ViewEntity.Raw==body.Camera.EntityHandle.Raw &&
                NativeWorld.FromNative(pawn.AbsOrigin!)==origin && PlacementPoint(pawn,kind)==before,
                $"{kind}: third-person switch preserves grenade position and target");
            ToggleGrenadeBodyCameraCore(owner,pawn);
            Check(!body.ThirdPerson && Vector3.Distance(NativeWorld.FromNative(body.Camera.AbsOrigin!),origin)<.01f,
                $"{kind}: camera returns to first-person centre");
            Check(inventory.ActiveWeapon.Raw==uint.MaxValue && body.Weapons.Keys.All(raw=>
                new CHandle<CBasePlayerWeapon>(raw).Value is {IsValid:true} held && held.RenderMode==RenderMode_t.kRenderNone),
                $"{kind}: camera toggles do not reveal held/holstered weapons");
            OpenPanelCore(owner,PlacementMenu(owner,s,s.Placement!));
            Check(CurrentPanel(owner,out var placementMenu) && placementMenu.Menu.AllowPlacement && !placementMenu.Menu.IsRoot && placementMenu.Menu.Back is not null &&
                placementMenu.Menu.Options.Any(o=>o.Text=="检查菜单按键"),
                $"{kind}: placement menu permits grenade form and offers input diagnostics");
            Check(placementMenu.Menu.Options.Any(o=>o.Text.Contains("假人")) && s.Placement is not null && grenadeBodies.ContainsKey(run.Slot),
                $"{kind}: placement menu includes dummy management without exiting grenade form");
            KeepGrenadeBodiesFlying();
            Check(pawn.MoveType==MoveType_t.MOVETYPE_NOCLIP && pawn.ActualMoveType==MoveType_t.MOVETYPE_NOCLIP &&
                grenadeBodies[run.Slot]==body && NativeWorld.FromNative(pawn.AbsOrigin!)==origin,
                $"{kind}: menu permits grenade movement without replacing body state or centre");
            ClosePanel(owner);
            Check(pawn.MoveType==MoveType_t.MOVETYPE_NOCLIP && pawn.ActualMoveType==MoveType_t.MOVETYPE_NOCLIP &&
                grenadeBodies[run.Slot]==body,
                $"{kind}: closing menu preserves grenade flight state");
        }
        void PreviewToggle(PlacementResult point)
        {
            Require(s.Placement is {Area:true},"preview toggle needs enabled placement");
            Require(effectPreviewScenes.TryGetValue(run.Slot,out var scene),"preview scene missing");
            var handles=scene!.Beams.Select(b=>b.Entity.EntityHandle.Raw).ToArray();
            Check(scene.Pulse && !scene.Area && scene.RangeSegments.Count==0 && scene.Beams.Count==3,
                $"{s.Kind}: preview uses three centre beams without damage-range rings");
            ToggleEffectPreviewCore(owner);TickPlacementAndEffects();TickEffectPreviewDrawing();
            Check(!s.Placement!.Area && !scene.Pulse && EffectMarkerReady(run.Slot,s.Kind,point) && VerifyNativeEffectSegments(scene),
                $"{s.Kind}: F4 off restores plain marker and retains confirmable centre");
            ToggleEffectPreviewCore(owner);TickPlacementAndEffects();TickEffectPreviewDrawing();
            Check(s.Placement.Area && scene.Pulse && EffectMarkerReady(run.Slot,s.Kind,point) &&
                scene.Beams.Select(b=>b.Entity.EntityHandle.Raw).SequenceEqual(handles),
                $"{s.Kind}: F4 on reuses marker entities without delaying confirmation");
        }
        try
        {
            switch(step)
            {
                case 0:
                {
                    // Bots still need their movement-command processing to
                    // acquire genuine ground/duck state after a teleport.
                    // bot_stop is supplied by the external empty-server test.
                    SetDiagnosticBotActive(pawn,true);
                    Require(TryStandingPosition(pawn,new Vector3(1200,-524,-168),out var anchor) || TryStandingPosition(pawn,run.Feet,out anchor),"no safe standing anchor for diagnostic");
                    run.Base=anchor;
                    run.Air=FindDiagnosticAir(pawn,anchor);
                    foreach(var kind in Enum.GetValues<GrenadeKind>())
                    {
                        var center=kind.Timed() ? run.Air+Vector3.UnitZ*64 : anchor+Vector3.UnitZ*2;
                        var segments=BuildEffectSegments(pawn,kind,center,true,true,true,kind.Fire() ? [center] : []).Where(x=>x.HasValue).Select(x=>x!.Value).ToArray();
                        Check(segments.Length is >=3 and <=EffectSegmentLimit && segments.All(x=>Geometry.Finite(x.A) && Geometry.Finite(x.B)),
                            $"{kind}: native effect geometry finite/bounded ({segments.Length} segments)");
                    }
                    run.TestAttack=Server.CurrentTime+60;
                    run.SentinelTarget=new(anchor+new Vector3(80,0,2),32);
                    run.SentinelStart=new(anchor+new Vector3(80,0,0),pawn.ViewOffset.Z);
                    s.Target=run.SentinelTarget;s.Start=run.SentinelStart;s.RequestedStart=s.Start;
                    s.Kind=GrenadeKind.Flash;
                    SetMovement(pawn,MoveType_t.MOVETYPE_WALK,MoveType_t.MOVETYPE_WALK);
                    pawn.Teleport(position:anchor,velocity:Vector3.Zero);SetDiagnosticView(pawn,new(0,0,ThrowMode.Left));
                    SetNextAttack(pawn,run.TestAttack);
                    SchedulePlacementDiagnostic(run,1,.4f);
                    break;
                }
                case 1:
                {
                    if(!DiagnosticStandingReady(run,step,owner,pawn)) break;
                    Say(null,"PLACEMENTCHECK ENTRY cancel: "+PlacementDiagnosticPose(owner,pawn));
                    SetNextAttack(pawn,run.TestAttack);
                    BeginPlacement(owner,pawn,s);
                    Require(s.Placement is not null,"cancel test could not enter placement: "+PlacementDiagnosticPose(owner,pawn));
                    BodyEntered(s.Kind);
                    SetDiagnosticBotActive(pawn,false);
                    var draft=s.Placement!;
                    Check(draft.Pawn==run.Pawn && draft.Epoch==run.Epoch && SamePlacementPawn(owner,draft,out _),"draft captures pawn identity and world epoch");
                    Check(MathF.Abs(draft.ReturnEyeHeight-run.LastBody!.ViewOffset.Z)<.01f,"draft captures original eye height before body transform");
                    var wrongPawn=new PlacementDraft(draft.Pawn^1u,draft.Epoch,draft.ReturnFeet,draft.ReturnEyeHeight,draft.ReturnView,draft.ReturnMove,draft.ReturnActualMove,draft.NextAttack,draft.StableStart,draft.Weapon);
                    var wrongEpoch=new PlacementDraft(draft.Pawn,draft.Epoch+1,draft.ReturnFeet,draft.ReturnEyeHeight,draft.ReturnView,draft.ReturnMove,draft.ReturnActualMove,draft.NextAttack,draft.StableStart,draft.Weapon);
                    Check(!SamePlacementPawn(owner,wrongPawn,out _) && !SamePlacementPawn(owner,wrongEpoch,out _),"stale pawn handle and stale epoch are rejected without moving the bot");
                    pawn.Teleport(position:run.Air,velocity:Vector3.Zero);
                    CompletePlacement(owner,pawn,s,false);
                    Check(s.Target is { } target && target.SameDefinition(run.SentinelTarget) && s.Start==run.SentinelStart,"cancel preserves target and throw start");
                    Returned("cancel");
                    Require(s.Placement is null,"cancel did not exit placement");
                    SetDiagnosticBotActive(pawn,true);
                    SchedulePlacementDiagnostic(run,2,.4f);
                    break;
                }
                case 2:
                    if(!DiagnosticStandingReady(run,step,owner,pawn)) break;
                    s.Kind=GrenadeKind.Flash;
                    Say(null,"PLACEMENTCHECK ENTRY flash: "+PlacementDiagnosticPose(owner,pawn));
                    SetNextAttack(pawn,run.TestAttack);
                    BeginPlacement(owner,pawn,s);
                    Require(s.Placement is not null,"flash test could not enter placement: "+PlacementDiagnosticPose(owner,pawn));
                    BodyEntered(s.Kind);
                    SetDiagnosticBotActive(pawn,false);
                    pawn.Teleport(position:run.Air,velocity:Vector3.Zero);SetDiagnosticView(pawn,new(0,0,ThrowMode.Left));
                    s.NextEffectAt=0;s.LastEffectCenter=null;
                    CompletePlacement(owner,pawn,s,true);
                    Check(s.Placement is not null && s.Target==run.SentinelTarget,"moving confirmation waits for the new published marker");
                    SchedulePlacementDiagnostic(run,3,1.25f);
                    break;
                case 3:
                {
                    var expected=PlacementPoint(pawn,GrenadeKind.Flash);
                    Require(expected.Valid && !EffectPlacement.Resolve(new NativeWorld(pawn),GrenadeKind.Smoke,NativeWorld.FromNative(pawn.AbsOrigin!)).Valid,"chosen air test point must allow flash and reject grounded smoke geometry");
                    Check(Vector3.Distance(expected.Position,NativeWorld.FromNative(pawn.AbsOrigin!))<.01f,"flash marker equals actual grenade AbsOrigin");
                    SetDiagnosticView(pawn,new(35,140,ThrowMode.Left));
                    Check(PlacementPoint(pawn,GrenadeKind.Flash)==expected,"changing view direction does not move flash target");
                    Require(EffectMarkerReady(run.Slot,GrenadeKind.Flash,expected),"flash marker not fully published after 1.25 seconds");
                    PreviewToggle(expected);
                    var entered=s.Placement!.StableStart;
                    CompletePlacement(owner,pawn,s,true);
                    Check(s.Target is {Airspace:true} target && Vector3.Distance(target.Position,expected.Position)<.01f,"flash confirms published airborne marker centre");
                    Check(entered is not null && s.Start==entered && s.RequestedStart==entered && s.Start!=run.SentinelStart,"walking entry replaces the old throw start with this entry position");
                    run.ConfirmedStart=s.Start;
                    Returned("flash confirm");
                    Require(s.Placement is null,"flash confirmation did not exit placement");
                    run.ConfirmedFlash=s.Target;
                    SetDiagnosticBotActive(pawn,true);
                    SchedulePlacementDiagnostic(run,4,.4f);
                    break;
                }
                case 4:
                    if(!DiagnosticStandingReady(run,step,owner,pawn)) break;
                    s.Kind=GrenadeKind.HighExplosive;
                    Say(null,"PLACEMENTCHECK ENTRY HE: "+PlacementDiagnosticPose(owner,pawn));
                    SetNextAttack(pawn,run.TestAttack);
                    BeginPlacement(owner,pawn,s);
                    Require(s.Placement is not null,"HE test could not enter placement: "+PlacementDiagnosticPose(owner,pawn));
                    BodyEntered(s.Kind);
                    SetDiagnosticBotActive(pawn,false);
                    pawn.Teleport(position:run.Air,velocity:Vector3.Zero);SetDiagnosticView(pawn,new(0,0,ThrowMode.Left));
                    s.NextEffectAt=0;s.LastEffectCenter=null;
                    SchedulePlacementDiagnostic(run,5,1.25f);
                    break;
                case 5:
                {
                    var expected=PlacementPoint(pawn,GrenadeKind.HighExplosive);
                    Require(expected.Valid && EffectMarkerReady(run.Slot,GrenadeKind.HighExplosive,expected),"HE marker not fully published");
                    PreviewToggle(expected);
                    Check(Vector3.Distance(expected.Position,NativeWorld.FromNative(pawn.AbsOrigin!))<.01f,"HE marker equals actual grenade AbsOrigin");
                    SetDiagnosticView(pawn,new(-35,-80,ThrowMode.Left));
                    Check(PlacementPoint(pawn,GrenadeKind.HighExplosive)==expected,"changing view direction does not move HE target");
                    CompletePlacement(owner,pawn,s,true);
                    Check(s.Target is {Airspace:true} target && Vector3.Distance(target.Position,expected.Position)<.01f,"HE confirmation stores grenade centre");
                    Returned("HE confirm");
                    Require(s.Placement is null,"HE confirm did not exit placement");
                    run.Marker=run.Base+new Vector3(48,0,80);
                    SetEffectPreview(owner,GrenadeKind.Decoy,run.Marker,true,false,true);
                    SchedulePlacementDiagnostic(run,6,1.25f);
                    break;
                }
                case 6:
                {
                    Require(effectPreviewScenes.TryGetValue(run.Slot,out var scene) && scene.Builder is null && scene.Beams.Count==3 && scene.Beams.All(b=>b.Visible && b.Entity.IsValid),"three marker beams not ready after 1.25 seconds");
                    run.Beams=scene!.Beams.Select(b=>b.Entity).ToArray();
                    run.BeamHandles=run.Beams.Select(b=>b.EntityHandle.Raw).ToArray();
                    Check(VerifyNativeEffectSegments(scene),"initial CBeam origins/endpoints/width/color equal published segments");
                    run.CreatedBeforeMove=effectBeamsCreated;
                    run.Marker+=new Vector3(32,16,8);
                    SetEffectPreview(owner,GrenadeKind.Decoy,run.Marker,false,false,true);
                    Say(null,"PLACEMENTCHECK DETAIL before move: "+EffectPreviewDiagnostics);
                    SchedulePlacementDiagnostic(run,7,1.25f);
                    break;
                }
                case 7:
                {
                    Require(effectPreviewScenes.TryGetValue(run.Slot,out var scene) && scene.Builder is null && scene.Beams.Count==3,"updated marker scene missing");
                    Check(scene!.Beams.Select(b=>b.Entity.EntityHandle.Raw).SequenceEqual(run.BeamHandles) && effectBeamsCreated==run.CreatedBeforeMove,"moving/red marker reuses identical entity handles without new entity creation");
                    Check(VerifyNativeEffectSegments(scene),"updated native CBeam endpoints/width/color match moved red marker");
                    Check(scene.Desired.Count==3 && scene.Desired.All(segment=>segment.Color.R==255 && segment.Color.G==65),"invalid placement uses red marker geometry");
                    Check(effectBeamCounts.GetValueOrDefault(run.Slot)<=EffectSegmentLimit,"per-owner beam entity cap respected");
                    Say(null,"PLACEMENTCHECK DETAIL after move: "+EffectPreviewDiagnostics);
                    ClearEffectPreview(run.Slot);
                    SchedulePlacementDiagnostic(run,8,1.25f);
                    break;
                }
                case 8:
                    Check(!effectPreviewScenes.ContainsKey(run.Slot) && effectBeamCounts.GetValueOrDefault(run.Slot)==0 && run.Beams.All(b=>!b.IsValid),"clear removes scene and all diagnostic beam entities");
                    s.Kind=GrenadeKind.Flash;
                    pawn.Teleport(position:run.Air,velocity:Vector3.Zero);SetMovement(pawn,MoveType_t.MOVETYPE_NOCLIP,MoveType_t.MOVETYPE_NOCLIP);
                    SetDiagnosticView(pawn,new(0,0,ThrowMode.Left));
                    BeginPlacement(owner,pawn,s);
                    Require(s.Placement is {StableStart:null},"flight entry unexpectedly captured a standing throw origin");
                    BodyEntered(s.Kind);
                    s.NextEffectAt=0;s.LastEffectCenter=null;
                    SchedulePlacementDiagnostic(run,9,1.25f);
                    break;
                case 9:
                    CompletePlacement(owner,pawn,s,true);
                    Check(s.Placement is null && s.Start==run.ConfirmedStart && s.RequestedStart==run.ConfirmedStart,"flight entry preserves the independent recorded throw origin");
                    Check(pawn.MoveType==MoveType_t.MOVETYPE_NOCLIP && Vector3.Distance(NativeWorld.FromNative(pawn.AbsOrigin!),run.Air)<1,"flight confirmation restores saved flying position");
                    Require(s.Placement is null,"flying confirm did not complete");
                    BodyRestored("flying confirm");
                    // A blocked return on unload/reset must never strand a
                    // walking player inside geometry or leave attack suppressed.
                    BeginPlacement(owner,pawn,s);
                    Require(s.Placement is not null,"cleanup fallback could not enter placement");
                    Require(grenadeBodies.TryGetValue(run.Slot,out var cleanupBody),"cleanup fallback has no camera to track");
                    run.RemovedCameraHandles.TryAdd(cleanupBody!.CameraHandle,"blocked-return reset");
                    var original=s.Placement!;
                    s.Placement=new(original.Pawn,original.Epoch,new Vector3(run.Base.X,run.Base.Y,-16000),original.ReturnEyeHeight,original.ReturnView,
                        MoveType_t.MOVETYPE_WALK,MoveType_t.MOVETYPE_WALK,original.NextAttack,null,original.Weapon);
                    SetMovement(pawn,MoveType_t.MOVETYPE_WALK,MoveType_t.MOVETYPE_WALK);
                    RestorePlacements();
                    Check(s.Placement is null && pawn.MoveType==MoveType_t.MOVETYPE_NOCLIP && NativeWorld.FromNative(pawn.AbsVelocity).Length()<.01f,"reset with blocked return clears placement and preserves safe flight");
                    Check(!grenadeBodies.ContainsKey(run.Slot),"blocked-return reset removes managed body state immediately");
                    SchedulePlacementDiagnostic(run,10,1.25f);
                    break;
                case 10:
                    Check(!effectPreviewScenes.ContainsKey(run.Slot) && effectBeamCounts.GetValueOrDefault(run.Slot)==0,"flying and reset paths leave no effect entities");
                    // Every removal request is at least 1.25 seconds old here.
                    // Resolve the original index+serial, so an unrelated new
                    // entity reusing the index cannot count as a camera leak.
                    foreach(var (handle,label) in run.RemovedCameraHandles)
                        Check(new CHandle<CBaseEntity>(handle).Value?.IsValid!=true,
                            $"{label}: original camera handle {handle} invalid after deferred native deletion");
                    FinishPlacementDiagnostic(run,null,true);
                    break;
            }
        }
        catch(Exception ex) {FinishPlacementDiagnostic(run,$"step {step}: {ex.GetType().Name}: {ex.Message}",true);}
    }

    private static void SetDiagnosticBotActive(CCSPlayerPawn pawn,bool active)
    {pawn.BotAllowActive=active;if(pawn.Bot is { } ai) ai.AllowActive=active;}

    private static void SetDiagnosticView(CCSPlayerPawn pawn,Aim view)
    {
        AlignView(pawn,view);
        // A server bot has no client to consume the queued view correction.
        // Set the measured bot eye field explicitly; production AlignView stays
        // unchanged and real players still receive the normal engine correction.
        pawn.EyeAngles.X=view.Pitch;pawn.EyeAngles.Y=view.Yaw;pawn.EyeAngles.Z=0;
    }

    private string PlacementDiagnosticPose(CCSPlayerController owner,CCSPlayerPawn pawn)
    {
        var feet=NativeWorld.FromNative(pawn.AbsOrigin!);
        bool support=TryStandingPosition(pawn,feet,out var supported);
        float? duck=pawn.MovementServices?.As<CCSPlayer_MovementServices>().DuckAmount;
        return $"feet=({feet.X:F2},{feet.Y:F2},{feet.Z:F2}); throw={ThrowInProgress(owner)}; move={pawn.MoveType}/{pawn.ActualMoveType}; flags=0x{pawn.Flags:X}; ground={(pawn.Flags&1)!=0}; speed={NativeWorld.FromNative(pawn.AbsVelocity).Length():F3}; duck={duck}; eye={pawn.ViewOffset.Z:F2}; upright={Upright(pawn)}; standing={Standing(pawn)}; eyeClear={PlacementEyeClear(pawn,feet,pawn.ViewOffset.Z)}; support={support}; supportRise={(support ? supported.Z-feet.Z : float.NaN):F3}; botActive={pawn.BotAllowActive}/{pawn.Bot?.AllowActive}; view=({pawn.EyeAngles.X:F2},{pawn.EyeAngles.Y:F2})";
    }

    private bool DiagnosticStandingReady(PlacementCheckRun run,int step,CCSPlayerController owner,CCSPlayerPawn pawn)
    {
        var feet=NativeWorld.FromNative(pawn.AbsOrigin!);
        if(Standing(pawn) && TryStandingPosition(pawn,feet,out var support) && support.Z-feet.Z<=3 && PlacementEyeClear(pawn,feet,pawn.ViewOffset.Z))
        {run.WaitingStep=-1;return true;}
        if(run.WaitingStep!=step)
        {
            run.WaitingStep=step;run.WaitingSince=Server.CurrentTime;
            Say(null,$"PLACEMENTCHECK WAIT step={step}: "+PlacementDiagnosticPose(owner,pawn));
        }
        if(Server.CurrentTime-run.WaitingSince>3)
            throw new InvalidOperationException("bot did not acquire a real standing state within 3 seconds: "+PlacementDiagnosticPose(owner,pawn));
        SetDiagnosticBotActive(pawn,true);
        SchedulePlacementDiagnostic(run,step,.25f);
        return false;
    }

    private static bool VerifyNativeEffectSegments(EffectPreviewScene scene)
    {
        if(scene.Desired.Count!=scene.Beams.Count) return false;
        for(int i=0;i<scene.Desired.Count;i++)
        {
            var wanted=scene.Desired[i];var beam=scene.Beams[i].Entity;
            if(!beam.IsValid || beam.AbsOrigin is not { } origin || Vector3.Distance(NativeWorld.FromNative(origin),wanted.A)>.02f ||
                Vector3.Distance(NativeWorld.FromNative(beam.EndPos),wanted.B)>.02f || MathF.Abs(beam.Width-wanted.Width)>.001f || beam.Render.ToArgb()!=wanted.Color.ToArgb()) return false;
        }
        return true;
    }

    private static Vector3 FindDiagnosticAir(CCSPlayerPawn pawn,Vector3 anchor)
    {
        var world=new NativeWorld(pawn);
        foreach(float z in new[]{192f,256f,384f})
            foreach(float x in new[]{0f,-128f,128f})
            {
                var feet=anchor+new Vector3(x,0,z);
                var eye=feet+Vector3.UnitZ*pawn.ViewOffset.Z;
                if(world.Sweep(eye,eye,2).Hit || !EffectPlacement.Resolve(world,GrenadeKind.Flash,feet).Valid || EffectPlacement.Resolve(world,GrenadeKind.Smoke,feet).Valid) continue;
                return feet;
            }
        throw new InvalidOperationException("could not find an unobstructed floating test point above the anchor");
    }

    private void FinishPlacementDiagnostic(PlacementCheckRun run,string? failure,bool restore)
    {
        if(!ReferenceEquals(placementCheck,run)) return;
        placementCheck=null;
        if(failure is not null) {run.Failed++;run.Details.Add("FAIL: "+failure);}
        void RestorePart(string label,Action action)
        {try {action();}catch(Exception ex) {run.Failed++;run.Details.Add($"FAIL: cleanup {label}: {ex.Message}");}}
        try
        {
            if(grenadeEpoch==run.Epoch)
            {
                run.Sandbox.Placement=null;
                RestorePart("grenade body",()=>RestoreGrenadeBody(run.Slot));
                RestorePart("effect entities",()=>ClearEffectPreview(run.Slot));
                RestorePart("route entities",()=>ClearPreview(run.Slot));
                var owner=Utilities.GetPlayerFromSlot(run.Slot);
                if(restore && owner is {IsValid:true,IsBot:true} && owner.EntityHandle.Raw==run.Controller && owner.SteamID==run.Steam &&
                    owner.PlayerPawn.Value is {IsValid:true,LifeState:0} pawn && pawn.EntityHandle.Raw==run.Pawn)
                {
                    RestorePart("position/view",()=>{pawn.Teleport(position:run.Feet,velocity:run.Velocity);SetDiagnosticView(pawn,run.View);});
                    RestorePart("movement",()=>SetMovement(pawn,run.Move,run.ActualMove));
                    RestorePart("attack time",()=>SetNextAttack(pawn,run.Attack));
                    RestorePart("weapon",()=>
                    {if(run.Weapon is { } weapon && pawn.WeaponServices is { } weapons && weapons.MyWeapons.Any(h=>h.Value is {IsValid:true} w && w.EntityHandle.Raw==weapon)) weapons.ActiveWeapon.Raw=weapon;});
                    RestorePart("bot AI",()=>{pawn.BotAllowActive=run.BotActive;if(run.AiActive is { } active && pawn.Bot is { } ai) ai.AllowActive=active;});
                }
            }
        }
        catch(Exception ex) {run.Failed++;run.Details.Add("FAIL: cleanup: "+ex.Message);}
        finally
        {
            RestorePart("session",()=>
            {
                if(grenadeEpoch==run.Epoch && sessions.TryGetValue(run.Slot,out var session) && ReferenceEquals(session,run.Sandbox))
                {
                    if(restore && run.OriginalSession is { } original) sessions[run.Slot]=original;
                    else sessions.Remove(run.Slot);
                }
            });
            foreach(var detail in run.Details) Say(null,"PLACEMENTCHECK DETAIL "+detail);
            Say(null,$"PLACEMENTCHECK {(run.Failed==0 ? "PASS" : "FAIL")}: passed={run.Passed}; failed={run.Failed}; native placement/state/pool checks only; client pixels and multi-player visibility remain unverified.");
        }
    }
}
