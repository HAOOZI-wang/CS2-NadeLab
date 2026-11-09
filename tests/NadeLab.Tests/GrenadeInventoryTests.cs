using NadeLab.Core;

public static class GrenadeInventoryTests
{
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        test("Full grenade inventory releases one spare flash without losing its weapon",()=>
        {
            var plan=GrenadeInventory.Prepare(GrenadeKind.Decoy,
                [new(GrenadeKind.Smoke,1),new(GrenadeKind.Flash,2),new(GrenadeKind.HighExplosive,1),new(GrenadeKind.Molotov,1)],5,1);
            assert(plan.Give && plan.Changes.SequenceEqual([new GrenadeInventoryChange(GrenadeKind.Flash,1)]),
                "five grenades across four weapon entities left no slot, or removed a whole flash stack");
        });
        test("Full single-count inventory releases only one unselected grenade",()=>
        {
            var plan=GrenadeInventory.Prepare(GrenadeKind.Flash,
                [new(GrenadeKind.Smoke,1),new(GrenadeKind.HighExplosive,1),new(GrenadeKind.Decoy,1)],3,2);
            assert(plan.Give && plan.Changes.SequenceEqual([new GrenadeInventoryChange(GrenadeKind.Decoy,0)]),"full inventory was not minimally cleared");
        });
        test("Fire replacement clears the counterpart without evicting another grenade",()=>
        {
            foreach(var (wanted,old) in new[]{(GrenadeKind.Molotov,GrenadeKind.Incendiary),(GrenadeKind.Incendiary,GrenadeKind.Molotov)})
            {
                var plan=GrenadeInventory.Prepare(wanted,[new(old,1),new(GrenadeKind.Smoke,1),new(GrenadeKind.Flash,2)],4,1);
                assert(plan.Give && plan.Changes.SequenceEqual([new GrenadeInventoryChange(old,0)]),"fire exchange changed unrelated inventory");
            }
        });
        test("Existing selected grenade and its count survive a full or over-limit inventory",()=>
        {
            var plan=GrenadeInventory.Prepare(GrenadeKind.Flash,[new(GrenadeKind.Flash,2),new(GrenadeKind.Smoke,1)],1,1);
            assert(!plan.Give && plan.Changes.Count==0,"existing selected grenade was replaced or topped up");
        });
        test("Free inventory grants once without removing another grenade",()=>
        {
            var plan=GrenadeInventory.Prepare(GrenadeKind.Smoke,[new(GrenadeKind.Flash,2)],4,1);
            assert(plan.Give && plan.Changes.Count==0,"non-full inventory was cleared");
        });
        test("A pulled grenade blocks all inventory edits even when it is not selected",()=>
        {
            var plan=GrenadeInventory.Prepare(GrenadeKind.Smoke,[new(GrenadeKind.Flash,2,true),new(GrenadeKind.Decoy,1)],3,1);
            assert(!plan.Give && plan.Changes.Count==0,"pulled grenade inventory changed");
        });
        test("Disabled total or type capacity never destroys inventory on a futile grant",()=>
        {
            foreach(var (total,perType) in new[]{(0,1),(4,0),(-1,1),(4,-1)})
            {
                var plan=GrenadeInventory.Prepare(GrenadeKind.Molotov,[new(GrenadeKind.Incendiary,1)],total,perType);
                assert(!plan.Give && plan.Changes.Count==0,"unavailable capacity removed an existing grenade");
            }
        });
        test("A lowered total cap makes enough room without removing more than necessary",()=>
        {
            var plan=GrenadeInventory.Prepare(GrenadeKind.Smoke,
                [new(GrenadeKind.Flash,2),new(GrenadeKind.HighExplosive,1),new(GrenadeKind.Decoy,1)],2,1);
            assert(plan.Give && plan.Changes.SequenceEqual([new GrenadeInventoryChange(GrenadeKind.Flash,0),
                new GrenadeInventoryChange(GrenadeKind.Decoy,0)]),"lowered cap was ignored or excess inventory removed");
        });
        test("An empty selected weapon is removed before granting usable ammo",()=>
        {
            var plan=GrenadeInventory.Prepare(GrenadeKind.Smoke,[new(GrenadeKind.Smoke,0),new(GrenadeKind.Flash,2)],3,1);
            assert(plan.Give && plan.Changes.SequenceEqual([new GrenadeInventoryChange(GrenadeKind.Smoke,0)]),"empty entity counted as usable selected grenade");
        });
    }
}
