using System.Numerics;

namespace NadeLab.Core;

public readonly record struct SpawnLocation(int Id,Vector3 Position,float Yaw,bool Enabled,int Priority=0);
public readonly record struct NumberedSpawn(int Number,SpawnLocation Location);

public static class SpawnTravel
{
    // Five players draw from priorities in ascending order. Keep every tied
    // point in the last required tier, not just an arbitrary first five.
    public static IReadOnlyList<NumberedSpawn> CompetitiveCatalog(IEnumerable<SpawnLocation> locations)
    {
        var valid=locations.Where(s=>s.Enabled && Geometry.Finite(s.Position) && float.IsFinite(s.Yaw))
            .OrderBy(s=>s.Priority).ToArray();
        if(valid.Length==0) return [];
        int boundary=valid[Math.Min(4,valid.Length-1)].Priority;
        return Catalog(valid.Where(s=>s.Priority<=boundary));
    }

    // Position order survives entity ID changes. Disabled and occupied points
    // retain their number so recorded lineups remain usable.
    public static IReadOnlyList<NumberedSpawn> Catalog(IEnumerable<SpawnLocation> locations)=>locations
        .Where(s=>Geometry.Finite(s.Position) && float.IsFinite(s.Yaw))
        .OrderBy(s=>s.Position.X).ThenBy(s=>s.Position.Y).ThenBy(s=>s.Position.Z).ThenBy(s=>s.Yaw).ThenBy(s=>s.Id)
        .Select((s,i)=>new NumberedSpawn(i+1,s)).ToArray();

    public static bool TryResolve(IReadOnlyList<NumberedSpawn> catalog,int number,Func<SpawnLocation,Vector3?> resolve,out SpawnLocation selected)
    {
        selected=default;
        if(number<1 || number>catalog.Count) return false;
        var entry=catalog[number-1];var spot=entry.Location;
        if(entry.Number!=number || !spot.Enabled || !Geometry.Finite(spot.Position) || !float.IsFinite(spot.Yaw)) return false;
        if(resolve(spot) is not { } feet || !Geometry.Finite(feet) ||
            MathF.Abs(feet.X-spot.Position.X)>.01f || MathF.Abs(feet.Y-spot.Position.Y)>.01f) return false;
        selected=spot with {Position=new Vector3(spot.Position.X,spot.Position.Y,feet.Z)};return true;
    }

    public static bool TrySelect(IEnumerable<SpawnLocation> locations,Func<SpawnLocation,Vector3?> resolve,out SpawnLocation selected)
    {
        selected=default;
        foreach(var spot in locations.Where(s=>s.Enabled && Geometry.Finite(s.Position) && float.IsFinite(s.Yaw)).OrderBy(s=>s.Id))
        {
            if(resolve(spot) is not { } feet || !Geometry.Finite(feet)) continue;
            selected=spot with {Position=feet};return true;
        }
        return false;
    }
}
