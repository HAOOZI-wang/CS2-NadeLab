using System.Numerics;

namespace NadeLab.Core;

public sealed record SpawnGroundMark(Vector3 Center,IReadOnlyList<Vector3> Corners,Vector3 Normal)
{
    // point_worldtext is in its local XY plane. Rotate local +Z to the
    // ground normal while keeping a fixed horizontal reading direction.
    public Vector3 TextAngles=>new(MathF.Atan2(Normal.X,Normal.Z)*180/MathF.PI,0,
        -MathF.Asin(Math.Clamp(Normal.Y,-1,1))*180/MathF.PI);
}

public static class SpawnGroundGeometry
{
    public static bool TryCreate(Vector3 origin,Func<Vector3,Vector3?> ground,out SpawnGroundMark mark)
    {
        mark=null!;
        if(!Geometry.Finite(origin)) return false;
        Vector3? Project(Vector3 p)
        {
            if(ground(p) is not { } floor || !Geometry.Finite(floor) ||
                MathF.Abs(floor.X-p.X)>.01f || MathF.Abs(floor.Y-p.Y)>.01f) return null;
            return floor+Vector3.UnitZ*.5f;
        }
        if(Project(origin) is not { } center) return false;
        Vector3[] offsets=[new(-24,-24,0),new(24,-24,0),new(24,24,0),new(-24,24,0)];
        var corners=new Vector3[4];
        for(int i=0;i<4;i++)
        {
            if(Project(origin+offsets[i]) is not { } corner || MathF.Abs(corner.Z-center.Z)>24) return false;
            corners[i]=corner;
        }
        var normal=Vector3.Normalize(Vector3.Cross(corners[1]-corners[0],corners[3]-corners[0]));
        if(!Geometry.Finite(normal) || normal.Z<.7f) return false;
        mark=new(center,corners,normal);return true;
    }
}
