using System.Numerics;
using NadeLab.Core;

public static class ReleaseCueTests
{
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        // Reflection permits recording an executable RED before the API exists.
        object Frame(Vector3 feet,Vector3 velocity,Aim aim,ReleaseWindow? window=null,bool aligned=true)
        {
            var type=typeof(Aim).Assembly.GetType("NadeLab.Core.ReleaseCue");
            assert(type is not null,"near-reticle release cue is missing");
            return type!.GetMethod("Build")!.Invoke(null,[new ThrowStart(Vector3.Zero,64,ThrowOrigin.Preparation),aim,new PhysicsProfile(),feet,velocity,aligned,window])!;
        }
        T Value<T>(object frame,string name)=>(T)frame.GetType().GetProperty(name)!.GetValue(frame)!;
        var walk=new Aim(-75,0,ThrowMode.Left,127.4f,ThrowAction.Walk);
        test("Release cue anticipates a sky-facing run without looking down",()=>
        {
            var start=Frame(Vector3.Zero,Vector3.Zero,walk);
            var moving=Frame(new(50,0,0),new(127.4f,0,0),walk);
            assert(Value<string>(start,"Text").Contains("W+Shift"),"ready cue omits executable movement keys");
            assert(Value<float>(moving,"Progress")>.45f && Value<float>(moving,"Progress")<.55f,"progress is not linked to the button location");
            assert(Value<string>(moving,"Text").Contains("准备") && Value<string>(moving,"Text").Contains("50u"),"no advance distance/action cue");
        });
        test("Cue never claims a sampled release window for invalid speed or unsampled timing",()=>
        {
            var window=new ReleaseWindow(.1f,.1f,115,140,.05f);
            var valid=Frame(new(100,0,0),new(127.4f,0,0),walk,window);
            assert(Value<ReleaseStage>(valid,"Stage")==ReleaseStage.Window && Value<string>(valid,"Text").Contains("采样区"),"verified sample window not represented");
            foreach(var frame in new[]{Frame(new(100,0,0),Vector3.Zero,walk,window),Frame(new(100,0,0),new(127.4f,0,0),walk,null),Frame(new(100,0,0),new(127.4f,0,0),walk,window,false)})
                assert(Value<ReleaseStage>(frame,"Stage")!=ReleaseStage.Window && !Value<string>(frame,"Text").Contains("采样区"),"cue falsely claims a usable sampled window");
            var past=Frame(new(130,0,0),new(127.4f,0,0),walk,window);
            assert(Value<ReleaseStage>(past,"Stage")==ReleaseStage.Passed && Value<string>(past,"Text").Contains("重新就位"),"past release line still invites throw");
        });
        test("Jump-run cue uses jump and release at the button line",()=>
        {
            var jump=walk with {Action=ThrowAction.RunJump,ForwardOffset=127.4f*JumpLatch.Seconds};
            var frame=Frame(new(80,0,0),new(127.4f,0,0),jump);
            assert(Value<string>(frame,"Text").Contains("跳+松鼠标") && Value<float>(frame,"Progress")>.79f,"jump cue uses the later projectile release position");
        });
    }
}
