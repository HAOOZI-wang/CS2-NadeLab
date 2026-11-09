using System.Numerics;

namespace NadeLab.Core;

public static class TrajectorySensitivity
{
    // Observed edge launches differed by about 16u from reconstructed sources.
    // These are stress cases, not fitted probabilities or a calibrated material model.
    public static IEnumerable<(Vector3 Source,PhysicsProfile Physics)> Cases(Vector3 source,Aim aim,PhysicsProfile physics,int wallBounces)
        => SourceCases(source,aim,physics).Concat(SurfaceCases(source,physics,wallBounces));
    public static IEnumerable<(Vector3 Source,PhysicsProfile Physics)> SourceCases(Vector3 source,Aim aim,PhysicsProfile physics)
    {
        var direction=Geometry.Direction(0,aim.Yaw);
        // The large discrepancy was observed for grounded stair launches.
        // Jump launches already use the separately verified engine latch; use
        // its existing position/height perturbations rather than extrapolating
        // a grounded source-reconstruction error to airborne releases.
        float horizontalError=aim.Airborne ? 2 : 16;
        float verticalError=aim.Airborne ? .25f : 2;
        foreach(float shift in new[]{-horizontalError,horizontalError})
            foreach(float rise in new[]{-verticalError,verticalError})
                yield return (source+direction*shift+new Vector3(0,0,rise),physics);
        yield return (source+new Vector3(0,0,verticalError),physics);
        yield return (source-new Vector3(0,0,verticalError),physics);
    }
    public static IEnumerable<(Vector3 Source,PhysicsProfile Physics)> SurfaceCases(Vector3 source,PhysicsProfile physics,int contacts)
    {
        if(contacts>0)
            foreach(float delta in new[]{-.03f,.03f})
            {
                yield return (source,physics with {Restitution=Math.Clamp(physics.Restitution+delta,0,1)});
                yield return (source,physics with {TangentialRetention=Math.Clamp(physics.TangentialRetention+delta,0,1)});
            }
    }
}
