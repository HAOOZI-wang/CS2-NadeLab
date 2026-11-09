using System.Numerics;
namespace NadeLab.Core;

public static class PersonalFlashHints
{
    public static IReadOnlyList<Aim> Select(IEnumerable<PersonalLineup> entries,PhysicsProfile physics,ThrowStart start,string map,string? world)
    {
        string? key=WorldIdentity.Key(world,map);
        if(key is null || physics.Kind!=GrenadeKind.Flash || !Geometry.Finite(start.Feet) || !float.IsFinite(start.EyeHeight)) return [];
        // A v1 collection records initial aim and impact, not exact release
        // timing/strength. Try existing fixed-origin families; never treat an
        // impact or a free-text note as proof that the current objective passes.
        var nearby=entries.Where(e=>e.Kind==GrenadeKind.Flash && e.ActualImpact is not null && e.ThrowId is not null &&
            WorldIdentity.Key(e.World,map)==key && Geometry.Finite(e.Feet) && Vector3.DistanceSquared(start.Feet,e.Feet)<=64 &&
            float.IsFinite(e.EyeHeight) && MathF.Abs(start.EyeHeight-e.EyeHeight)<=4 &&
            float.IsFinite(e.Pitch) && e.Pitch is >=-89 and <=89 && float.IsFinite(e.Yaw))
            .OrderBy(e=>Vector3.DistanceSquared(start.Feet,e.Feet)).ThenByDescending(e=>e.SavedAt)
            .DistinctBy(e=>(e.Pitch,Geometry.NormalizeYaw(e.Yaw))).Take(4);
        var families=ThrowCatalog.Families(physics,new SearchOptions {IncludeActions=true},0,0)
            .Where(FlashSearchOptions.FixedAim).ToArray();
        return nearby.SelectMany(e=>families.Select(f=>f with {Pitch=e.Pitch,Yaw=Geometry.NormalizeYaw(e.Yaw)})).Distinct().Take(24).ToArray();
    }
}
