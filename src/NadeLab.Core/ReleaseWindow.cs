using System.Numerics;

namespace NadeLab.Core;

// A contiguous run of passing samples around nominal release, not a measured
// human success probability or proof of every state between samples.
public sealed record ReleaseWindow(float EarlySeconds,float LateSeconds,float MinSpeed,float MaxSpeed,float SampleSeconds)
{
    public float Duration=>EarlySeconds+LateSeconds;
    public Vector2 ButtonOffsets(Aim aim,float releaseDelay)
    {
        float lower=float.NegativeInfinity,upper=float.PositiveInfinity;
        float basis=aim.Airborne ? aim.ForwardOffset-aim.ForwardSpeed*JumpLatch.Seconds : aim.ForwardOffset;
        float delay=aim.Airborne ? 0 : releaseDelay;
        foreach(float speed in new[]{MinSpeed,aim.ForwardSpeed,MaxSpeed})
        {
            float scale=MathF.Min(speed,aim.ForwardSpeed);
            lower=MathF.Max(lower,basis-EarlySeconds*scale-speed*delay);
            upper=MathF.Min(upper,basis+LateSeconds*scale-speed*delay);
        }
        return new(lower,upper);
    }
    public static ReleaseWindow? FromSamples(IReadOnlyList<bool> samples,float speed,float speedDelta)
    {
        if(samples.Count!=5 || !samples[2]) return null;
        int early=0,late=0;
        for(int i=1;i>=0 && samples[i];i--) early++;
        for(int i=3;i<5 && samples[i];i++) late++;
        return early+late==0 ? null : new(early*.05f,late*.05f,MathF.Max(0,speed-speedDelta),MathF.Min(300,speed+speedDelta),.05f);
    }
    public static IEnumerable<(int Index,ThrowStart Start,Aim Aim)> Cases(ThrowStart start,Aim aim,PhysicsProfile physics)
    {
        float delta=MathF.Max(15,aim.ForwardSpeed*.1f);
        for(int i=0;i<5;i++)
            foreach(float change in new[]{-delta,0,delta})
            {
                float speed=Math.Clamp(aim.ForwardSpeed+change,0,300);
                float distance=speed*((i-2)*.05f);
                var sample=start.Origin==ThrowOrigin.Preparation && aim.NeedsRunUp ? start with {RunUpDistance=start.RunUpDistance+distance} :
                    start with {Feet=start.Feet+Geometry.Direction(0,aim.Yaw)*distance};
                yield return (i,sample,aim with {ForwardSpeed=speed,ForwardOffset=aim.ForwardOffset+(aim.Airborne ? (speed-aim.ForwardSpeed)*JumpLatch.Seconds : 0)});
            }
    }
}
