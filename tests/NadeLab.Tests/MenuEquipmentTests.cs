using NadeLab.Core;
static class MenuEquipmentTests
{
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        test("Menu equipment preserves the held entity and restores exact inventory handles",()=>
        {
            uint[] original=[41,72,83,94];var masked=MenuEquipment.Mask(original,72);
            assert(masked.SequenceEqual([uint.MaxValue,72u,uint.MaxValue,uint.MaxValue]),"other items stayed selectable");
            var restored=MenuEquipment.Restore(masked,original,_=>true);
            assert(restored.Handles.SequenceEqual(original) && restored.Overflow.Length==0,"inventory identity/order changed");
        });
        test("Menu equipment restore never reclaims transferred items or overwrites a new inventory",()=>
        {
            uint[] original=[41,72,83];uint[] current=[uint.MaxValue,72,99];
            assert(MenuEquipment.Restore(current,original,h=>h!=83).Handles.SequenceEqual([41u,72u,99u]),"new or transferred item overwritten");
            var recovery=MenuEquipment.Restore([99u],original,_=>true);
            assert(recovery.Handles.SequenceEqual([99u]) && recovery.Overflow.SequenceEqual(original),"missing handles must be retained for explicit slot recovery");
        });
        test("Menu equipment fills available slots before returning overflow and never duplicates owned handles",()=>
        {
            var recovery=MenuEquipment.Restore([uint.MaxValue,99u],[41u,41u,72u,83u],h=>h!=83);
            assert(recovery.Handles.SequenceEqual([41u,99u]) && recovery.Overflow.SequenceEqual([72u]),"partial recovery discarded or duplicated equipment");
        });
    }
}
