using System.Numerics;

namespace NadeLab.Core;

public static class PersonalSmokeHints
{
    private const int MaximumHints=12;

    public static Aim[] Select(IEnumerable<PersonalLineup> entries,PhysicsProfile physics,ThrowStart start,string map,string? world,SearchOptions options)
    {
        string? key=WorldIdentity.Key(world,map);
        if(key is null || physics.Kind!=GrenadeKind.Smoke || !Geometry.Finite(start.Feet) || !float.IsFinite(start.EyeHeight)) return [];
        // V1 records the initial pose and an actual impact, not release action,
        // timing or strength. These angles must pass the ordinary search checks;
        // neither the impact nor the free-text instructions supply success proof.
        var nearby=entries.Where(e=>e.Kind==GrenadeKind.Smoke && e.ActualImpact is { } impact && Geometry.Finite(impact) &&
            !string.IsNullOrWhiteSpace(e.ThrowId) && WorldIdentity.Key(e.World,map)==key &&
            Geometry.Finite(e.Feet) && Vector3.DistanceSquared(start.Feet,e.Feet)<=64 &&
            float.IsFinite(e.EyeHeight) && MathF.Abs(start.EyeHeight-e.EyeHeight)<=4 &&
            float.IsFinite(e.Pitch) && e.Pitch is >=-89 and <=89 && float.IsFinite(e.Yaw))
            .OrderBy(e=>Vector3.DistanceSquared(start.Feet,e.Feet)).ThenByDescending(e=>e.SavedAt)
            .DistinctBy(e=>(e.Pitch,Geometry.NormalizeYaw(e.Yaw))).Take(2).ToArray();
        if(nearby.Length==0) return [];
        // Current request families own all launch parameters and permissions.
        // Rotate bookmarks within each family so the second angle is tried early.
        var families=ThrowCatalog.Families(physics,options,0,0)
            .Where(f=>f.EffectiveAction is ThrowAction.Jump or ThrowAction.AirForwardJump or ThrowAction.Standing)
            .OrderBy(f=>f.Mode).ThenBy(f=>f.EffectiveAction switch {ThrowAction.Jump=>0,ThrowAction.AirForwardJump=>1,_=>2});
        return families.SelectMany(f=>nearby.Select(e=>f with {Pitch=e.Pitch,Yaw=Geometry.NormalizeYaw(e.Yaw)}))
            .Distinct().Take(MaximumHints).ToArray();
    }

    public static Aim[] Combine(IEnumerable<Aim> prior,IEnumerable<Aim> personal)
    {
        using var routes=prior.Distinct().GetEnumerator();
        using var bookmarks=personal.Distinct().GetEnumerator();
        var result=new List<Aim>(MaximumHints);var seen=new HashSet<Aim>();
        bool hasRoutes=true,hasBookmarks=true;
        while(result.Count<MaximumHints && (hasRoutes || hasBookmarks))
        {
            if(hasRoutes && (hasRoutes=routes.MoveNext()) && seen.Add(routes.Current)) result.Add(routes.Current);
            if(result.Count<MaximumHints && hasBookmarks && (hasBookmarks=bookmarks.MoveNext()) && seen.Add(bookmarks.Current)) result.Add(bookmarks.Current);
        }
        return result.ToArray();
    }
}
