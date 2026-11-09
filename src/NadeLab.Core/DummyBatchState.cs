namespace NadeLab.Core;

public readonly record struct DummyBatchIdentity(int Epoch,uint Controller,uint Pawn,ulong Steam);
public enum DummyBatchPhase { Recording,Pending,Creating }
public enum DummyBatchBackResult { CancelledPose,CancelledCreation,Exited }

public sealed class DummyBatchState<TPose>(DummyBatchIdentity identity) where TPose:class
{
    public DummyBatchIdentity Identity { get; }=identity;
    public bool Active { get; private set; }=true;
    public DummyBatchPhase Phase { get; private set; }
    public TPose? Pose { get; private set; }
    public long Request { get; private set; }
    public bool Matches(DummyBatchIdentity current)=>Active && Identity==current;
    public bool Record(TPose pose)
    {
        if(!Active || Phase!=DummyBatchPhase.Recording) return false;
        Pose=pose;Phase=DummyBatchPhase.Pending;return true;
    }
    public bool TryBeginCreation(int playerCount,int serverCount,out long request)
    {
        request=0;
        if(!Active || Phase!=DummyBatchPhase.Pending || Pose is null || !DummyRoster.CanAdd(playerCount,serverCount)) return false;
        Phase=DummyBatchPhase.Creating;request=++Request;return true;
    }
    public bool Complete(long request,bool success)
    {
        if(!Active || Phase!=DummyBatchPhase.Creating || request!=Request) return false;
        Phase=success ? DummyBatchPhase.Recording : DummyBatchPhase.Pending;
        if(success) Pose=null;
        return true;
    }
    public DummyBatchBackResult Back()
    {
        if(!Active || Phase==DummyBatchPhase.Recording) {Clear();return DummyBatchBackResult.Exited;}
        var result=Phase==DummyBatchPhase.Creating ? DummyBatchBackResult.CancelledCreation : DummyBatchBackResult.CancelledPose;
        Request++;Pose=null;Phase=DummyBatchPhase.Recording;return result;
    }
    public void Clear() {Active=false;Request++;Pose=null;Phase=DummyBatchPhase.Recording;}
}
