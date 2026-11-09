using System.Numerics;

namespace NadeLab.Core;

public static class SafePlacement
{
    public static bool TryStepSupport(Vector3 recorded,SweepHit support,Func<Vector3,bool> clear,out Vector3 position,float maxStep=18)
    {
        position=recorded;
        if(!Geometry.Finite(recorded) || !support.Hit || support.StartSolid || !Geometry.Finite(support.Position) || support.Normal.Z<.7f ||
            support.Position.Z<recorded.Z-.125f || support.Position.Z-recorded.Z>maxStep-.1f) return false;
        var proposed=new Vector3(recorded.X,recorded.Y,MathF.Max(recorded.Z,support.Position.Z)+.1f);
        if(!clear(proposed)) return false;
        position=proposed; return true;
    }
    // Never lower the recorded feet into geometry. Only small upward corrections
    // are allowed; the real player hull (including player clips) validates them.
    public static bool TryRaise(Vector3 recorded,Func<Vector3,bool> canStand,out Vector3 position)
    {
        position=recorded;
        if (!Geometry.Finite(recorded)) return false;
        foreach (float rise in new[]{.5f,1f,2f,3f})
        {
            var candidate=recorded+new Vector3(0,0,rise);
            if (!canStand(candidate)) continue;
            position=candidate; return true;
        }
        return false;
    }
}
