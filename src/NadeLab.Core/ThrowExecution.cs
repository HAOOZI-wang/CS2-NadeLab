using System.Numerics;

namespace NadeLab.Core;

public readonly record struct ThrowExecutionPlan(Vector3 PreparationFeet,ThrowStart SimulationStart,
    Vector3 ButtonFeet,Vector3 ReleaseFeet);

public static class ThrowExecution
{
    public const float RunUpDistance=100;
    public static bool Valid(ThrowStart start)=>Geometry.Finite(start.Feet) && float.IsFinite(start.EyeHeight) &&
        start.EyeHeight is >=40 and <=80 && Enum.IsDefined(start.Origin) &&
        (start.Origin==ThrowOrigin.Release || float.IsFinite(start.RunUpDistance) && start.RunUpDistance is >=60 and <=160);

    // Pure geometry: native player-hull support/clearance is checked separately,
    // incrementally, for each candidate and each perturbed execution request.
    // Keeping Origin in the request means yaw/speed perturbations resolve their
    // own release reference instead of rotating around an obsolete release point.
    public static ThrowExecutionPlan Resolve(ThrowStart start,Aim aim,PhysicsProfile physics)
    {
        if(!Valid(start)) throw new ArgumentException("Invalid throwing origin.");
        var direction=Geometry.Direction(0,aim.Yaw);
        var basis=start with {Origin=ThrowOrigin.Release};
        if(start.Origin==ThrowOrigin.Preparation && aim.NeedsRunUp)
        {
            var button=start.Feet+direction*start.RunUpDistance;
            basis=basis with {Feet=button+(aim.Airborne ? Vector3.Zero : direction*aim.ForwardSpeed*physics.ReleaseDelaySeconds)};
        }
        var release=aim.ReleaseFeet(basis);
        var buttonFeet=aim.Airborne ? basis.Feet : release-direction*aim.ForwardSpeed*physics.ReleaseDelaySeconds;
        return new(start.Feet,basis,buttonFeet,release);
    }

    public static (Vector3 Early,Vector3 Late) WindowEdges(ThrowStart start,Aim aim,PhysicsProfile physics,ReleaseWindow window)
    {
        var plan=Resolve(start,aim,physics);var direction=Geometry.Direction(0,aim.Yaw);
        bool fixedStart=start.Origin==ThrowOrigin.Preparation && aim.NeedsRunUp;
        var offsets=window.ButtonOffsets(aim,fixedStart ? 0 : physics.ReleaseDelaySeconds);
        var basis=fixedStart ? plan.ButtonFeet : plan.SimulationStart.Feet;
        return (basis+direction*offsets.X,basis+direction*offsets.Y);
    }
}
