using System.Numerics;

namespace NadeLab.Core;

public readonly record struct RangeEdge(Vector3 A,Vector3 B);

/// <summary>Allowed grenade positions, not the smoke/fire/damage footprint.</summary>
public static class TargetRangeGeometry
{
    // All edges fit into the renderer's 26-entity allowance. The previous pair
    // of 16-sided rings silently lost the last six edges of the upper ring.
    public static IReadOnlyList<RangeEdge> Edges(Target target,bool showHeight)
    {
        target.Validate();
        var size=target.AreaHalfSize ?? new Vector3(target.Radius,target.Radius,target.Radius);
        var result=new List<RangeEdge>();
        if(!showHeight)
        {
            // A smoke draft can also show four other region centres (8 lines).
            // Sixteen perimeter edges keep that complete scene below 26.
            Ring(16,2);return result;
        }
        if(!target.IsArea)
        {
            // Precise points use a spherical distance check, not an elliptic
            // cylinder. Keep the full 3D outline truthful for this legacy mode.
            foreach(var edge in EffectRangeGeometry.Sphere(target.Position,target.Radius,8,1)) result.Add(edge);
            return result;
        }
        Ring(8,-size.Z);Ring(8,size.Z);
        for(int i=0;i<4;i++)
        {
            var offset=new Vector3(size.X*MathF.Cos(i*MathF.PI/2),size.Y*MathF.Sin(i*MathF.PI/2),0);
            result.Add(new(target.Position+offset-Vector3.UnitZ*size.Z,target.Position+offset+Vector3.UnitZ*size.Z));
        }
        return result;

        void Ring(int segments,float z)
        {
            Vector3 Point(int index)=>target.Position+new Vector3(size.X*MathF.Cos(index*MathF.Tau/segments),size.Y*MathF.Sin(index*MathF.Tau/segments),z);
            for(int i=0;i<segments;i++) result.Add(new(Point(i),Point((i+1)%segments)));
        }
    }
}

public static class EffectRangeGeometry
{
    public static IEnumerable<RangeEdge> Sphere(Vector3 center,float radius,int segments=12,float fraction=.65f)
    {
        if(!Geometry.Finite(center) || !float.IsFinite(radius) || radius<=0 || segments is <4 or >16 ||
            !float.IsFinite(fraction) || fraction is <=0 or >1) throw new ArgumentException("Invalid effect outline.");
        for(int plane=0;plane<3;plane++)
            for(int i=0;i<segments;i++)
            {
                Vector3 Point(float t)=>center+(plane switch
                {
                    0=>new Vector3(MathF.Cos(t),MathF.Sin(t),0),
                    1=>new Vector3(MathF.Cos(t),0,MathF.Sin(t)),
                    _=>new Vector3(0,MathF.Cos(t),MathF.Sin(t))
                })*radius;
                yield return new(Point(i*MathF.Tau/segments),Point((i+fraction)*MathF.Tau/segments));
            }
    }
}
