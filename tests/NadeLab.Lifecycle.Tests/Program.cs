using NadeLab.Plugin;

int passed=0;
void Test(string name,Action test) {test();passed++;Console.WriteLine("PASS "+name);}
void Assert(bool value,string reason) {if(!value) throw new InvalidOperationException(reason);}

Test("A stable pawn does not invalidate a newly bound preparation",()=>
{
    var life=new PreparationLifetime();
    Assert(!life.Observe(1,100,true),"initial bind invalidated");
    Assert(!life.Observe(1,100,true),"same life invalidated");
});
Test("Death invalidates once and respawn invalidates again",()=>
{
    var life=new PreparationLifetime();life.Observe(1,100,true);
    Assert(life.Observe(1,100,false),"death retained preparation");
    Assert(!life.Observe(1,100,false),"every dead tick invalidates again");
    Assert(life.Observe(1,101,true),"respawn retained preparation");
});
Test("Immediate pawn replacement needs no intervening dead tick",()=>
{
    var life=new PreparationLifetime();life.Observe(1,100,true);
    Assert(life.Observe(1,101,true),"replacement retained preparation");
});
Test("Missing pawn and map reset invalidate preparation",()=>
{
    var life=new PreparationLifetime();life.Observe(1,100,true);
    Assert(life.Observe(1,null,false),"missing pawn retained preparation");
    Assert(life.Observe(2,100,true),"new map retained preparation");
});
Test("Arm expires at its deadline without requiring a throw",()=>
{
    Assert(!PreparationLifetime.Expired(null,200),"empty record expired");
    Assert(!PreparationLifetime.Expired(90,89.99f),"expired early");
    Assert(PreparationLifetime.Expired(90,90),"deadline did not expire");
    Assert(PreparationLifetime.Expired(90,180),"stale Arm never expires");
});

var session=new object();var draft=new object();
var original=new PlayerActionIdentity(1,22,33,44,session,draft);
Test("Unchanged deferred action remains executable",()=>Assert(original.Matches(original),"valid callback rejected"));
Test("Actions cannot cross map or controller or pawn replacement",()=>
{
    Assert(!original.Matches(original with {Epoch=2}),"cross-map callback accepted");
    Assert(!original.Matches(original with {Controller=23}),"controller replacement accepted");
    Assert(!original.Matches(original with {Pawn=34}),"respawn callback accepted");
    Assert(!original.Matches(original with {Steam=45}),"slot reuse accepted");
});
Test("Actions cannot cross existing session replacement",()=>
    Assert(!original.Matches(original with {Session=new object()}),"old session callback accepted"));
Test("Cancelled and replaced placement cannot receive old toggles",()=>
{
    Assert(!original.Matches(original with {Placement=null}),"old F5 could reopen cancelled placement");
    Assert(!original.Matches(original with {Placement=new object()}),"old confirm could commit new draft");
    Assert(!(original with {Placement=null}).Matches(original),"old normal action entered a new placement");
});
Test("A first command may create a session without bypassing placement identity",()=>
{
    var first=original with {Session=null,Placement=null};
    Assert(first.Matches(original with {Placement=null}),"initial session creation rejected");
    Assert(!first.Matches(original),"new placement bypassed first-command guard");
});
Test("One pending action per slot, consumed only once",()=>
{
    var queue=new PendingPlayerActions();
    Assert(queue.TryEnqueue(2,out var token),"enqueue failed");
    Assert(!queue.TryEnqueue(2,out _),"duplicate enqueue accepted");
    Assert(queue.Take(2,token),"original callback rejected");
    Assert(!queue.Take(2,token),"callback executed twice");
});
Test("A callback from before Reset cannot remove the new world's callback",()=>
{
    var queue=new PendingPlayerActions();queue.TryEnqueue(2,out var old);
    queue.Clear();Assert(queue.TryEnqueue(2,out var current),"new map could not enqueue");
    Assert(!queue.Take(2,old),"old callback consumed new token");
    Assert(!queue.TryEnqueue(2,out _),"new pending action was removed");
    Assert(queue.Take(2,current),"new callback no longer runnable");
});
var logTailDirectory=Path.Combine(Path.GetTempPath(),"nadelab-tail-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(logTailDirectory);
try
{
    string logPath=Path.Combine(logTailDirectory,"evidence.jsonl");
    Test("Evidence tail bounds line count and ignores an unfinished append",()=>
    {
        File.WriteAllText(logPath,"{\"id\":1}\r\n{\"id\":2}\r\n{\"id\":3}\n{\"id\":4}");
        Assert(BoundedLogTail.ReadLines(logPath,1024,2).SequenceEqual(new[]{"{\"id\":2}","{\"id\":3}"}),"tail retained incomplete or old records");
    });
    Test("Evidence tail skips a cut UTF-8 prefix and retains complete records",()=>
    {
        File.WriteAllText(logPath,"{\"text\":\""+new string('烟',4096)+"\"}\n{\"id\":2}\n{\"id\":3}\n");
        Assert(BoundedLogTail.ReadLines(logPath,64,128).SequenceEqual(new[]{"{\"id\":2}","{\"id\":3}"}),"partial UTF-8 record escaped tail bound");
    });
    Test("Evidence tail keeps a first record beginning exactly on the byte bound",()=>
    {
        File.WriteAllText(logPath,"older\nfirst\nlast\n");
        Assert(BoundedLogTail.ReadLines(logPath,11,128).SequenceEqual(new[]{"first","last"}),"boundary-aligned complete record lost");
    });
    Test("Evidence tail tolerates empty files and records larger than its budget",()=>
    {
        File.WriteAllText(logPath,"");Assert(BoundedLogTail.ReadLines(logPath).Length==0,"empty log failed");
        File.WriteAllText(logPath,new string('x',1000)+"\n");Assert(BoundedLogTail.ReadLines(logPath,64).Length==0,"truncated oversized record admitted");
    });
}
finally {File.Delete(Path.Combine(logTailDirectory,"evidence.jsonl"));Directory.Delete(logTailDirectory);}
Test("Timer policy disposal restores only its bit and preserves unrelated later changes",()=>
{
    var policy=new OwnedPowerPolicy();policy.Capture(0,0);policy.Applied();
    Assert(policy.TryRestore(5,1,out uint control,out uint state) && control==1 && state==1,"unrelated power policy lost");
});
Test("Timer policy disposal preserves a later external timer setting",()=>
{
    var policy=new OwnedPowerPolicy();policy.Capture(4,4);policy.Applied();
    Assert(!policy.TryRestore(4,4,out _,out _),"later explicit opt-out overwritten");
    Assert(!policy.TryRestore(0,0,out _,out _),"later OS-default policy overwritten");
});
Test("Timer policy reads and failed writes never acquire restoration ownership",()=>
{
    var policy=new OwnedPowerPolicy();policy.Capture(4,4);
    Assert(!policy.TryRestore(4,0,out _,out _),"an unapplied request acquired ownership");
    policy.Applied();policy.Capture(4,0);
    Assert(policy.TryRestore(5,0,out uint control,out uint state) && control==5 && state==4,"repeated reads replaced original policy");
});
Test("Struct vector writes use schema stride and preserve earlier entries",()=>
{
    var memory=System.Runtime.InteropServices.Marshal.AllocHGlobal(192);
    try
    {
        for(int i=0;i<192;i++) System.Runtime.InteropServices.Marshal.WriteByte(memory,i,0x5a);
        var second=NativeStructVector.Element(2,1,64,()=>memory);
        System.Runtime.InteropServices.Marshal.WriteInt32(second,0,123);
        Assert(System.Runtime.InteropServices.Marshal.ReadInt32(memory,64)==123,"typed element did not use schema stride");
        for(int i=0;i<64;i++) Assert(System.Runtime.InteropServices.Marshal.ReadByte(memory,i)==0x5a,"earlier correction changed");
        for(int i=128;i<192;i++) Assert(System.Runtime.InteropServices.Marshal.ReadByte(memory,i)==0x5a,"following memory changed");
    }
    finally {System.Runtime.InteropServices.Marshal.FreeHGlobal(memory);}
});
Test("Struct vector rejects invalid bounds before requesting native storage",()=>
{
    int calls=0;
    foreach(var (count,index,size) in new[]{(0,0,64),(1,-1,64),(1,1,64),(-1,0,64),(65,0,64),(1,0,0),(1,0,4097)})
    {
        bool rejected=false;
        try {NativeStructVector.Element(count,index,size,()=>{calls++;return (nint)4096;});}
        catch(InvalidOperationException) {rejected=true;}
        Assert(rejected,"unsafe bounds accepted");
    }
    Assert(calls==0,"invalid queue requested native storage");
});
Test("Struct vector reacquires storage after growth and rejects null storage",()=>
{
    nint storage=4096;
    Assert(NativeStructVector.Element(1,0,64,()=>storage)==4096,"initial base changed");
    storage=8192;
    Assert(NativeStructVector.Element(2,1,64,()=>storage)==8256,"old storage retained after growth");
    bool rejected=false;
    try {NativeStructVector.Element(1,0,64,()=>0);}
    catch(InvalidOperationException) {rejected=true;}
    Assert(rejected,"null storage accepted");
});
Test("Live player teleport may replace one correction without increasing queue length",()=>
{
    ViewCorrectionSnapshot old=new(54,1,-20,30,0), fresh=new(55,1,0,-168.34198f,0);
    Assert(ViewCorrectionOwnership.Select([old],[fresh],-168.34198f,false)==0,"observed 54 -> 55 replacement was rejected");
});
Test("Teleport owns only the fresh absolute correction and preserves retained entries",()=>
{
    ViewCorrectionSnapshot old=new(5,2,12,40,0), fresh=new(6,1,0,179,0);
    Assert(ViewCorrectionOwnership.Select([], [fresh],179,false)==0,"initial correction rejected");
    Assert(ViewCorrectionOwnership.Select([old],[old,fresh],179,false)==1,"append rejected");
    Assert(ViewCorrectionOwnership.Select([old,old with {Index=4}],[fresh],179,false)==0,"engine coalescence rejected");
    Assert(ViewCorrectionOwnership.Select([old],[old],179,true)==-1,"confirmed bot without correction rejected");
});
Test("Stale ambiguous relative or wrong-angle corrections cannot acquire ownership",()=>
{
    ViewCorrectionSnapshot old=new(5,1,0,179,0), fresh=new(6,1,0,179,0);
    void Reject(ViewCorrectionSnapshot[] before,ViewCorrectionSnapshot[] after,bool bot=false)
    {
        bool refused=false;
        try {ViewCorrectionOwnership.Select(before,after,179,bot);} catch(InvalidOperationException) {refused=true;}
        Assert(refused,"unowned correction admitted");
    }
    Reject([old],[old]);Reject([old],[]);Reject([],[]);
    Reject([old],[fresh,fresh with {Index=7}]);
    Reject([old],[old with {Pitch=1},fresh]);
    Reject([old],[fresh with {Type=2}]);
    Reject([old],[fresh with {Pitch=10}]);Reject([old],[fresh with {Yaw=170}]);Reject([old],[fresh with {Roll=2}]);
    Reject([old],[fresh with {Yaw=float.NaN}]);Reject([old],[fresh with {Pitch=float.PositiveInfinity}]);
    Reject([old],[old with {Pitch=1}],true);
});
Console.WriteLine($"{passed} lifecycle, bounded-log, policy and view-correction checks passed; native event ordering is not exercised.");
