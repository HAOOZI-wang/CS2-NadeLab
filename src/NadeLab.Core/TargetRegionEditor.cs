using System.Numerics;

namespace NadeLab.Core;

public static class TargetRegionEditor
{
    public static Target AsArea(Target target) => target with
    {
        AreaHalfSize=target.AreaHalfSize ?? new Vector3(target.Radius,target.Radius,24),
        Label="自选落点范围（封烟效果待确认）"
    };
    public static Target Resize(Target target,float horizontalScale,float verticalDelta=0)
    {
        if(!float.IsFinite(horizontalScale) || horizontalScale<=0 || !float.IsFinite(verticalDelta)) throw new ArgumentException("Invalid region adjustment.");
        var area=AsArea(target);var size=area.AreaHalfSize!.Value;
        var result=area with {AreaHalfSize=new Vector3(Math.Clamp(size.X*horizontalScale,8,256),Math.Clamp(size.Y*horizontalScale,8,256),Math.Clamp(size.Z+verticalDelta,4,64))};
        result=result with {Radius=MathF.Max(result.AreaHalfSize!.Value.X,result.AreaHalfSize.Value.Y)};
        result.Validate();return result;
    }
    public static Target ResizeAxis(Target target,bool x,float scale)
    {
        if(!float.IsFinite(scale) || scale<=0) throw new ArgumentException("Invalid axis adjustment.");
        var area=AsArea(target);var size=area.AreaHalfSize!.Value;
        if(x) size.X=Math.Clamp(size.X*scale,8,256);else size.Y=Math.Clamp(size.Y*scale,8,256);
        var result=area with {AreaHalfSize=size,Radius=MathF.Max(size.X,size.Y)};
        result.Validate();return result;
    }
    public static Target WithEdge(Target target,Vector3 edge)
    {
        if(!Geometry.Finite(edge)) throw new ArgumentException("Invalid edge.");
        float radius=Vector2.Distance(new(target.Position.X,target.Position.Y),new(edge.X,edge.Y));
        if(radius is <8 or >256) throw new ArgumentException("Edge must be 8 to 256 units from the center.");
        var area=AsArea(target);
        if(MathF.Abs(edge.Z-target.Position.Z)>area.AreaHalfSize!.Value.Z) throw new ArgumentException("Edge is outside the selected height range.");
        return area with {AreaHalfSize=new Vector3(radius,radius,area.AreaHalfSize!.Value.Z),Radius=radius};
    }
    // Selection: 0 main region, 1..2 extra allowed, -1..-2 exclusions.
    public static Target Selected(Target target,int selection)=>selection==0 ? target : new(
        (selection>0 ? target.ExtraRegions![selection-1] : target.Exclusions![-selection-1]).Center,
        AreaHalfSize:(selection>0 ? target.ExtraRegions![selection-1] : target.Exclusions![-selection-1]).HalfSize);
    public static Target Edit(Target target,int selection,Func<Target,Target> edit)
    {
        var changed=edit(Selected(target,selection));
        if(selection==0) return changed;
        var regions=(selection>0 ? target.ExtraRegions! : target.Exclusions!).ToArray();
        regions[Math.Abs(selection)-1]=new(changed.Position,changed.AreaHalfSize!.Value);
        var result=selection>0 ? target with {ExtraRegions=regions} : target with {Exclusions=regions};result.Validate();return result;
    }
    public static Target Add(Target target,Vector3 center,bool exclude)
    {
        var regions=(exclude ? target.Exclusions : target.ExtraRegions)?.ToList() ?? [];
        if(regions.Count>=2) throw new ArgumentException("At most two additional regions of each kind.");
        regions.Add(new(center,new(exclude ? 32 : 64,exclude ? 32 : 64,target.AreaHalfSize!.Value.Z)));
        var result=exclude ? target with {Exclusions=regions.ToArray()} : target with {ExtraRegions=regions.ToArray()};result.Validate();return result;
    }
    public static Target Remove(Target target,int selection)=>selection==0 ? target : selection>0 ?
        target with {ExtraRegions=target.ExtraRegions!.Where((_,i)=>i!=selection-1).ToArray()} :
        target with {Exclusions=target.Exclusions!.Where((_,i)=>i!=-selection-1).ToArray()};
    public static int[] Selections(Target target)=>new[]{0}.Concat(Enumerable.Range(1,target.ExtraRegions?.Count ?? 0)).Concat(Enumerable.Range(1,target.Exclusions?.Count ?? 0).Select(i=>-i)).ToArray();
    public static IReadOnlyList<Vector3> Outline(Target target,int segments=24)
    {
        var area=AsArea(target);var size=area.AreaHalfSize!.Value;
        // A perimeter only, never a filled surface or an upright wall of beams.
        return Enumerable.Range(0,segments+1).Select(i=>area.Position+new Vector3(
            size.X*MathF.Cos(i*MathF.Tau/segments),size.Y*MathF.Sin(i*MathF.Tau/segments),2)).ToArray();
    }
}
