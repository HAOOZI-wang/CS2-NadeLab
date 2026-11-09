using System.Numerics;

namespace NadeLab.Core;

// Shared by verification, search perturbations and the training HUD.
// Finite samples cover boundaries and coupled errors, not a success probability.
public static class ExecutionEnvelope
{
    public const float Angle = .5f, EyeHeight = 1, Speed = 15;
    public static float Position(Aim aim) => aim.ForwardSpeed>0 || aim.Airborne ? 8 : 4;
    public static IEnumerable<(ThrowStart Start,Aim Aim)> Perturbations(ThrowStart start,Aim aim)
        => Cases(start,aim).Select(c=>(c.Start,c.Aim));
    public static IEnumerable<ExecutionTrial> Cases(ThrowStart start,Aim aim)
    {
        float radius=Position(aim);
        foreach(var offset in new[]{new Vector3(radius,0,0),new Vector3(-radius,0,0),new Vector3(0,radius,0),new Vector3(0,-radius,0)})
            yield return new("position",start with {Feet=start.Feet+offset},aim);
        foreach(float pitch in new[]{-Angle,Angle})
            foreach(float yaw in new[]{-Angle,Angle})
            {
                var changed=aim with {Pitch=Math.Clamp(aim.Pitch+pitch,-89,89),Yaw=Geometry.NormalizeYaw(aim.Yaw+yaw)};
                yield return new("angle",start,changed);
                yield return new("coupled",start with {Feet=start.Feet+new Vector3(MathF.Sign(pitch),MathF.Sign(yaw),0)*(radius/MathF.Sqrt(2))},changed);
            }
        foreach(float angle in new[]{-Angle,Angle})
        {
            yield return new("angle",start,aim with {Pitch=Math.Clamp(aim.Pitch+angle,-89,89)});
            yield return new("angle",start,aim with {Yaw=Geometry.NormalizeYaw(aim.Yaw+angle)});
        }
        if(!aim.Airborne && aim.EffectiveAction!=ThrowAction.Crouch)
            foreach(float eye in new[]{-EyeHeight,EyeHeight}) yield return new("eye",start with {EyeHeight=start.EyeHeight+eye},aim);
    }
}
