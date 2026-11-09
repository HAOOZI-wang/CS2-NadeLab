using NadeLab.Core;

internal static class CompanionInputTests
{
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        test("Dummy reset shortcut is contextual and never repeats or intercepts typing",()=>
        {
            var flow=WorkflowModel.Resolve(new());
            assert(!CompanionControls.Resolve(flow,false,false,false).Any(k=>k.Key==0x74),"empty scene intercepts F5");
            foreach(bool menu in new[]{false,true})
            {
                var keys=CompanionControls.Resolve(flow,menu,menu,false,true);
                assert(CompanionControls.Text(keys).Contains("F5 复位假人"),"reset shortcut is hidden");
                var router=new CompanionInputRouter();
                assert(router.Route(0x74,true,false,true,false,menu,keys,[]).Action=="resetdummies","F5 does not reset");
                assert(router.Route(0x74,true,false,true,false,menu,keys,[]).Action is null,"held F5 repeats reset");
                router.Route(0x74,false,false,true,false,menu,keys,[]);
                assert(!router.Route(0x74,true,false,true,true,menu,keys,[]).Block,"typing F5 intercepted");
            }
            foreach(var state in new[]{new WorkflowSnapshot(Loading:true),new WorkflowSnapshot(Placement:true)})
                assert(!CompanionControls.Resolve(WorkflowModel.Resolve(state),false,false,false,true).Any(k=>k.Key==0x74),"reset offered during loading/placement");
        });
        test("Companion pipe handshake preserves game timestamp and rejects wrong-session or replayed requests",()=>PipeRoundTrip(assert).GetAwaiter().GetResult());
        test("Companion acknowledgments wait for queued menu work and reset for a new peer",()=>
        {
            var ack=new CompanionAcknowledgements();ack.Observe(9);ack.Complete(false);
            assert(ack.Value==9,"immediate action not acknowledged");
            ack.Observe(10);ack.Complete(true);assert(ack.Value==9,"queued menu work acknowledged early");
            ack.Complete(false);assert(ack.Value==10,"finished menu work not acknowledged");
            assert(new CompanionAcknowledgements().Value==0,"new peer reused old acknowledgment");
        });
        test("Companion reconnect cannot steal a passed key-down or its matching release",()=>
        {
            var router=new CompanionInputRouter();var keys=CompanionControls.Resolve(WorkflowModel.Resolve(new()),false,false,false);
            assert(!router.Route(0x45,true,false,false,false,false,keys,[]).Block,"inactive press blocked");
            assert(!router.Route(0x45,true,false,true,false,false,keys,[]).Block,"autorepeat stolen on reconnect");
            assert(!router.Route(0x45,false,false,true,false,false,keys,[]).Block,"passed key release swallowed");
            assert(router.Route(0x45,true,false,true,false,false,keys,[]).Action=="primary","fresh press not rearmed");
        });
        test("Companion transport heartbeat cannot renew stale gameplay authority",()=>
        {
            var state=new CompanionSnapshot("peer","context",1000,true,true,[new(0x70,false,"menu","收起")]);
            assert(CompanionProtocol.CanRoute(state,1100,5),"fresh game frame rejected");
            assert(!CompanionProtocol.CanRoute(state,1800,5),"transport receipt renewed stale game state");
            assert(!CompanionProtocol.CanRoute(state with {Keys=[]},1100,5),"inactive game state accepted");
            assert(!CompanionProtocol.CanRoute(state,1100,800),"broken transport accepted");
        });
        test("Companion menu transitions block switch leakage until server acknowledgment",()=>
        {
            var gate=new CompanionTransition();
            gate.Begin("a",5,100);
            assert(gate.Pending("a",4,150),"transition released before acknowledgment");
            assert(!gate.Pending("a",5,151),"acknowledged transition stayed blocked");
            gate.Begin("a",6,200);assert(!gate.Pending("b",0,210),"transition survived peer replacement");
            gate.Begin("a",7,300);assert(!gate.Pending("a",0,1100),"transition outlived input lease");
        });
        test("Companion wheel leaves personal binds alone outside menus",()=>
        {
            var router=new CompanionInputRouter();
            var state=CompanionControls.Resolve(WorkflowModel.Resolve(new()),false,false,false);
            assert(!router.Route(-1,true,false,true,false,false,state,[]).Block,"wheel swallowed outside menu");
            var menu=CompanionControls.Resolve(WorkflowModel.Resolve(new()),true,true,false);
            var result=router.Route(-1,true,false,true,false,true,menu,[]);
            assert(result.Block && result.Action=="up","menu wheel not routed");
            assert(!router.Route(-1,true,false,false,false,true,menu,[]).Block,"stale/unfocused state swallowed wheel");
        });
        test("Companion switch blocking preserves movement and balances releases across menu close",()=>
        {
            var router=new CompanionInputRouter();var keys=CompanionControls.Resolve(WorkflowModel.Resolve(new()),true,true,false);
            assert(router.Route(0x51,true,false,true,false,true,keys,[0x51,0x57]).Block,"Q switch not blocked");
            assert(!router.Route(0x57,true,false,true,false,true,keys,[0x51,0x57]).Block,"W movement blocked");
            assert(router.Route(0x51,false,false,true,false,false,keys,[]).Block,"owned release leaked after menu closed");
            assert(!router.Route(0x51,true,false,true,false,false,keys,[]).Block,"Q did not recover outside menu");
            assert(!router.Route(0x51,false,false,true,false,true,keys,[0x51]).Block,"release of prior passed key swallowed");
        });
        test("Companion chord assignments and text match approved keys and F3 cycles",()=>
        {
            var keys=CompanionControls.Resolve(WorkflowModel.Resolve(new(HasTarget:true,HasOrigin:true,HasCandidate:true)),false,false,false);
            var router=new CompanionInputRouter();
            assert(router.Route(0x52,true,true,true,false,false,keys,[]).Action=="reposition","Shift+R wrong");
            assert(router.Route(0x45,true,true,true,false,false,keys,[]).Action=="retarget","Shift+E wrong");
            assert(keys.Single(x=>x.Key==0x72).Action=="grenade","F3 must cycle");
            var menu=CompanionControls.Resolve(WorkflowModel.Resolve(new()),true,true,false);
            assert(!menu.Any(x=>x.Shift),"Shift navigation returned");
            assert(CompanionControls.Text(menu).Contains("V 飞行") && CompanionControls.Text(menu).Contains("F1"),"available key missing");
        });
        test("Companion typing and OS modifiers never dispatch training commands",()=>
        {
            var router=new CompanionInputRouter();var keys=CompanionControls.Resolve(WorkflowModel.Resolve(new()),true,true,false);
            assert(!router.Route(0x45,true,false,true,true,true,keys,[]).Block,"typing E swallowed");
            router.Route(0x45,false,false,true,true,true,keys,[]);
            assert(!router.Route(0x70,true,false,false,false,true,keys,[]).Block,"unfocused F1 intercepted");
        });
        test("Companion continuous dummy placement offers Shift+E without restoring Shift navigation",()=>
        {
            var keys=CompanionControls.Resolve(WorkflowModel.Resolve(new()),false,false,true);
            var router=new CompanionInputRouter();
            assert(router.Route(0x45,true,true,true,false,false,keys,[]).Action=="retarget","batch cannot cancel into retarget");
            assert(CompanionControls.Text(keys).Contains("Shift+E 重选目标"),"batch retarget hidden from shortcut guide");
            assert(!keys.Any(k=>k.Shift && k.Action is "up" or "down"),"old Shift navigation returned");
        });
        test("Companion reads weapon and chat bindings without treating attacks as switches",()=>
        {
            var profile=BindingProfile.Parse("\"bindings\" { \"MWHEELUP\" \"slot6\" \"MWHEELDOWN\" \"slot10\" \"q\" \"lastinv\" \"mouse5\" \"use weapon_flashbang\" \"mouse1\" \"+attack\" \"u\" \"messagemode2\" \"`\" \"toggleconsole\" }");
            assert(profile.SwitchKeys.SetEquals([-1,-2,0x51,6]),"weapon switches not recognized");
            assert(profile.ChatKeys.Contains(0x55)&&profile.ConsoleKeys.Contains(0xC0),"text input keys lost");
        });
    }
    private static async Task PipeRoundTrip(Action<bool,string> assert)
    {
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        string name="NadeLab.Tests."+Guid.NewGuid().ToString("N");
        using var server=new CompanionPipeServer(name);
        string originalPeer;
        {
        using var pipe=new System.IO.Pipes.NamedPipeClientStream(".",name,System.IO.Pipes.PipeDirection.InOut,System.IO.Pipes.PipeOptions.Asynchronous);
        await pipe.ConnectAsync(stop.Token);
        using var reader=new StreamReader(pipe,System.Text.Encoding.UTF8,false,1024,true);
        using var writer=new StreamWriter(pipe,new System.Text.UTF8Encoding(false),1024,true){AutoFlush=true};
        async Task Send(CompanionRequest request)=>await writer.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(request).AsMemory(),stop.Token);
        await Send(new("","",0,"hello",42));
        while(server.Peer is null) await Task.Delay(5,stop.Token);
        var peer=server.Peer;originalPeer=peer.Id;assert(peer.SteamId==42,"hello identity changed");
        assert(!peer.InputActive,"unconfirmed foreground began restricting equipment");
        await Send(new(peer.Id,"fixture",0,"heartbeat",InputActive:true));
        while(!peer.InputActive) await Task.Delay(5,stop.Token);
        peer.Snapshot=new(peer.Id,"fixture",1000,true,true,[new(0x70,false,"menu","菜单")]);
        CompanionSnapshot? frame;
        do {frame=System.Text.Json.JsonSerializer.Deserialize<CompanionSnapshot>((await reader.ReadLineAsync(stop.Token))!);} while(frame?.Context!="fixture");
        assert(frame.SentAt==1000 && frame.Session==peer.Id,"transport renewed old game authority");
        await Send(new("wrong",frame.Context,1,"select"));
        await Send(new("wrong",frame.Context,0,"heartbeat",InputActive:false));
        await Send(new(peer.Id,frame.Context,2,"select"));
        await Send(new(peer.Id,frame.Context,2,"select"));
        await Send(new(peer.Id,frame.Context,3,"back"));
        while(Interlocked.Read(ref peer.Sequence)<3) await Task.Delay(5,stop.Token);
        assert(peer.Requests.Count==2,"wrong-session or duplicate request entered game queue");
        assert(peer.InputActive,"wrong-session heartbeat changed input ownership");
        await Send(new(peer.Id,frame.Context,0,"heartbeat",InputActive:false));
        while(peer.InputActive) await Task.Delay(5,stop.Token);
        }
        while(server.Peer is not null) await Task.Delay(5,stop.Token);
        using var reconnect=new System.IO.Pipes.NamedPipeClientStream(".",name,System.IO.Pipes.PipeDirection.InOut,System.IO.Pipes.PipeOptions.Asynchronous);
        await reconnect.ConnectAsync(stop.Token);
        using var againReader=new StreamReader(reconnect,System.Text.Encoding.UTF8,false,1024,true);
        using var againWriter=new StreamWriter(reconnect,new System.Text.UTF8Encoding(false),1024,true){AutoFlush=true};
        await againWriter.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(new CompanionRequest("","",0,"hello",42)).AsMemory(),stop.Token);
        var again=System.Text.Json.JsonSerializer.Deserialize<CompanionSnapshot>((await againReader.ReadLineAsync(stop.Token))!);
        assert(again is not null && again.Session!=originalPeer && again.Acknowledged==0,"reconnected peer retained old authority");
    }
}
