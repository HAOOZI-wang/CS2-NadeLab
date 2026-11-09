using NadeLab.Core;

internal static class DummyBatchTests
{
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        var identity=new DummyBatchIdentity(2,11,22,33);
        test("Dummy batch records then places once and advances only after actual success",()=>
        {
            var batch=new DummyBatchState<string>(identity);
            assert(!batch.TryBeginCreation(0,0,out _),"placement started without a pose");
            assert(batch.Record("pose A") && batch.TryBeginCreation(0,0,out long request),"recorded pose cannot start creation");
            long token=batch.Request;
            assert(batch.Phase==DummyBatchPhase.Creating && batch.Pose=="pose A","request discarded its own pose");
            assert(!batch.Record("pose B") && !batch.TryBeginCreation(0,0,out _),"repeat E duplicated or replaced the creation");
            assert(batch.Complete(token,true) && batch.Phase==DummyBatchPhase.Recording && batch.Pose is null,"successful creation did not become ready to record");
            assert(batch.Record("pose B"),"second placement cannot be recorded");
        });
        test("Dummy batch failed creation keeps the recorded pose for retry",()=>
        {
            var batch=new DummyBatchState<string>(identity);batch.Record("pose A");batch.TryBeginCreation(0,0,out long first);
            assert(batch.Complete(first,false) && batch.Phase==DummyBatchPhase.Pending && batch.Pose=="pose A","failed creation lost the draft");
            assert(batch.TryBeginCreation(0,0,out long retry) && retry!=first,"retry reused the old operation identity");
            assert(!batch.Complete(first,true) && batch.Phase==DummyBatchPhase.Creating,"late success completed a different request");
            assert(batch.Complete(retry,true),"retry cannot complete");
        });
        test("Dummy batch R cancels a pose then exits and invalidates in-flight completion",()=>
        {
            var batch=new DummyBatchState<string>(identity);batch.Record("pose A");
            assert(batch.Back()==DummyBatchBackResult.CancelledPose && batch.Active && batch.Pose is null,"R failed to cancel just the recorded pose");
            batch.Record("pose B");batch.TryBeginCreation(0,0,out long first);
            assert(batch.Back()==DummyBatchBackResult.CancelledCreation && batch.Phase==DummyBatchPhase.Recording,"R failed to cancel creation");
            batch.Record("pose C");batch.TryBeginCreation(0,0,out long next);
            assert(!batch.Complete(first,true) && batch.Pose=="pose C","cancelled completion consumed the next pose");
            batch.Complete(next,true);
            assert(batch.Back()==DummyBatchBackResult.Exited && !batch.Active,"R from idle failed to exit");
            assert(!batch.Record("pose D") && !batch.TryBeginCreation(0,0,out _),"exited batch accepted input");
        });
        test("Dummy batch refuses ninth and shared-capacity placement while preserving retry",()=>
        {
            var batch=new DummyBatchState<string>(identity);batch.Record("pose H");
            assert(!batch.TryBeginCreation(8,8,out _) && !batch.TryBeginCreation(0,8,out _) && batch.Pose=="pose H","capacity allowed a ninth or lost the pending pose");
            assert(batch.TryBeginCreation(7,7,out long last) && batch.Complete(last,true),"eighth target refused");
            batch.Record("pose I");
            assert(!batch.TryBeginCreation(8,8,out _) && batch.Phase==DummyBatchPhase.Pending,"ninth started after successful eighth");
        });
        test("Dummy batch identity rejects respawn reconnect and map change without session revisions",()=>
        {
            var batch=new DummyBatchState<string>(identity);batch.Record("pose A");batch.TryBeginCreation(0,0,out long token);
            assert(batch.Matches(identity),"same player/world rejected");
            assert(!batch.Matches(identity with {Epoch=3}) && !batch.Matches(identity with {Controller=12}) &&
                !batch.Matches(identity with {Pawn=23}) && !batch.Matches(identity with {Steam=34}),"stale identity accepted");
            batch.Clear();
            assert(!batch.Matches(identity) && !batch.Complete(token,true) && batch.Pose is null,"clear allowed late completion to resurrect the draft");
        });
    }
}
