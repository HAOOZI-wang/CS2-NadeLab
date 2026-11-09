using System.Numerics;

namespace NadeLab.Core;

public static class NearbyStart
{
    // A bounded forward reposition, only over a continuously supported clear
    // route. The player is moved later by the existing explicit prepare action.
    public static ThrowStart? Find(ThrowStart start,Target target,Func<Vector3,Vector3?> support,Func<Vector3,Vector3,bool> clear)
    {
        var delta=target.Position-start.Feet; delta.Z=0;
        if(!target.IsArea || delta.Length()<1500) return null;
        var direction=Vector3.Normalize(delta);
        var previous=start.Feet;
        for(int distance=8;distance<=96;distance+=8)
        {
            var proposed=start.Feet+direction*distance; proposed.Z=previous.Z;
            var next=support(proposed);
            if(next is null || !Geometry.Finite(next.Value) || MathF.Abs(next.Value.Z-previous.Z)>18 ||
                !clear(previous,next.Value)) return null;
            previous=next.Value;
        }
        return start with {Feet=previous,EyeHeight=64};
    }
}
