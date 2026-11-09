using System.Numerics;
using NadeLab.Core;

static class LaunchObservationTests
{
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        test("Jump launch verification requires the immediate capture instead of a later trajectory point",()=>
        {
            var source=new Vector3(100,200,90);var speed=new Vector3(540,-30,500);
            const float age=2f/64,gravity=320;
            var later=source+speed*age-new Vector3(0,0,.5f*gravity*age*age);
            float tick=1f/64;
            var captured=source+speed*tick-new Vector3(0,0,.5f*gravity*tick*tick);
            assert(!Verification.MatchesJumpLaunch(source,speed,speed,later,tick),"later trajectory point should not widen the launch contract");
            assert(Verification.MatchesJumpLaunch(source,speed,speed,captured,tick),"immediate snapshot rejected");
            assert(!Verification.MatchesJumpLaunch(source,speed,speed+new Vector3(9,0,0),captured,tick),"velocity threshold loosened");
            assert(!Verification.MatchesJumpLaunch(source,speed,speed,captured+new Vector3(7,0,0),tick),"position threshold loosened");
        });
        test("Jump launch capture rejects invalid and divergent positions",()=>
        {
            var source=new Vector3(0,0,100);var speed=new Vector3(400,0,300);
            assert(!Verification.MatchesJumpLaunch(source,speed,speed,new Vector3(float.NaN,0,0),1f/64),"invalid position accepted");
            assert(!Verification.MatchesJumpLaunch(source,speed,speed,source-new Vector3(7,0,0),1f/64),"divergent position accepted");
            assert(Verification.MatchesJumpLaunch(source,speed,speed,source,1f/64),"construction snapshot rejected");
        });
    }
}
