using System.Numerics;

namespace NadeLab.Core;

public static class ManualExecution
{
    // Check coupled motion errors, not just isolated +/-10u/s changes. In the
    // recorded 60u/s step-jump attempts, actual speed was only 30-39u/s.
    public static IEnumerable<(ThrowStart Start,Aim Aim)> Perturbations(ThrowStart start,Aim aim,PhysicsProfile physics)
        => Cases(start,aim,physics).Select(c=>(c.Start,c.Aim));
    public static IEnumerable<ExecutionTrial> Cases(ThrowStart start,Aim aim,PhysicsProfile physics)
    {
        if(aim.ForwardSpeed<=0) yield break;
        if(aim.EffectiveAction==ThrowAction.AirForwardJump)
        {
            // Controlled air acceleration is a different recipe from a ground
            // prestep. Keep the recorded takeoff point fixed, vary both inherited
            // speed and accumulated displacement. These conservative reference
            // samples are not a measured human-success probability.
            foreach(float change in new[]{-2f,2f})
                foreach(float shift in new[]{-2f,0,2f})
                {
                    float speed=MathF.Max(0,aim.ForwardSpeed+change);
                    yield return new(shift==0 ? "motion-speed" : "motion-timing",start,
                        aim with {ForwardSpeed=speed,ForwardOffset=MathF.Max(0,aim.ForwardOffset+change*JumpLatch.Seconds+shift)});
                }
            yield break;
        }
        bool step=aim.EffectiveAction is ThrowAction.Step or ThrowAction.StepJump;
        float delta=step ? MathF.Max(30,aim.ForwardSpeed*.3f) : MathF.Max(15,aim.ForwardSpeed*.1f);
        var direction=Geometry.Direction(0,aim.Yaw);
        foreach(float speedDelta in new[]{-delta,delta})
            foreach(float dt in new[]{-.1f,0,.1f})
            {
                float speed=Math.Clamp(aim.ForwardSpeed+speedDelta,0,300);
                float offset=aim.ForwardOffset+(aim.Airborne || step ? (speed-aim.ForwardSpeed)*(aim.Airborne ? JumpLatch.Seconds : physics.ReleaseDelaySeconds) : 0);
                var sample=start.Origin==ThrowOrigin.Preparation && aim.NeedsRunUp ? start with {RunUpDistance=start.RunUpDistance+speed*dt} : start with {Feet=start.Feet+direction*speed*dt};
                yield return new(dt==0 ? "motion-speed" : "motion-timing",sample,aim with {ForwardSpeed=speed,ForwardOffset=offset});
            }
    }
}
