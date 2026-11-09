using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using NadeLab.Core;
using CsTrace=CounterStrikeSharp.API.Modules.Utils.Trace;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private static Vector3[] FindDummyCapacitySites(Vector3 origin)
    {
        var sites=new List<Vector3>();
        var options=new TraceOptions {InteractsAs=Contents.Player,InteractsWith=Masks.PlayerSolid|Contents.PhysicsProp};
        foreach(float radius in new[]{80f,144f,208f,288f,384f,512f})
            for(int n=0;n<24;n++)
            {
                var xy=origin+new Vector3(MathF.Cos(n*MathF.Tau/24)*radius,MathF.Sin(n*MathF.Tau/24)*radius,0);
                var floor=CsTrace.TraceHullShape(NativeWorld.ToNative(xy+Vector3.UnitZ*128),NativeWorld.ToNative(xy-Vector3.UnitZ*384),new(-16,-16,0),new(16,16,72),null,options);
                if(!floor.DidHit() || floor.IsAllSolid || floor.Normal.Z<.7f ||
                    !TryStandingPosition(null,NativeWorld.FromNative(floor.EndPos),out var safe) ||
                    Vector3.Distance(safe,origin)<64 || sites.Any(site=>Vector3.Distance(site,safe)<64)) continue;
                sites.Add(safe);if(sites.Count==MaxPlayerDummies) return sites.ToArray();
            }
        return sites.ToArray();
    }

    private void RegisterDummyCapacityDiagnostic()=>AddCommand("css_nl_dummycapacitycheck","Empty-server eight-target capacity, identity, prediction and cleanup checks",(caller,command)=>
    {
        if(caller is not null || !loaded || !Practice || Utilities.GetPlayers().Any(p=>!p.IsBot) || !BenchmarkIdle)
        {Say(caller,"DUMMYCAPACITY refused: empty idle practice server required.");return;}
        var owner=Utilities.GetPlayers().FirstOrDefault(p=>p.IsValid && p.IsBot && p.PlayerPawn.Value is {IsValid:true,LifeState:0,AbsOrigin:not null});
        if(owner?.PlayerPawn.Value is not {IsValid:true,AbsOrigin:not null} pawn)
        {Say(null,"DUMMYCAPACITY refused: one existing living idle bot required.");return;}
        if(ConVar.Find("bot_stop")?.StringValue is not ("0" or "false"))
        {Say(null,"DUMMYCAPACITY refused: bot_stop must be 0.");return;}
        var sites=FindDummyCapacitySites(NativeWorld.FromNative(pawn.AbsOrigin));
        if(sites.Length<MaxPlayerDummies) {Say(null,"DUMMYCAPACITY refused: eight safe supported nearby sites were not found.");return;}
        uint ownerHandle=owner.EntityHandle.Raw,pawnHandle=pawn.EntityHandle.Raw;int epoch=grenadeEpoch;
        var original=Utilities.GetPlayers().Select(p=>p.EntityHandle.Raw).ToHashSet();
        string? originalAlpha=ConVar.Find("sv_disable_immunity_alpha")?.StringValue;
        var oldMove=pawn.MoveType;var oldActual=pawn.ActualMoveType;var oldVelocity=NativeWorld.FromNative(pawn.AbsVelocity);
        bool oldActive=pawn.BotAllowActive,oldAi=pawn.Bot?.AllowActive ?? false;
        pawn.BotAllowActive=false;if(pawn.Bot is { } ai) ai.AllowActive=false;
        SetMovement(pawn,MoveType_t.MOVETYPE_NONE,MoveType_t.MOVETYPE_NONE);pawn.Teleport(velocity:Vector3.Zero);
        dummyCheckPending=true;int phase=0,passed=0;float deadline=Server.CurrentTime+12,settleAt=0;
        var originalTargets=new Dictionary<int,uint>();uint oldH=0,oldD=0,newH=0;
        EffectPose Pose(int index)=>new(sites[index],64,new((index-4)*5,Geometry.NormalizeYaw(-179+index*51),ThrowMode.Left),index%2==0 ? 100 : 0);
        void Require(bool okay,string text)
        {if(!okay) throw new InvalidOperationException(text);passed++;Say(null,"DUMMYCAPACITY PASS: "+text);}
        void CleanupOwned()
        {
            if(dummyCreation is { } request && request.Owner==ownerHandle && request.Epoch==epoch)
            {
                // Do not depend on the practice Tick: this cleanup also runs
                // after practice is disabled or a human joins the server.
                request.Cancelled=true;SetDummyQuota();
                try
                {
                    if(request.Requested && request.Claimed is null)
                    {
                        var added=Utilities.GetPlayers().Where(p=>p.IsValid && p.IsBot && !request.Known.Contains(p.EntityHandle.Raw)).ToArray();
                        // An ambiguous externally added bot is never kicked.
                        if(added.Length==1) ClaimPendingDummy(request,added[0]);
                    }
                }
                finally {dummyCreation=null;SetDummyQuota();}
            }
            foreach(var target in effectDummies.Values.Where(d=>d.Owner==ownerHandle).ToArray()) RemoveDummy(target);
            selectedDummies.Remove(ownerHandle);RemoveDummyPredictions(owner.Slot);ReleaseDummyQuota();
        }
        void Done(string? error=null)
        {
            if(epoch==grenadeEpoch) CleanupOwned();
            dummyCheckPending=false;
            if(pawn.IsValid && pawn.EntityHandle.Raw==pawnHandle)
            {pawn.BotAllowActive=oldActive;if(pawn.Bot is { } botAi) botAi.AllowActive=oldAi;SetMovement(pawn,oldMove,oldActual);pawn.Teleport(velocity:oldVelocity);}
            Say(null,$"DUMMYCAPACITY DONE {(error is null ? "PASS" : "FAIL")}: passed={passed}; error={error ?? "none"}; native lifecycle only, client pixels unverified.");
        }
        void Request(int index,int nextPhase)
        {
            PlaceEffectDummyCore(owner,Pose(index));
            Require(dummyCreation is not null,"requested target "+DummyRoster.Label(index));
            phase=nextPhase;deadline=Server.CurrentTime+12;
        }
        void Step()
        {
            if(!dummyCheckPending) return;
            try
            {
                if(!loaded || !Practice || epoch!=grenadeEpoch || Utilities.GetPlayers().Any(p=>!p.IsBot) ||
                    !owner.IsValid || owner.EntityHandle.Raw!=ownerHandle || !pawn.IsValid || pawn.EntityHandle.Raw!=pawnHandle || pawn.LifeState!=0)
                    throw new InvalidOperationException("zero-human/world/owner guard changed");
                if(Server.CurrentTime>deadline) throw new InvalidOperationException("capacity phase timed out: "+phase);
                var mine=OwnedDummies(owner);
                if(creatingDummy || dummyRemoving || mine.Any(d=>!d.Ready)) {AddTimer(.1f,Step);return;}
                if(phase==0)
                {
                    if(mine.Length<MaxPlayerDummies) {Request(mine.Length,0);AddTimer(.1f,Step);return;}
                    Require(mine.Select(d=>d.LabelIndex).SequenceEqual(Enumerable.Range(0,8)),"A through H coexist with stable unique identities");
                    Require(Utilities.GetPlayers().Count==original.Count+8,"exactly eight targets, no roaming companion bots");
                    Require(mine.All(d=>d.Bot.PlayerPawn.Value is {IsValid:true,LifeState:0,TakesDamage:true,BotAllowActive:false,IsSpawning:false,Respawning:false,Health:100} targetPawn &&
                        targetPawn.Bot?.AllowActive==false && targetPawn.MoveType==MoveType_t.MOVETYPE_NONE && targetPawn.WeaponServices?.MyWeapons.Count==0 && DummyOpaque(d,targetPawn)),
                        "all eight retain model-specific body meshes, normal damage, no weapons and frozen movement; client pixels unverified");
                    Require(mine.All(d=>d.Bot.PlayerPawn.Value is {IsValid:true,AbsRotation:not null} current &&
                        Math.Abs(current.EyeAngles.X-d.Pose.View.Pitch)<1 && Math.Abs(Geometry.NormalizeYaw(current.EyeAngles.Y-d.Pose.View.Yaw))<1 &&
                        Math.Abs(current.V_angle.X-d.Pose.View.Pitch)<1 && Math.Abs(Geometry.NormalizeYaw(current.V_angle.Y-d.Pose.View.Yaw))<1 &&
                        Math.Abs(current.AbsRotation.X)<1 && Math.Abs(current.AbsRotation.Z)<1 && Math.Abs(Geometry.NormalizeYaw(current.AbsRotation.Y-d.Pose.View.Yaw))<1),
                        "all eight keep their own distinct eye/native/body directions with upright bodies");
                    string roster=DummyRosterText(owner) ?? "";
                    Require(mine.All(d=>roster.Contains(DummyLabel(d))) && System.Text.Encoding.UTF8.GetByteCount(roster)<=900 && roster.Split('\n').Length==9,
                        "all eight appear in the existing roster budget");
                    Require(RunPredictionDiagnostic(owner),"all eight receive independent Flash/HE prediction samples");
                    foreach(var d in mine) originalTargets[d.LabelIndex]=d.Handle;
                    int quota=ConVar.Find("bot_quota")!.GetPrimitiveValue<int>();
                    PlaceEffectDummyCore(owner,Pose(0));
                    Require(!creatingDummy && OwnedDummies(owner).Length==8 && ConVar.Find("bot_quota")!.GetPrimitiveValue<int>()==quota,"ninth placement is refused without quota changes");
                    var last=mine.Single(d=>d.LabelIndex==7);oldH=last.Handle;SelectDummy(owner,last);
                    Require(DummyActionTarget(owner,oldH)==last && last.MarkerText=="[H]","H can be selected and maps to its world marker");
                    // Explicit setup for reset isolation, not a damage/physics measurement.
                    last.Bot.PlayerPawn.Value!.Health=55;last.LastDamage=45;last.DamageTotal=45;
                    ResetDummy(last);
                    Require(last.Ready && last.Bot.PlayerPawn.Value!.Health==100 && last.LastDamage==0 && mine.Where(d=>d!=last).All(d=>d.Bot.PlayerPawn.Value!.Health==100),"resetting H affects only H");
                    RemoveDummy(last);phase=1;deadline=Server.CurrentTime+10;
                }
                else if(phase==1)
                {
                    Require(mine.Length==7 && mine.All(d=>d.Handle==originalTargets[d.LabelIndex]) && DummyActionTarget(owner,oldH) is null,"deleting H preserves A through G and rejects its stale action");
                    Request(7,2);
                }
                else if(phase==2)
                {
                    Require(mine.Length==8 && mine.Single(d=>d.LabelIndex==7).Handle!=oldH,"the freed H label can be placed again");
                    newH=mine.Single(d=>d.LabelIndex==7).Handle;
                    var middle=mine.Single(d=>d.LabelIndex==3);oldD=middle.Handle;SelectDummy(owner,middle);RemoveDummy(middle);
                    phase=3;deadline=Server.CurrentTime+10;
                }
                else if(phase==3)
                {
                    Require(mine.Length==7 && mine.All(d=>d.LabelIndex==7 ? d.Handle==newH : d.Handle==originalTargets[d.LabelIndex]),"middle removal leaves every surviving label unchanged");
                    Request(3,4);
                }
                else if(phase==4)
                {
                    Require(mine.Length==8 && mine.Single(d=>d.LabelIndex==3).Handle!=oldD && mine.Single(d=>d.LabelIndex==7).Handle==newH,"middle gap reuses D without renumbering E through H");
                    CleanupOwned();phase=5;deadline=Server.CurrentTime+10;settleAt=Server.CurrentTime+2;
                }
                else if(phase==5 && Server.CurrentTime>=settleAt)
                {
                    Require(effectDummies.Count==0 && !creatingDummy && !dummyRemoving,"all diagnostic targets fully removed");
                    Require(Utilities.GetPlayers().Select(p=>p.EntityHandle.Raw).ToHashSet().SetEquals(original),"no orphan or replacement bots after cleanup");
                    Require(!dummyQuotaLease && ConVar.Find("bot_quota")!.GetPrimitiveValue<int>()==original.Count,"quota returned to the original bot population");
                    Require(dummyOriginalImmunityAlpha is null && ConVar.Find("sv_disable_immunity_alpha")?.StringValue==originalAlpha,"client rendering lease returned");
                    // Exercise the same synchronous cancellation path used by
                    // the zero-human/practice guards, with an issued request.
                    PlaceEffectDummyCore(owner,Pose(0));
                    if(dummyCreation is not { } pending) throw new InvalidOperationException("pending cancellation request missing");
                    pending.ReadyAt=Server.CurrentTime;TickDummyCreation();
                    Require(pending.Requested,"cancellation exercises an issued native quota request");
                    CleanupOwned();Require(dummyCreation is null,"pending cleanup does not depend on a later practice tick");
                    phase=6;deadline=Server.CurrentTime+10;settleAt=Server.CurrentTime+2;
                }
                else if(phase==6 && Server.CurrentTime>=settleAt)
                {
                    Require(effectDummies.Count==0 && Utilities.GetPlayers().Select(p=>p.EntityHandle.Raw).ToHashSet().SetEquals(original),"cancelled request leaves no owned or roaming replacement bot");
                    Require(!dummyQuotaLease && ConVar.Find("bot_quota")!.GetPrimitiveValue<int>()==original.Count &&
                        dummyOriginalImmunityAlpha is null && ConVar.Find("sv_disable_immunity_alpha")?.StringValue==originalAlpha,
                        "cancelled request returns quota and rendering lease");
                    Done();return;
                }
                AddTimer(.1f,Step);
            }
            catch(Exception ex) {Done(ex.Message);}
        }
        Say(null,"DUMMYCAPACITY START: eight targets, cap, H operations, middle reuse, all-target prediction and cleanup.");
        AddTimer(.1f,Step);
    });
}
