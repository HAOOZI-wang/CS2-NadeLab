using System.Numerics;
using NadeLab.Core;

static class PersonalFlashHintTests
{
    private const string World="{\"GameVersion\":\"case-build\",\"MapBytes\":176187297,\"MapLastWriteUtc\":\"2026-09-23T13:37:44Z\",\"MapName\":\"de_mirage\"}";
    private static readonly PhysicsProfile Physics=Grenades.Bootstrap(GrenadeKind.Flash,new());
    private static readonly ThrowStart Start=new(new(445.95343f,571.96875f,-257.96875f),63.703125f);
    private static PersonalLineup Entry()=>new(Guid.NewGuid(),"saved-manual-flash",GrenadeKind.Flash,Start.Feet,Start.EyeHeight,-12.061312f,-131.38467f,
        "manual action unknown",new(-242.18774f,-208.99599f,248.29692f),"saved-throw",World,DateTimeOffset.UnixEpoch);
    private static void Complete(FlashEffectSearch search)
    {int count=0;while(search.Advance())if(++count>100000)throw new Exception("unbounded seed-only test");}
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        test("Personal flash angles expand standing and jump strengths without assuming recorded action",()=>
        {
            var entry=Entry();var seeds=PersonalFlashHints.Select([entry],Physics,Start,"de_mirage",World);
            assert(seeds.Count==6,"saved flash did not contribute six fixed action hypotheses");
            assert(seeds.All(a=>a.Pitch==entry.Pitch && a.Yaw==entry.Yaw && a.ForwardOffset==0 && a.ForwardSpeed==0),"hint changed aim or requested movement");
            assert(seeds.Select(a=>(a.EffectiveAction,a.Mode)).Distinct().Count()==6 && seeds.Any(a=>a.EffectiveAction==ThrowAction.Jump && a.Mode==ThrowMode.Left),"known jump family omitted");
            assert(seeds.Where(a=>a.Airborne).All(a=>a.ReleaseHeight==JumpLatch.Height(Physics) && a.VerticalSpeed==JumpLatch.VerticalSpeed(Physics)),"stale release physics");
        });
        test("Personal hints exclude different world, source, stance, type and unthrown bookmarks",()=>
        {
            var e=Entry();var invalid=new[]{e with {World=World.Replace("case-build","old-build")},e with {Feet=Start.Feet+new Vector3(9,0,0)},
                e with {EyeHeight=46},e with {Kind=GrenadeKind.Smoke},e with {ActualImpact=null,ThrowId=null},e with {Pitch=float.NaN}};
            assert(PersonalFlashHints.Select(invalid,Physics,Start,"de_mirage",World).Count==0,"incompatible collection influenced request");
            assert(PersonalFlashHints.Select([e],Physics,Start,"de_dust2",World).Count==0,"cross-map hint accepted");
            assert(PersonalFlashHints.Select([e],Physics,Start,"de_mirage",null).Count==0,"unknown world accepted");
            var nearby=Start with {Feet=Start.Feet+new Vector3(1,0,0)};
            assert(PersonalFlashHints.Select([e],Physics,nearby,"de_mirage",World).Count==6,"small standing displacement lost all hints");
        });
        test("Personal hint selection is bounded and favors closest source without modifying collection",()=>
        {
            var entries=Enumerable.Range(0,20).Select(i=>Entry() with {Pitch=-30+i,Feet=Start.Feet+new Vector3(i%8,0,0),SavedAt=DateTimeOffset.UnixEpoch.AddSeconds(i)}).ToArray();
            var before=entries.ToArray();var seeds=PersonalFlashHints.Select(entries,Physics,Start,"de_mirage",World);
            assert(seeds.Count==24 && seeds.Distinct().Count()==24,"hint list exceeds budget or duplicates angles");
            assert(seeds[0].Pitch==entries[16].Pitch,"closest most-recent source not prioritized");
            assert(entries.SequenceEqual(before),"selection edited collection");
        });
        test("Collected angle recovers seed-only route and still rejects an exposed protector",()=>
        {
            var e=Entry();var world=new EmptyWorld();
            var known=new Aim(e.Pitch,e.Yaw,ThrowMode.Left,Action:ThrowAction.Jump,ReleaseHeight:JumpLatch.Height(Physics),VerticalSpeed:JumpLatch.VerticalSpeed(Physics));
            var endpoint=SmokeSimulation.Run(world,Physics,Start,known).Endpoint;
            var victim=new FlashObserver(1,"A",endpoint+new Vector3(100,0,0),new(0,180,ThrowMode.Left),FlashRole.MustFlash);
            var protector=victim with {Id=2,Label="B",Role=FlashRole.Protect};
            var seeds=PersonalFlashHints.Select([e],Physics,Start,"de_mirage",World);
            PredictionVisibility Sight(Vector3 p,FlashObserver observer)=>new(observer.Role==FlashRole.Protect ? 0 : Vector3.Distance(p,endpoint)<.1f ? 5 : 0,5);
            var objective=new FlashObjective([victim,protector]);
            using var search=new FlashEffectSearch(world,Physics,Start,objective,Sight,new(){MaxCandidates=6,SeedAims=seeds});Complete(search);
            assert(search.Result!.Matches.Any(m=>m.Candidate.Aim==known),"known saved jump angle was not evaluated and recovered first");
            assert(search.Start==Start && search.Evaluated<=6,"seed bypassed origin or search budget");
            using var unsafeSearch=new FlashEffectSearch(world,Physics,Start,objective,(p,o)=>o.Role==FlashRole.Protect ? new(5,5) : Sight(p,o),new(){MaxCandidates=6,SeedAims=seeds});Complete(unsafeSearch);
            assert(unsafeSearch.Result!.Matches.Count==0,"saved lineup bypassed a protected observer");
        });
    }
}
