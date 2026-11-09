namespace NadeLab.Core;

public sealed record TrainingMap(string Name,string Chinese,string English,bool ActiveDuty)
{
    public string DisplayName=>$"{Chinese} {English}";
}

public static class MapCatalog
{
    // Deliberately allow only these shipped maps. A filename supplied by a
    // player must never become a path or an engine-console command argument.
    public static IReadOnlyList<TrainingMap> All { get; }=Array.AsReadOnly(new TrainingMap[]
    {
        new("de_mirage","荒漠迷城","Mirage",true),
        new("de_dust2","炙热沙城Ⅱ","Dust2",true),
        new("de_inferno","炼狱小镇","Inferno",true),
        new("de_nuke","核子危机","Nuke",true),
        new("de_ancient","远古遗迹","Ancient",true),
        new("de_anubis","阿努比斯","Anubis",true),
        new("de_cache","死城之谜","Cache",true),
        new("de_overpass","死亡游乐园","Overpass",false),
        new("de_train","列车停放站","Train",false),
        new("de_vertigo","殒命大厦","Vertigo",false)
    });
    public static TrainingMap? Find(string? name)=>All.FirstOrDefault(map=>string.Equals(map.Name,name,StringComparison.OrdinalIgnoreCase));
    public static bool IsSupported(string? name)=>Find(name) is not null;
    public static IReadOnlyList<TrainingMap> Installed(Func<string,bool> exists)=>All.Where(map=>exists(map.Name)).ToArray();
    public static bool CanChange(int humanCount,bool hasMapPermission)=>humanCount==1 || humanCount>1 && hasMapPermission;
}
