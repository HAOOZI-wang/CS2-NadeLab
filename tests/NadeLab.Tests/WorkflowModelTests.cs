using NadeLab.Core;

internal static class WorkflowModelTests
{
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        test("Workflow flash requirements remain editable while point search defers region editing",()=>
        {
            var flash=WorkflowModel.Resolve(new(Flash:true,Searching:true));
            var point=WorkflowModel.Resolve(new(HasTarget:true,HasOrigin:true,Searching:true));
            assert(flash.Availability(WorkflowAction.Region)==WorkflowAvailability.Available,"flash requirements incorrectly wait for search completion");
            assert(point.Availability(WorkflowAction.Region)==WorkflowAvailability.Deferred,"point region editor bypasses active search");
        });
        test("Workflow menus retain navigation while loading and drafts suppress unrelated commands",()=>
        {
            var loading=WorkflowModel.Resolve(new(Loading:true,Flash:true,HasCandidate:true));
            string keys=WorkflowKeys.Menu(loading,root:true);
            assert(keys.Contains("WASD移动") && keys.Contains("E/F8确定") && !keys.Contains("F2") && !keys.Contains("F5"),"loading advertises unrelated training actions or loses navigation");
            string draft=WorkflowKeys.Menu(WorkflowModel.Resolve(new(RegionDraft:true,Flash:true)));
            assert(draft.Contains("弃草稿") && !draft.Contains("F5") && !draft.Contains("F2要求"),"flash objective bypasses draft menu semantics");
        });
        test("Workflow search offers adoption only after a candidate has passed review",()=>
        {
            foreach(bool flash in new[]{false,true})
            {
                var snapshot=new WorkflowSnapshot(Flash:flash,Searching:true,HasTarget:true,HasOrigin:true);
                string pending=WorkflowKeys.Text(WorkflowModel.Resolve(snapshot));
                string reviewed=WorkflowKeys.Text(WorkflowModel.Resolve(snapshot with {ReviewedCandidate=true}));
                assert(pending.Contains("查看进度") && !pending.Contains("采用") && reviewed.Contains("采用"),"unreviewed search implies an adoptable candidate");
            }
        });
        test("Workflow special modes own primary action and suppress unrelated shortcuts",()=>
        {
            var overlap=new WorkflowSnapshot(Placement:true,Loading:true,RegionDraft:true,Flash:true,Searching:true,HasCandidate:true);
            var placement=WorkflowModel.Resolve(overlap);
            assert(placement.Primary==WorkflowAction.ConfirmPlacement,"placement lost primary action");
            assert(placement.Availability(WorkflowAction.Retarget) is null && placement.Availability(WorkflowAction.Camera)==WorkflowAvailability.Available,"placement advertises a target action or hides camera");
            var loading=WorkflowModel.Resolve(overlap with {Placement=false});
            assert(loading.Primary==WorkflowAction.OpenLoading && loading.Availability(WorkflowAction.Prepare) is null,"loading exposes candidate preparation");
            var draft=WorkflowModel.Resolve(overlap with {Placement=false,Loading=false});
            assert(draft.Primary==WorkflowAction.EditRegion && draft.Availability(WorkflowAction.FlashSearch) is null && draft.Availability(WorkflowAction.Retarget) is null,"draft can be bypassed by flash mode or retargeting");
        });
        test("Workflow point target actions follow target origin searching result and continuation",()=>
        {
            var cases=new (WorkflowSnapshot Snapshot,WorkflowAction Action)[]
            {
                (new(),WorkflowAction.SelectTarget),
                (new(HasTarget:true),WorkflowAction.CaptureOrigin),
                (new(HasTarget:true,HasOrigin:true),WorkflowAction.Search),
                (new(HasTarget:true,HasOrigin:true,Searching:true,HasCandidate:true),WorkflowAction.Prepare),
                (new(HasTarget:true,HasOrigin:true,HasCandidate:true,Continuation:true),WorkflowAction.Prepare),
                (new(HasTarget:true,HasOrigin:true,Continuation:true),WorkflowAction.ContinueSearch)
            };
            foreach(var (snapshot,action) in cases)
                assert(WorkflowModel.Resolve(snapshot).Primary==action,$"wrong action for {snapshot}");
        });
        test("Workflow flash objective bypasses point capture and keeps published candidates actionable",()=>
        {
            assert(WorkflowModel.Resolve(new(Flash:true)).Primary==WorkflowAction.FlashSearch,"flash requests a point target");
            var searching=WorkflowModel.Resolve(new(Flash:true,Searching:true,HasCandidate:true));
            assert(searching.Primary==WorkflowAction.Prepare && searching.Availability(WorkflowAction.NextCandidate)==WorkflowAvailability.Available,"ongoing search blocked published flash candidates");
            assert(WorkflowModel.Resolve(new(Flash:true,HasCandidate:true)).Primary==WorkflowAction.Prepare,"flash candidate cannot prepare");
        });
        test("Workflow flight keeps target selection live and defers origin preparation and stance changes",()=>
        {
            var target=WorkflowModel.Resolve(new(Flying:true));
            assert(target.Availability(WorkflowAction.SelectTarget)==WorkflowAvailability.Available,"flying target selection blocked");
            var origin=WorkflowModel.Resolve(new(HasTarget:true,Flying:true));
            assert(origin.Availability(WorkflowAction.CaptureOrigin)==WorkflowAvailability.Deferred,"airborne origin advertised as ready");
            foreach(bool flash in new[]{false,true})
            {
                var result=WorkflowModel.Resolve(new(Flash:flash,HasTarget:true,HasOrigin:true,HasCandidate:true,Flying:true));
                assert(result.Availability(WorkflowAction.Prepare)==WorkflowAvailability.Deferred && result.Availability(WorkflowAction.Reposition)==WorkflowAvailability.Deferred,"flight preparation or stance change advertised as ready");
                assert(result.Availability(WorkflowAction.NextCandidate)==WorkflowAvailability.Available,"flying candidate viewing disabled");
            }
        });
        test("Workflow preparation ready and recording retain result routing without claiming new search",()=>
        {
            var candidate=new WorkflowSnapshot(HasTarget:true,HasOrigin:true,HasCandidate:true);
            foreach(var snapshot in new[]{candidate with {Preparing=true},candidate with {PreparationBlocked=true},candidate with {Ready=true},candidate with {Recording=true}})
                assert(WorkflowModel.Resolve(snapshot).Primary==WorkflowAction.Prepare,"result lifecycle rerouted to a new search");
            var distinct=new[]{candidate,candidate with {Preparing=true},candidate with {PreparationBlocked=true},candidate with {Ready=true},candidate with {Recording=true}}
                .Select(s=>WorkflowModel.Resolve(s).Step).Distinct().Count();
            assert(distinct==5,"preparation lifecycle is invisible to the shared step model");
        });
        test("Workflow advertised primary action and availability agree across all reachable shortcut contexts",()=>
        {
            foreach(bool flying in new[]{false,true})
            foreach(var snapshot in new[]{new WorkflowSnapshot(),new(HasTarget:true),new(HasTarget:true,HasOrigin:true),new(HasTarget:true,HasOrigin:true,Searching:true),new(HasTarget:true,HasOrigin:true,HasCandidate:true),new(Flash:true),new(Flash:true,Searching:true),new(Flash:true,HasCandidate:true),new(Placement:true),new(RegionDraft:true),new(Loading:true)})
            {
                var resolved=WorkflowModel.Resolve(snapshot with {Flying=flying});
                assert(resolved.Availability(resolved.Primary) is not null,"primary dispatch has no matching shortcut");
                string keys=WorkflowKeys.Text(resolved);
                assert(PanelText.Shortcuts(keys)==keys && keys.Contains("F1"),"resolved shortcut budget/common menu key lost");
                if(resolved.Availability(resolved.Primary)==WorkflowAvailability.Deferred)
                    assert(keys.Contains("落地"),"deferred primary lacks landing requirement");
            }
        });
    }
}
