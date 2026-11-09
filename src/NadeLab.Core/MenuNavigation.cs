namespace NadeLab.Core;

// Remembers values only. Reopening a menu always builds fresh, guarded actions.
public sealed class MenuNavigation
{
    private readonly Dictionary<string,(string Item,int Index)> selections=new(StringComparer.Ordinal);
    public void Remember(string menu,IReadOnlyList<string> items,int selected)
    {
        if(items.Count==0) return;
        selected=Math.Clamp(selected,0,items.Count-1);
        selections[menu]=(items[selected],selected);
    }
    public int Restore(string menu,IReadOnlyList<string> items,int? fallbackCount=null)
    {
        if(items.Count==0 || !selections.TryGetValue(menu,out var saved)) return 0;
        for(int i=0;i<items.Count;i++) if(items[i]==saved.Item) return i;
        int count=Math.Clamp(fallbackCount ?? items.Count,1,items.Count);
        return Math.Clamp(saved.Index,0,count-1);
    }
}

public static class PersonalLibraryMenu
{
    public static IReadOnlyList<GrenadeKind> Categories {get;}=Array.AsReadOnly(new[]{GrenadeKind.Smoke,GrenadeKind.Molotov,GrenadeKind.HighExplosive,GrenadeKind.Flash,GrenadeKind.Decoy});
    public static GrenadeKind Category(GrenadeKind kind)=>kind.Fire() ? GrenadeKind.Molotov : kind;
    public static bool Contains(GrenadeKind category,GrenadeKind kind)=>Category(kind)==category;
    public static string Label(GrenadeKind category)=>category switch
    {GrenadeKind.Molotov=>"燃烧瓶 / 燃烧弹",GrenadeKind.HighExplosive=>"手雷",_=>category.Label()};
}
