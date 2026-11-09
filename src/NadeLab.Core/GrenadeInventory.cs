namespace NadeLab.Core;

public readonly record struct GrenadeInventoryItem(GrenadeKind Kind,int Count,bool InUse=false);
public readonly record struct GrenadeInventoryChange(GrenadeKind Kind,int Remaining);
public sealed record GrenadeInventoryPlan(bool Give,IReadOnlyList<GrenadeInventoryChange> Changes);

public static class GrenadeInventory
{
    public static GrenadeInventoryPlan Prepare(GrenadeKind selected,IReadOnlyList<GrenadeInventoryItem> inventory,int totalLimit,int selectedLimit)
    {
        if(!Enum.IsDefined(selected) || inventory.Any(item=>item.InUse) || totalLimit<=0 || selectedLimit<=0)
            return new(false,[]);
        // One grenade weapon can represent two flashes. Counts are the pawn's
        // ammo quantities, never the number of weapon entities in its inventory.
        var counts=inventory.GroupBy(item=>item.Kind).ToDictionary(group=>group.Key,group=>Math.Max(0,group.Max(item=>item.Count)));
        if(counts.GetValueOrDefault(selected)>0) return new(false,[]);
        var changes=new Dictionary<GrenadeKind,int>();
        if(counts.ContainsKey(selected)) changes[selected]=0;
        if(selected.Fire())
        {
            var counterpart=selected==GrenadeKind.Molotov ? GrenadeKind.Incendiary : GrenadeKind.Molotov;
            if(counts.ContainsKey(counterpart)) {changes[counterpart]=0;counts[counterpart]=0;}
        }
        long excess=counts.Values.Sum(count=>(long)count)+1-totalLimit;
        // Preserve each carried type where possible by consuming spare ammo first.
        foreach(var kind in counts.Keys.OrderBy(kind=>kind).ToArray())
        {
            if(excess<=0) break;
            int remove=(int)Math.Min(excess,Math.Max(0,counts[kind]-1));
            if(remove==0) continue;
            counts[kind]-=remove;excess-=remove;changes[kind]=counts[kind];
        }
        foreach(var kind in new[]{GrenadeKind.Decoy,GrenadeKind.Flash,GrenadeKind.HighExplosive,
            GrenadeKind.Molotov,GrenadeKind.Incendiary,GrenadeKind.Smoke})
        {
            if(excess<=0) break;
            if(counts.GetValueOrDefault(kind)<=0) continue;
            excess-=counts[kind];counts[kind]=0;changes[kind]=0;
        }
        return excess>0 ? new(false,[]) : new(true,changes.Select(change=>new GrenadeInventoryChange(change.Key,change.Value)).ToArray());
    }
}
