using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private Action? stopDummyLayoutDiagnostic;
    private void StopDummyLayoutDiagnostic()=>stopDummyLayoutDiagnostic?.Invoke();
    private void RegisterDummyLayoutDiagnostic()=>AddCommand("css_nl_dummylayoutcheck",
        "Zero-human sparse dummy combination creation, support and cancellation diagnostic",(caller,command)=>
    {
        if(caller is not null || !loaded || !Practice || !BenchmarkIdle || Utilities.GetPlayers().Any(p=>!p.IsBot))
        {Say(caller,"DUMMYLAYOUT refused: console, zero humans and idle practice required.");return;}
        var owner=Utilities.GetPlayers().FirstOrDefault(p=>p.IsValid && p.IsBot && p.PlayerPawn.Value is {IsValid:true,LifeState:0,AbsOrigin:not null});
        if(owner?.PlayerPawn.Value is not {IsValid:true,LifeState:0,AbsOrigin:not null} pawn || ConVar.Find("bot_stop") is not { } botStop)
        {Say(null,"DUMMYLAYOUT refused: one existing idle living seed bot and bot_stop required.");return;}
        var sites=FindDummyCapacitySites(NativeWorld.FromNative(pawn.AbsOrigin));
        if(sites.Length<3) {Say(null,"DUMMYLAYOUT refused: three nearby supported sites were not found.");return;}
        int epoch=grenadeEpoch,phase=0,passed=0,index=0;bool stopped=false;
        uint controller=owner.EntityHandle.Raw,pawnHandle=pawn.EntityHandle.Raw;string map=Server.MapName,oldStop=botStop.StringValue;
        var original=Utilities.GetPlayers().Select(p=>p.EntityHandle.Raw).ToHashSet();
        var oldMove=pawn.MoveType;var oldActual=pawn.ActualMoveType;var velocity=NativeWorld.FromNative(pawn.AbsVelocity);
        bool oldActive=pawn.BotAllowActive,oldAi=pawn.Bot?.AllowActive ?? false;
        bool hadSession=sessions.TryGetValue(owner.Slot,out var oldSession);
        var temporary=new Session {SteamId=owner.SteamID,Revision=(oldSession?.Revision ?? 0)+1,HudEnabled=false,GuideEnabled=false};
        var members=new[]{new DummyLayoutMember(0,sites[0],64,new(-20,-135,ThrowMode.Left),0,FlashRole.MustFlash),
            new DummyLayoutMember(3,sites[1],64,new(0,45,ThrowMode.Left),50,FlashRole.Protect),
            new DummyLayoutMember(7,sites[2],64,new(20,135,ThrowMode.Left),100,FlashRole.Ignore)};
        DummyCreation? latest=null;float deadline=Server.CurrentTime+30,settleAt=0;
        DummyLayoutLoad Context(DummyCreation? request=null)=>new()
        {Slot=owner.Slot,Owner=controller,Pawn=pawnHandle,Steam=owner.SteamID,Epoch=epoch,Map=map,
            Session=temporary,Revision=temporary.Revision,LayoutSlot=1,Members=members,Request=request,Deadline=deadline};
        void Require(bool condition,string message)
        {if(!condition) throw new InvalidOperationException(message);passed++;Say(null,"DUMMYLAYOUT PASS: "+message);}
        void Cleanup()
        {
            if(dummyLayoutLoad is { } load && load.Owner==controller) StopDummyLayoutLoad();
            if(dummyCreation is { } request && request.Owner==controller && request.Epoch==epoch)
            {
                request.Cancelled=true;SetDummyQuota(0);
                if(request.Requested && request.Claimed is null)
                {
                    var added=Utilities.GetPlayers().Where(p=>p.IsValid && p.IsBot && !request.Known.Contains(p.EntityHandle.Raw)).ToArray();
                    if(added.Length==1) ClaimPendingDummy(request,added[0]);
                }
                dummyCreation=null;SetDummyQuota(0);
            }
            foreach(var target in effectDummies.Values.Where(d=>d.Owner==controller && d.OwnerSteam==owner.SteamID).ToArray()) RemoveDummy(target);
            selectedDummies.Remove(controller);RemoveDummyPredictions(owner.Slot);ReleaseDummyQuota();
        }
        void Finish(string? error=null)
        {
            if(stopped) return;stopped=true;
            try {if(epoch==grenadeEpoch) Cleanup();}
            catch(Exception ex) {error=(error is null ? "" : error+"; ")+"cleanup: "+ex.Message;}
            if(epoch==grenadeEpoch)
            {
                if(ConVar.Find("bot_stop") is { } stop) stop.StringValue=oldStop;
                if(pawn.IsValid && pawn.EntityHandle.Raw==pawnHandle)
                {pawn.BotAllowActive=oldActive;if(pawn.Bot is { } ai) ai.AllowActive=oldAi;SetMovement(pawn,oldMove,oldActual);pawn.Teleport(velocity:velocity);}
                if(sessions.TryGetValue(owner.Slot,out var current) && ReferenceEquals(current,temporary))
                {if(hadSession) sessions[owner.Slot]=oldSession!;else sessions.Remove(owner.Slot);}
            }
            dummyCheckPending=false;stopDummyLayoutDiagnostic=null;
            Say(null,$"DUMMYLAYOUT DONE {(error is null ? "PASS" : "FAIL")}: passed={passed}; error={error ?? "none"}; native sparse labels/support/cancellation only; no player archive written and client menu pixels unverified.");
        }
        void Request(DummyLayoutMember member)
        {
            PlaceEffectDummyCore(owner,new(member.Feet,member.EyeHeight,member.View,member.Armor),member.LabelIndex,member.Role);
            latest=dummyCreation;Require(latest is not null,"issued serial creation for "+DummyRoster.Label(member.LabelIndex));
        }
        void Step()
        {
            if(stopped) return;
            try
            {
                if(!loaded || !Practice || epoch!=grenadeEpoch || Server.MapName!=map || Utilities.GetPlayers().Any(p=>!p.IsBot) ||
                    !owner.IsValid || owner.EntityHandle.Raw!=controller || !pawn.IsValid || pawn.EntityHandle.Raw!=pawnHandle || pawn.LifeState!=0)
                    throw new InvalidOperationException("zero-human/world/full-handle guard changed");
                if(Server.CurrentTime>deadline) throw new InvalidOperationException("phase timed out: "+phase);
                if(phase==0)
                {
                    if(creatingDummy || dummyRemoving) {AddTimer(.1f,Step);return;}
                    if(latest is not null) Require(latest.Claimed is {Live:true,Ready:true,Removing:false},"previous requested target fully positioned before next creation");
                    if(index<3) {Request(members[index++]);AddTimer(.1f,Step);return;}
                    var mine=OwnedDummies(owner);
                    Require(mine.Select(d=>d.LabelIndex).SequenceEqual(new[]{0,3,7}),"sparse A/D/H identity preserved without renumbering");
                    Require(mine.All(d=>members.Single(m=>m.LabelIndex==d.LabelIndex) is { } expected && d.SearchRole==expected.Role && d.Pose.Armor==expected.Armor &&
                        Vector3.Distance(d.PositionedFeet,expected.Feet)<3.1f && MathF.Abs(d.Pose.View.Pitch-expected.View.Pitch)<.1f && MathF.Abs(Geometry.NormalizeYaw(d.Pose.View.Yaw-expected.View.Yaw))<.1f),"each target restores its own role, armour, position and direction");
                    Require(mine.All(d=>SnapshotDummyLayout(d)==members.Single(m=>m.LabelIndex==d.LabelIndex)),"resaving retains original base position instead of accumulating placement clearance height");
                    Require(mine.All(d=>d.Bot.PlayerPawn.Value is {IsValid:true,LifeState:0,Health:100,BotAllowActive:false} target &&
                        target.Bot?.AllowActive==false && target.WeaponServices?.MyWeapons.Count==0 && DummyOpaque(d,target)),"restored targets are frozen disarmed visible-state entities, not roaming bots");
                    Require(Utilities.GetPlayers().Count==original.Count+3,"exactly three new targets without companion bots");
                    var ignored=mine.Select(d=>d.Handle).ToHashSet();
                    Require(members.All(m=>DummyLayoutSupported(m.Feet,out var supported) && Vector3.Distance(supported,m.Feet)<3.1f),"support preflight ignores old own dummy bodies while checking map geometry");
                    Require(members.All(m=>!DummyLayoutBodyBlocked(m.Feet,ignored)) && DummyLayoutBodyBlocked(members[0].Feet,[]),"body check ignores only requested owned handles and detects occupied targets otherwise");
                    var context=Context();foreach(var d in mine) context.Created.Add(d.Handle);dummyLayoutLoad=context;
                    StopDummyLayoutLoad();Require(dummyLayoutLoad is null,"formal cancellation clears load coordinator");
                    phase=1;deadline=Server.CurrentTime+12;settleAt=Server.CurrentTime+2;
                }
                else if(phase==1 && Server.CurrentTime>=settleAt && !creatingDummy && !dummyRemoving)
                {
                    Require(effectDummies.Count==0 && Utilities.GetPlayers().Select(p=>p.EntityHandle.Raw).ToHashSet().SetEquals(original),"formal cancellation removes all created target handles without orphans");
                    Request(members[1]);phase=2;deadline=Server.CurrentTime+12;
                }
                else if(phase==2 && !creatingDummy && !dummyRemoving)
                {
                    Require(latest?.Claimed is {Live:true,Ready:true},"latest target finished before layout cancellation tick");
                    Require(latest?.Claimed is { } again && SnapshotDummyLayout(again)==members[1],"a second restore and save of D preserves the same base height and role");
                    var context=Context(latest);Require(context.Created.Count==0 && !ReferenceEquals(dummyCreation,latest),"simulate completed request not yet copied into layout-created set");
                    dummyLayoutLoad=context;StopDummyLayoutLoad();phase=3;deadline=Server.CurrentTime+12;settleAt=Server.CurrentTime+2;
                }
                else if(phase==3 && Server.CurrentTime>=settleAt && !creatingDummy && !dummyRemoving)
                {
                    Require(effectDummies.Count==0 && Utilities.GetPlayers().Select(p=>p.EntityHandle.Raw).ToHashSet().SetEquals(original),"just-completed untracked request also removed by cancellation");
                    Request(members[0]);latest!.ReadyAt=Server.CurrentTime;TickDummyCreation();Require(latest.Requested,"cancellation includes an issued native quota request");
                    dummyLayoutLoad=Context(latest);StopDummyLayoutLoad();phase=4;deadline=Server.CurrentTime+12;settleAt=Server.CurrentTime+2;
                }
                else if(phase==4 && Server.CurrentTime>=settleAt && !creatingDummy && !dummyRemoving)
                {
                    Require(effectDummies.Count==0 && Utilities.GetPlayers().Select(p=>p.EntityHandle.Raw).ToHashSet().SetEquals(original),"pending native creation cancellation leaves no roaming or replacement bot");
                    Require(!dummyQuotaLease && ConVar.Find("bot_quota")?.GetPrimitiveValue<int>()==original.Count,"bot quota lease returned to original population");
                    Finish();return;
                }
                AddTimer(.1f,Step);
            }
            catch(Exception ex) {Finish(ex.Message);}
        }
        sessions[owner.Slot]=temporary;dummyCheckPending=true;stopDummyLayoutDiagnostic=()=>Finish("interrupted by lifecycle reset");
        botStop.SetValue(false);pawn.BotAllowActive=false;if(pawn.Bot is { } frozen) frozen.AllowActive=false;
        SetMovement(pawn,MoveType_t.MOVETYPE_NONE,MoveType_t.MOVETYPE_NONE);pawn.Teleport(velocity:Vector3.Zero);
        Say(null,"DUMMYLAYOUT START: sparse A/D/H creation and cancellation; no player save files touched.");AddTimer(.2f,Step);
    });
}
