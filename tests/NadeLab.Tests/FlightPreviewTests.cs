using NadeLab.Core;
internal static class FlightPreviewTests
{
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        test("Overlapping releases suppress camera instead of replacing its subject",()=>{
            var state=new FlightPreviewState();state.Prepare(1,1.08);
            var first=state.Begin(1.08);state.Bind(first,100);
            state.Prepare(2,2.08);var second=state.Begin(2.08);state.Bind(second,200);
            assert(!state.Visible(2.1),"overlapping flight reopened native camera");
            state.Finish(100);
            assert(!state.Visible(2.2),"first effect revived second flight camera");
            state.Finish(200);state.Prepare(3,3.08);
            assert(state.Visible(3.01),"fresh single throw did not recover");
        });
        test("Native preview primes only an imminent committed release",()=>{
            var state=new FlightPreviewState();
            assert(!state.Prepare(10,0) && !state.Visible(10),"holding a pin opened preview");
            assert(!state.Prepare(10,9) && !state.Prepare(10,12),"stale or remote throw time accepted");
            assert(!state.Prepare(double.NaN,10) && !state.Prepare(10,double.PositiveInfinity),"nonfinite schedule accepted");
            assert(state.Prepare(10,10.08) && state.Visible(10.01),"committed release did not prime camera");
            assert(!state.Active(10.01),"priming was mislabeled as real flight");
            assert(state.Refresh(10.05,true) && state.Visible(10.05),"maintenance erased priming before release");
            var token=state.Begin(10.08);state.Bind(token,100);
            assert(state.Visible(11) && state.Finish(100) && !state.Visible(11),"release/effect handoff failed");
        });
        test("Interrupted preview priming expires or closes immediately when ineligible",()=>{
            var state=new FlightPreviewState();state.Prepare(10,10.08);
            assert(!state.Refresh(10.5,true) && !state.Visible(10.5),"missing release left camera open");
            state.Prepare(20,20.08);
            assert(!state.Visible(19),"clock rollback preserved pending camera");
            assert(!state.Refresh(20.01,false) && !state.Visible(20.02),"death/disable did not close pending camera");
        });
        test("Flight preview needs a release and ends on its own projectile",()=>{
            var state=new FlightPreviewState();
            assert(!state.Active(1),"idle preview visible");
            var token=state.Begin(1);
            assert(state.Active(1) && state.Bind(token,100),"actual release not tracked");
            assert(!state.Finish(101) && state.Active(2),"foreign effect ended preview");
            assert(state.Finish(100) && !state.Active(2),"own effect retained preview");
        });
        test("Delayed binding tracks both releases without reviving either camera",()=>{
            var state=new FlightPreviewState();var old=state.Begin(1);state.Bind(old,100);
            var current=state.Begin(2);
            assert(state.Bind(old,100) && state.Bind(current,200),"lost a live release binding");
            assert(state.Finish(100) && state.Active(3) && !state.Visible(3),"old completion lost or revived other flight");
            assert(!state.Cancel(old) && !state.Bind(old,300),"finished token was accepted again");
            assert(state.Finish(200) && !state.Active(3),"new flight cannot finish");
        });
        test("Flight preview expires and cleared callbacks cannot reopen it",()=>{
            var state=new FlightPreviewState();var token=state.Begin(1);state.Clear();
            assert(!state.Bind(token,100) && !state.Active(2),"cancelled release revived");
            state.Begin(5);assert(!state.Active(36),"missing effect never expires");
            assert(!state.Active(4),"map clock rollback retained preview");
        });
        test("Disabled preview retains live releases but never reopens them",()=>{
            var state=new FlightPreviewState();state.Prepare(.92,1);var token=state.Begin(1);
            assert(!state.Refresh(2,false) && state.Active(2),"disable discarded live release");
            assert(state.Bind(token,100) && !state.Refresh(3,true),"toggle reopened an old camera");
            state.Finish(100);state.Prepare(5,5.08);
            assert(state.Visible(5.01),"new throw did not recover after toggle");
            state.Clear();assert(!state.Bind(token,100),"cleared owner accepted a callback");
        });
        test("Preparing then cancelling a second grenade never revives the first",()=>{
            var state=new FlightPreviewState();state.Prepare(1,1.08);state.Bind(state.Begin(1.08),100);
            state.ObserveHolding(true);
            assert(state.Paused && !state.Visible(2),"second preparation did not pause immediately");
            state.ObserveHolding(false);
            assert(!state.Visible(2.1),"cancellation reopened first camera");
            state.Finish(100);state.Prepare(3,3.08);
            assert(state.Visible(3.01),"cancelled preparation stranded next throw");
        });
        test("Holding next grenade cannot prolong pause after previous flights ended",()=>{
            var state=new FlightPreviewState();state.Prepare(1,1.08);state.Bind(state.Begin(1.08),100);
            state.ObserveHolding(true);state.Finish(100);
            state.Prepare(3,3.08);state.ObserveHolding(false);state.Bind(state.Begin(3.08),200);
            assert(state.Visible(3.1) && !state.Paused,"held next throw inherited a completed batch pause");
            state.Finish(200);state.Prepare(4,4.08);state.ObserveHolding(false);
            assert(state.Visible(4.01),"completed batch did not reset");
        });
        test("Repeated training recovers every next release once preceding smoke has finished",()=>{
            var state=new FlightPreviewState();state.Prepare(1,1.08);state.Bind(state.Begin(1.08),100);
            for(uint i=100;i<110;i++)
            {
                var at=2+(i-100)*3;
                state.ObserveHolding(true);
                assert(!state.Visible(at),"next preparation should hide current camera");
                state.Finish(i);
                assert(!state.Paused && !state.Visible(at+.1),"completed smoke kept batch paused or opened held prediction");
                state.Prepare(at+1,at+1.08);state.ObserveHolding(false);
                assert(state.Visible(at+1.01),"fresh release after smoke effect did not prime");
                state.Bind(state.Begin(at+1.08),i+1);
            }
        });
        test("Recovery while holding still waits for every actual flight to finish",()=>{
            var state=new FlightPreviewState();state.Bind(state.Begin(1),100);state.Bind(state.Begin(2),200);
            state.ObserveHolding(true);state.Finish(100);
            assert(state.Paused && !state.Visible(3),"one remaining flight incorrectly allowed recovery");
            state.Finish(200);
            assert(!state.Paused && !state.Visible(3),"last effect did not restore ready state while holding");
            state.Prepare(4,4.08);state.ObserveHolding(false);
            assert(state.Visible(4.01),"ready next release remained hidden");
        });
        test("An already committed suppressed release cannot reopen when previous grenade ends",()=>{
            var state=new FlightPreviewState();state.Prepare(1,1.08);state.Bind(state.Begin(1.08),100);
            state.ObserveHolding(true);state.Prepare(2,2.08);state.ObserveHolding(false);
            state.Finish(100);
            assert(!state.Visible(2.05),"effect reopened a release already committed during overlap");
            state.Bind(state.Begin(2.08),200);
            assert(!state.Visible(2.1),"suppressed commit reopened after thrown event");
        });
        test("Repeated throw animation observations cannot rearm a finished release",()=>{
            var state=new FlightPreviewState();state.Prepare(1,1.08);state.Bind(state.Begin(1.05),100);
            assert(!state.Prepare(1.06,1.08),"repeated observation rearmed camera");
            state.Finish(100);assert(!state.Prepare(1.07,1.08) && !state.Visible(1.07),"finished release reopened");
        });
        test("Missed prime and failed association hide safely with bounded recovery",()=>{
            var state=new FlightPreviewState();var token=state.Begin(1);
            assert(!state.Visible(1.1),"unprimed event enabled camera too late");
            assert(state.Cancel(token) && state.InFlightCount==1,"failed association forgot a live release");
            state.Prepare(2,2.08);state.Bind(state.Begin(2.08),200);
            state.Finish(200);assert(!state.Visible(3),"unknown flight allowed premature recovery");
            state.Refresh(32,true);state.Prepare(33,33.08);
            assert(state.Visible(33.01),"unknown association never expired");
        });
        test("Bound flight stays suppressed until explicit cleanup even past watchdog",()=>{
            var state=new FlightPreviewState();state.Bind(state.Begin(1),100);state.ObserveHolding(true);
            state.ObserveHolding(false);state.Refresh(32,true);state.Prepare(33,33.08);
            assert(!state.Visible(33.01) && state.InFlightCount==1,"still-live bound grenade expired");
            state.Clear();assert(!state.Visible(33.02) && state.InFlightCount==0,"owner reset retained a flight");
        });
        test("Burst cleanup remains deterministic through reverse-order callbacks",()=>{
            var state=new FlightPreviewState();state.Prepare(1,1.08);var tokens=new List<long>();
            for(int i=0;i<100;i++) tokens.Add(state.Begin(1.08+i*.01));
            for(int i=99;i>=0;i--) assert(state.Bind(tokens[i],(uint)(100+i)),"lost deferred association");
            for(int i=0;i<100;i++) { assert(!state.Visible(3),"burst reopened preview");state.Finish((uint)(100+i)); }
            assert(state.InFlightCount==0 && !state.Paused,"burst left residual tracking");
            state.Prepare(4,4.08);assert(state.Visible(4.01),"burst stranded single throw");
        });
        test("Clock rollback preserves live handles and cannot replay the same animation",()=>{
            var state=new FlightPreviewState();state.Prepare(10,10.08);state.Bind(state.Begin(10.08),100);
            assert(!state.Refresh(9.9,true),"rollback retained camera");
            assert(state.InFlightCount==1 && !state.Prepare(10,10.08) && !state.Visible(10.01),"rollback forgot live flight or replayed animation");
            state.Finish(100);state.Prepare(11,11.08);
            assert(state.Visible(11.01),"rollback stranded subsequent new throw");
        });
        test("Rollback rebases unknown association watchdog without forgetting it",()=>{
            var state=new FlightPreviewState();state.Begin(100);state.Refresh(1,true);
            assert(state.InFlightCount==1 && !state.Visible(1),"rollback discarded unknown release");
            state.Refresh(32,true);state.Prepare(33,33.08);
            assert(state.Visible(33.01),"rollback left unknown release pending for old clock duration");
        });
    }
}
