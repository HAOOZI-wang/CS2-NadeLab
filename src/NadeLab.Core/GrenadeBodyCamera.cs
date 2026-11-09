using System.Numerics;

namespace NadeLab.Core;

public readonly record struct GrenadeBodyCameraPose(Vector3 Position,Vector3 Angles);

public static class GrenadeBodyCamera
{
    public static GrenadeBodyCameraPose Resolve(ICollisionWorld world,Vector3 center,float pitch,float yaw,bool thirdPerson)
    {
        ArgumentNullException.ThrowIfNull(world);
        if(!Geometry.Finite(center) || !float.IsFinite(pitch) || !float.IsFinite(yaw)) throw new ArgumentException("Invalid grenade camera pose.");
        var angles=new Vector3(Math.Clamp(pitch,-89,89),Geometry.NormalizeYaw(yaw),0);
        if(!thirdPerson) return new(center,angles);
        var desired=center-Geometry.Direction(angles.X,angles.Y)*72+Vector3.UnitZ*12;
        var hit=world.Sweep(center,desired,2);
        Vector3 camera=hit.StartSolid || !Geometry.Finite(hit.Position) || !float.IsFinite(hit.Fraction) || hit.Fraction is <0 or >1 ? center :
            hit.Hit ? center+(desired-center)*MathF.Max(0,hit.Fraction-.01f) : desired;
        var toward=center-camera;
        if(toward.LengthSquared()<.01f) return new(center,angles);
        float horizontal=new Vector2(toward.X,toward.Y).Length();
        var look=new Vector3(-MathF.Atan2(toward.Z,horizontal)*180/MathF.PI,MathF.Atan2(toward.Y,toward.X)*180/MathF.PI,0);
        return new(camera,look);
    }
}
