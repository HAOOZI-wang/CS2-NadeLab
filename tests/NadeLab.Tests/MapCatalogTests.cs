using NadeLab.Core;

internal static class MapCatalogTests
{
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        test("Map selection exposes the requested fixed map pool with readable labels",()=>
        {
            assert(MapCatalog.All.Count==10 && MapCatalog.All.Select(map=>map.Name).Distinct().Count()==10,"wrong or repeated map list");
            assert(MapCatalog.All.Count(map=>map.ActiveDuty)==7,"wrong active pool");
            assert(new[]{"de_overpass","de_train","de_vertigo"}.All(name=>MapCatalog.Find(name) is {ActiveDuty:false}),"requested former maps missing");
            assert(MapCatalog.All.All(map=>map.Chinese.Length>0 && map.English.Length>0),"unreadable map label");
        });
        test("Map catalog rejects paths and engine-command injection instead of sanitizing them",()=>
        {
            foreach(string? name in new[]{null,""," de_mirage","../de_mirage","de_mirage.vpk","de_mirage;quit","de_mirage\nquit","workshop/123/de_mirage","de_cache_vanity"})
                assert(!MapCatalog.IsSupported(name),"untrusted map name accepted: "+name);
            assert(MapCatalog.Find("DE_MIRAGE")?.Name=="de_mirage","canonical allowlisted command name not returned");
        });
        test("Map menu reflects actual installation and rechecks removed files",()=>
        {
            var present=new HashSet<string>{"de_mirage","de_train","de_unknown"};
            assert(MapCatalog.Installed(present.Contains).Select(map=>map.Name).SequenceEqual(new[]{"de_mirage","de_train"}),"uninstalled or unknown map offered");
            present.Remove("de_train");
            assert(MapCatalog.Installed(present.Contains).Select(map=>map.Name).SequenceEqual(new[]{"de_mirage"}),"removed map remained selectable");
            assert(MapCatalog.Installed(_=>false).Count==0,"empty installation fabricated maps");
        });
        test("Map switching is personal when alone and permission-gated when others are present",()=>
        {
            assert(MapCatalog.CanChange(1,false) && MapCatalog.CanChange(1,true),"single player training cannot choose map");
            assert(!MapCatalog.CanChange(2,false) && MapCatalog.CanChange(2,true),"multiplayer map-change permission not respected");
            assert(!MapCatalog.CanChange(0,false) && !MapCatalog.CanChange(0,true) && !MapCatalog.CanChange(-1,true),"invalid player count bypassed guard");
        });
    }
}
