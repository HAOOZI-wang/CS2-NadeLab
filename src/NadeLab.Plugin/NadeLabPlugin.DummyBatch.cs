using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private readonly Dictionary<int,DummyBatchState<EffectPose>> dummyBatches=[];
    private DummyBatchIdentity DummyBatchIdentityFor(CCSPlayerController player)=>
        new(grenadeEpoch,player.EntityHandle.Raw,player.PlayerPawn.Value?.EntityHandle.Raw ?? 0,player.SteamID);

    private bool HasDummyBatch(CCSPlayerController player)
    {
        if(!dummyBatches.TryGetValue(player.Slot,out var batch)) return false;
        if(player.IsValid && Practice && player.PlayerPawn.Value is {IsValid:true,LifeState:0} && batch.Matches(DummyBatchIdentityFor(player))) return true;
        ClearDummyBatch(player.Slot);return false;
    }
    private void BeginDummyBatch(CCSPlayerController? player)
    {
        if(!Check(player,out _)) return;
        if(DummyLayoutLoadActive) {Say(player,"正在加载假人组合，完成后再连续放置。");return;}
        if(!HasDummyBatch(player!))
        {
            // A search completing or an old preparation timer must not move
            // the player while they are recording the next target's pose.
            if(job?.Slot==player!.Slot || flashJob?.Slot==player.Slot) CancelSearch();
            var session=Get(player!);
            session.Arm=null;session.Pending=null;session.FlightCheck=null;session.Snapshot=null;session.PreparationIssue=null;
            session.CueStage=ReleaseStage.None;session.CueUntil=0;session.PreparationRevision++;
            dummyBatches[player!.Slot]=new(DummyBatchIdentityFor(player));
        }
        ClosePanel(player);
        Say(player,DummyBatchHud(player!)!);
    }
    private void DummyBatchPrimary(CCSPlayerController player)
    {
        if(!HasDummyBatch(player) || !Check(player,out var pawn)) return;
        var batch=dummyBatches[player.Slot];
        if(batch.Phase==DummyBatchPhase.Creating) {Say(player,"正在放置假人，请稍候；R 可取消本次。");return;}
        if(!DummyRoster.CanAdd(effectDummies.Values.Count(d=>d.Owner==player.EntityHandle.Raw),effectDummies.Count))
        {Say(player,$"已达到假人上限（我的{MaxPlayerDummies}个 / 全服{MaxServerDummies}个）；R 退出后可管理已有假人。");return;}
        if(batch.Phase==DummyBatchPhase.Recording)
        {
            if(pawn.AbsOrigin is null || !Standing(pawn)) {Say(player,"先在地面站立停稳，再按 E 记录假人站位和朝向。");return;}
            var pose=new EffectPose(NativeWorld.FromNative(pawn.AbsOrigin),pawn.ViewOffset.Z,
                new(pawn.EyeAngles.X,pawn.EyeAngles.Y,ThrowMode.Left),Get(player).EffectPose?.Armor ?? 100);
            if(batch.Record(pose))
            {
                // Keep the legacy armor preference available in management;
                // the batch still owns its immutable pose independently.
                Get(player).EffectPose=pose;
                Say(player,"已记录站位和朝向；离开此位置后按 E 放置，R 取消记录。");
            }
        }
        else if(batch.Pose is { } pose)
        {
            if(pawn.AbsOrigin is null || Vector3.Distance(NativeWorld.FromNative(pawn.AbsOrigin),pose.Feet)<48)
            {Say(player,"请先离开记录的位置，再按 E 放置假人。");return;}
            if(!batch.TryBeginCreation(effectDummies.Values.Count(d=>d.Owner==player.EntityHandle.Raw),effectDummies.Count,out long request)) return;
            try
            {
                if(!PlaceEffectDummyCore(player,pose,batch:batch,batchRequest:request)) batch.Complete(request,false);
            }
            catch {batch.Complete(request,false);throw;}
        }
        Get(player).NextHudAt=0;
    }
    private void DummyBatchBack(CCSPlayerController player)
    {
        if(!HasDummyBatch(player)) return;
        var batch=dummyBatches[player.Slot];
        var result=batch.Back();
        if(result==DummyBatchBackResult.CancelledCreation) CancelDummyBatchRequest(batch);
        if(result==DummyBatchBackResult.Exited) dummyBatches.Remove(player.Slot);
        Get(player).NextHudAt=0;
        Say(player,result switch
        {
            DummyBatchBackResult.CancelledPose=>"已取消记录；E 重新记录，R 退出连续放置。",
            DummyBatchBackResult.CancelledCreation=>"已取消本次放置；正在清理创建请求。E 可记录下一处，R 退出。",
            _=>"已退出连续放置；已有假人保留。"
        });
    }
    private void CancelDummyBatchRequest(DummyBatchState<EffectPose> batch)
    {
        if(dummyCreation is not { } request || !ReferenceEquals(request.Batch,batch)) return;
        request.Cancelled=true;
        // Drop the quota reservation immediately, but keep the request identity
        // until Tick has claimed/removed a bot already entering the engine.
        // Other creation requests remain blocked during this cleanup window.
        SetDummyQuota(0);
        if(request.Claimed is { } claimed) RemoveDummy(claimed);
    }
    private void ClearDummyBatch(int? slot=null)
    {
        foreach(int key in dummyBatches.Keys.Where(key=>slot is null || key==slot).ToArray())
        {
            var batch=dummyBatches[key];batch.Clear();CancelDummyBatchRequest(batch);dummyBatches.Remove(key);
        }
    }
    private void CompleteDummyBatchCreation(DummyCreation request,bool success)
    {
        if(request.Batch is not { } batch) return;
        var owner=Utilities.GetPlayerFromSlot(request.Slot);
        if(owner is not {IsValid:true} || !dummyBatches.TryGetValue(request.Slot,out var current) || !ReferenceEquals(current,batch) ||
            !batch.Matches(DummyBatchIdentityFor(owner))) return;
        if(batch.Complete(request.BatchRequest,success)) Get(owner).NextHudAt=0;
    }
    private string? DummyBatchHud(CCSPlayerController player)
    {
        if(!HasDummyBatch(player)) return null;
        int count=effectDummies.Values.Count(d=>d.Owner==player.EntityHandle.Raw && !d.Removing);
        var phase=dummyBatches[player.Slot].Phase;
        string keys=phase switch
        {
            DummyBatchPhase.Creating=>"正在放置… · R 取消本次",
            DummyBatchPhase.Pending=>"已记录 · 离开后 E 放置 · R 取消记录",
            _ when count>=MaxPlayerDummies || effectDummies.Count>=MaxServerDummies=>"已达容量上限 · R 退出并管理假人",
            _=>"E 记录站位和朝向 · R 退出"
        };
        return $"连续放置假人 {count}/{MaxPlayerDummies}\n{keys} · F1 菜单";
    }
}
