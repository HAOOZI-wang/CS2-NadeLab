using System.Numerics;

namespace NadeLab.Core;

public readonly record struct LandingRegion(Vector3 Center,Vector3 HalfSize)
{
    public float Margin(Vector3 p)
    {var d=p-Center;return MathF.Min(1-MathF.Sqrt(d.X*d.X/(HalfSize.X*HalfSize.X)+d.Y*d.Y/(HalfSize.Y*HalfSize.Y)),1-MathF.Abs(d.Z)/HalfSize.Z);}
    public bool Contains(Vector3 p)=>Geometry.Finite(p) && Margin(p)>=0;
    public float Distance(Vector3 p)
    {
        var d=p-Center;
        float xy=MathF.Max(0,MathF.Sqrt(d.X*d.X/(HalfSize.X*HalfSize.X)+d.Y*d.Y/(HalfSize.Y*HalfSize.Y))-1)*MathF.Min(HalfSize.X,HalfSize.Y);
        float z=MathF.Max(0,MathF.Abs(d.Z)-HalfSize.Z);return MathF.Sqrt(xy*xy+z*z);
    }
    public void Validate()
    {
        if(!Geometry.Finite(Center) || !Geometry.Finite(HalfSize) || HalfSize.X is <1 or >256 || HalfSize.Y is <1 or >256 || HalfSize.Z is <1 or >64)
            throw new ArgumentException("Invalid landing region.");
    }
}

// A player-selected vertical face and optional viewing pose. This is an intent
// marker and a search hint, never a claim about the engine's smoke volume.
public sealed record SmokeObjective(Vector3 Center,Vector3 Normal,float HalfWidth,float HalfHeight,Vector3? ObserverEye=null,Aim? ObserverView=null)
{
    public Vector3 Right=>Vector3.Cross(Vector3.UnitZ,Normal);
    public static SmokeObjective FromCorners(Vector3 first,Vector3 second,Vector3 normal)
    {
        if(!Geometry.Finite(first) || !Geometry.Finite(second) || !Geometry.Finite(normal) || MathF.Abs(normal.Z)>.35f || normal.LengthSquared()<.5f)
            throw new ArgumentException("Select a vertical frame.");
        normal=Vector3.Normalize(new Vector3(normal.X,normal.Y,0));
        var d=second-first;if(MathF.Abs(Vector3.Dot(d,normal))>16) throw new ArgumentException("Corners must lie on the same face.");
        second-=normal*Vector3.Dot(d,normal);
        var result=new SmokeObjective((first+second)/2,normal,MathF.Abs(Vector3.Dot(second-first,Vector3.Cross(Vector3.UnitZ,normal)))/2,MathF.Abs(second.Z-first.Z)/2);
        result.Validate();return result;
    }
    public void Validate()
    {
        if(!Geometry.Finite(Center) || !Geometry.Finite(Normal) || MathF.Abs(Normal.Z)>.001f || MathF.Abs(Normal.Length()-1)>.001f ||
            !float.IsFinite(HalfWidth) || HalfWidth is <8 or >256 || !float.IsFinite(HalfHeight) || HalfHeight is <8 or >256 ||
            (ObserverEye.HasValue!=ObserverView.HasValue) || (ObserverEye is { } eye && !Geometry.Finite(eye)) ||
            (ObserverView is { } view && (!float.IsFinite(view.Pitch) || view.Pitch is <-89 or >89 || !float.IsFinite(view.Yaw))))
            throw new ArgumentException("Invalid smoke objective.");
    }
    public IReadOnlyList<Vector3> Outline()=>[Center-Right*HalfWidth-Vector3.UnitZ*HalfHeight,Center+Right*HalfWidth-Vector3.UnitZ*HalfHeight,
        Center+Right*HalfWidth+Vector3.UnitZ*HalfHeight,Center-Right*HalfWidth+Vector3.UnitZ*HalfHeight,Center-Right*HalfWidth-Vector3.UnitZ*HalfHeight];
    public bool SameView(Vector3 eye,Aim view)=>ObserverEye is { } planned && ObserverView is { } direction &&
        Vector3.Distance(eye,planned)<=32 && SceneAdvisor.ViewTurn(view,direction)<=10;
}
