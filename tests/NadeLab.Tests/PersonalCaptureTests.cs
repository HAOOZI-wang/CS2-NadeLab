using NadeLab.Core;
using System.Numerics;

static class PersonalCaptureTests
{
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        test("Collection recall permits stable same-stance height variation before teleport",()=>
        {
            bool stable=PersonalPose.Stable(true,true,Vector3.Zero,0,61.875f);
            assert(PersonalPose.CanRecall(stable,61.875f,63.703125f),"CT spawn standing pose rejected before reaching saved origin");
            assert(PersonalPose.CanRecall(true,46,48),"same crouched stance rejected before teleport");
            assert(!PersonalPose.CanRecall(true,46,64) && !PersonalPose.CanRecall(true,64,46),"different stances accepted");
            assert(!PersonalPose.CanRecall(false,64,64),"unstable pose accepted");
        });
        test("Collection recall rejects invalid view heights",()=>
        {
            foreach(float invalid in new[]{float.NaN,float.PositiveInfinity,0f,100f})
                assert(!PersonalPose.CanRecall(true,invalid,invalid),"invalid matching heights accepted");
        });
        test("Personal poses allow settled crouch but reject transitioning and moving poses",()=>
        {
            var zero=System.Numerics.Vector3.Zero;
            assert(PersonalPose.Stable(true,true,zero,0,64),"standing rejected");
            assert(PersonalPose.Stable(true,true,zero,1,46),"settled crouch rejected");
            assert(!PersonalPose.Stable(true,true,zero,.5f,55),"transition accepted");
            assert(!PersonalPose.Stable(true,false,zero,1,46),"airborne accepted");
            assert(!PersonalPose.Stable(false,true,zero,1,46),"noclip accepted");
            assert(!PersonalPose.Stable(true,true,new(5,0,0),1,46),"moving accepted");
            assert(!PersonalPose.Stable(true,true,zero,float.NaN,46),"NaN posture accepted");
        });
        test("Personal recording binds one exact release and consumes impact once",()=>
        {
            var owner=new ImpactOwner(2,3,4);var gate=new PersonalCapture(owner,5,GrenadeKind.Flash,10);
            assert(gate.Release(owner,5,GrenadeKind.Flash,11),"first release rejected");
            assert(!gate.Complete(owner,5,GrenadeKind.Flash,10.9f,12),"different release accepted");
            assert(!gate.Complete(owner,6,GrenadeKind.Flash,11,12),"different pawn accepted");
            assert(gate.Complete(owner,5,GrenadeKind.Flash,11,12),"own impact rejected");
            assert(!gate.Complete(owner,5,GrenadeKind.Flash,11,12),"duplicate impact accepted");
        });
        test("Personal recording refuses concurrent releases, wrong kind, old world and expired events",()=>
        {
            var owner=new ImpactOwner(2,3,4);var gate=new PersonalCapture(owner,5,GrenadeKind.Flash,10);
            assert(!gate.Release(owner,5,GrenadeKind.Smoke,11),"wrong kind release");
            assert(!gate.Release(owner with {Epoch=3},5,GrenadeKind.Flash,11),"old world release");
            assert(gate.Release(owner,5,GrenadeKind.Flash,12),"valid release missing");
            assert(!gate.Release(owner,5,GrenadeKind.Flash,13),"second release accepted");
            assert(!gate.Complete(owner,5,GrenadeKind.Flash,12,14),"ambiguous shot captured");
            var expired=new PersonalCapture(owner,5,GrenadeKind.Smoke,10);
            assert(!expired.Release(owner,5,GrenadeKind.Smoke,131),"late release accepted");
            var fuse=new PersonalCapture(owner,5,GrenadeKind.Smoke,10);
            assert(fuse.Release(owner,5,GrenadeKind.Smoke,11),"release");
            assert(!fuse.Complete(owner,5,GrenadeKind.Smoke,11,42),"late impact accepted");
        });
    }
}
