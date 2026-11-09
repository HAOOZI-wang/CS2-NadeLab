using NadeLab.Core;

public static class MenuNavigationTests
{
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        test("Menu memory follows stable items after reordering and survives independent child navigation",()=>
        {
            var memory=new MenuNavigation();
            memory.Remember("root",["task","target","library"],2);
            memory.Remember("library/smoke/page1",["a","b","c"],1);
            assert(memory.Restore("root",["library","task","target"])==0,"remembered item replaced by its old index");
            assert(memory.Restore("library/smoke/page1",["a","b","c"])==1,"child lost selection");
            assert(memory.Restore("library/flash/page1",["x","y","z"])==0,"different category inherited selection");
        });
        test("Deleting a selected item keeps its nearby position and clamps a deleted last item",()=>
        {
            var memory=new MenuNavigation();memory.Remember("list",["a","b","c","d"],1);
            assert(memory.Restore("list",["a","c","d"])==1,"deletion jumped away from adjacent item");
            memory.Remember("list",["a","c","d"],2);
            assert(memory.Restore("list",["a","c"])==1 && memory.Restore("list",[])==0,"deleted tail or empty list not clamped");
        });
        test("Library fire category includes both actual fire types without mixing other grenade types",()=>
        {
            foreach(var kind in Enum.GetValues<GrenadeKind>())
            {
                var groups=PersonalLibraryMenu.Categories.Where(c=>PersonalLibraryMenu.Contains(c,kind)).ToArray();
                assert(groups.Length==1,"entry must appear in exactly one category");
                if(kind is GrenadeKind.Molotov or GrenadeKind.Incendiary) assert(groups[0]==GrenadeKind.Molotov,"fire variant separated");
                else assert(groups[0]==kind,"actual grenade assigned to wrong category");
            }
        });
        test("Deleting the last collection item never moves selection onto a page navigation action",()=>
        {
            var memory=new MenuNavigation();memory.Remember("list",["a","b","c","back"],2);
            assert(memory.Restore("list",["a","b","back"],fallbackCount:2)==1,"deleted collection selected unrelated footer action");
        });
    }
}
