using System.Text.Json;
using NadeLab.Core;

internal static class WorldIdentityTests
{
    internal static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        const string legacy="{\"GameVersion\":\"test-build\",\"MapBytes\":123,\"MapLastWriteUtc\":\"2026-10-14T00:00:00Z\"}";
        test("Legacy Mirage cache retains identity without leaking into another map",()=>
        {
            string old=WorldIdentity.RouteKey(legacy)!;
            assert(old==WorldIdentity.Key(legacy,"de_mirage"),"old hints lost");
            assert(old!=WorldIdentity.Key(legacy,"de_nuke"),"different map can borrow cached route");
            assert(WorldIdentity.RouteKey(WorldIdentity.Key(legacy,"de_nuke"))==WorldIdentity.Key(legacy,"de_nuke"),"new map identity overwritten");
        });
        test("Explicit recorded map and internal world identity must agree",()=>
        {
            using var old=JsonDocument.Parse(legacy);
            using var current=JsonDocument.Parse(WorldIdentity.Key(legacy,"de_mirage")!);
            assert(WorldIdentity.Matches("de_mirage",old.RootElement,"de_mirage",current.RootElement),"legacy same-map impact rejected");
            assert(!WorldIdentity.Matches("de_nuke",old.RootElement,"de_mirage",current.RootElement),"cross-map history accepted");
            using var other=JsonDocument.Parse(WorldIdentity.Key(legacy,"de_nuke")!);
            assert(!WorldIdentity.Matches("de_mirage",other.RootElement,"de_mirage",current.RootElement),"contradictory embedded map accepted");
        });
        test("World identity rejects unknown metadata and invalidates changed map/build",()=>
        {
            string original=WorldIdentity.RouteKey(legacy)!;
            foreach(string changed in new[]{legacy.Replace("test-build","new-build"),legacy.Replace("123","124"),legacy.Replace("00:00:00Z","01:00:00Z")})
                assert(WorldIdentity.RouteKey(changed)!=original,"changed world reused old evidence");
            foreach(string bad in new[]{"{}","null","[]","not json",legacy.Replace("test-build","unknown"),legacy.Replace("123","0")})
                assert(WorldIdentity.RouteKey(bad) is null,"unidentified world accepted");
            assert(WorldIdentity.Key(legacy,"de_mirage;quit") is null,"invalid map name accepted");
        });
    }
}
