using System.Numerics;
using NadeLab.Core;

internal static class RecentImpactTests
{
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        test("Real impact identity rejects slot reuse, map epoch and projectile reuse",()=>
        {
            var owner=new ImpactOwner(3,1234,5678);
            assert(RecentImpactRules.Matches(owner,owner,GrenadeKind.Flash,GrenadeKind.Flash,900,900),"exact event rejected");
            foreach(var stale in new[]{owner with {Epoch=4},owner with {Controller=1235},owner with {Steam=5679}})
                assert(!RecentImpactRules.Matches(owner,stale,GrenadeKind.Flash,GrenadeKind.Flash,900,900),"stale owner accepted");
            assert(!RecentImpactRules.Matches(owner,owner,GrenadeKind.Flash,GrenadeKind.HighExplosive,900,900),"wrong kind accepted");
            assert(!RecentImpactRules.Matches(owner,owner,GrenadeKind.Flash,GrenadeKind.Flash,900,901),"recycled projectile index accepted");
        });
        test("Real impact creates an editable target at the exact event centre",()=>
        {
            var point=new Vector3(-206.23354f,-227.92235f,270.17676f);
            foreach(var kind in Enum.GetValues<GrenadeKind>())
            {
                var target=RecentImpactRules.TargetFor(kind,point);target.Validate();
                assert(target.Position==point,"impact snapped to a guessed location");
                assert(target.IsArea && target.Airspace==kind.Timed(),"wrong target space");
                assert(target.Exclusions is null && target.ExtraRegions is null && target.Objective is null,"old constraints inherited");
            }
        });
        test("Real impact history needs exact ownership, world and event position",()=>
        {
            const string world="{\"GameVersion\":\"build 1\",\"MapBytes\":123,\"MapLastWriteUtc\":\"2026-10-13\"}";
            string row="{\"ThrowId\":\"real-1\",\"PlayerSteamId\":\"123456\",\"Kind\":\"Flash\",\"Outcome\":\"detonated\",\"Actual\":{\"X\":1,\"Y\":2,\"Z\":3},\"Utc\":\"2026-10-13T01:02:03Z\",\"Map\":\"de_mirage\",\"WorldVersion\":"+world+"}";
            var good=RecentImpactRules.ReadArchive(row,"de_mirage",world);
            assert(good is {Steam:123456,Kind:GrenadeKind.Flash} && good.Position==new Vector3(1,2,3),"owned real event missing");
            foreach(string bad in new[]{row.Replace("123456","0"),row.Replace("PlayerSteamId","UnknownOwner"),row.Replace("Actual","Samples"),row.Replace("detonated","effect-event-missing"),row.Replace("build 1","build 2"),row.Replace("de_mirage","de_inferno"),"not json"})
                assert(RecentImpactRules.ReadArchive(bad,"de_mirage",world) is null,"unowned/guessed/stale archive accepted");
        });
    }
}
