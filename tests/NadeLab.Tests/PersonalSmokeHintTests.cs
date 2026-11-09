using System.Numerics;
using NadeLab.Core;

internal static class PersonalSmokeHintTests
{
    private const string World="{\"GameVersion\":\"smoke-hint-build\",\"MapBytes\":176187297,\"MapLastWriteUtc\":\"2026-09-23T13:37:44Z\",\"MapName\":\"de_mirage\"}";
    private static readonly PhysicsProfile Physics=new();
    private static readonly ThrowStart Start=new(new(100,200,0),64);
    private static readonly SearchOptions Actions=new() {IncludeActions=true};
    private static PersonalLineup Entry()=>new(Guid.NewGuid(),"manual-vip-smoke",GrenadeKind.Smoke,Start.Feet,Start.EyeHeight,-28,151,
        "W tap then jump; timing and action not measured",new(400,500,32),"manual-throw",World,DateTimeOffset.UnixEpoch);
    private static Aim[] Select(IEnumerable<PersonalLineup> entries,SearchOptions? options=null)=>
        PersonalSmokeHints.Select(entries,Physics,Start,"de_mirage",World,options ?? Actions);

    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        test("Personal smoke angle proposes all controlled action strengths with current family identity",()=>
        {
            var e=Entry();var seeds=Select([e]);
            assert(seeds.Length==9,"one recorded angle did not propose three actions at all three strengths");
            foreach(var action in new[]{ThrowAction.Jump,ThrowAction.AirForwardJump,ThrowAction.Standing})
                assert(seeds.Where(a=>a.EffectiveAction==action).Select(a=>a.Mode).Order().SequenceEqual(new[]{ThrowMode.Left,ThrowMode.Both,ThrowMode.Right}),"action lost a throw strength");
            assert(seeds.All(a=>a.Pitch==e.Pitch && a.Yaw==e.Yaw),"bookmark angle changed");
            var current=ThrowCatalog.Families(Physics,Actions,0,0);
            assert(seeds.All(a=>current.Contains(a with {Pitch=0,Yaw=0})),"hint invented a launch state outside current families");
            assert(seeds[0].EffectiveAction==ThrowAction.Jump && seeds[1].EffectiveAction==ThrowAction.AirForwardJump && seeds[2].EffectiveAction==ThrowAction.Standing,
                "ordinary jump, controlled forward jump and standing did not get early opportunities");
        });
        test("Personal smoke hint filter rejects stale map source stance kind and incomplete actual throw",()=>
        {
            var e=Entry();assert(Select([e]).Length>0,"valid fixture rejected");
            var invalid=new[]{e with {World=World.Replace("smoke-hint-build","old-build")},e with {World=World.Replace("de_mirage","de_dust2")},
                e with {Feet=Start.Feet+new Vector3(8.01f,0,0)},e with {EyeHeight=68.01f},e with {Kind=GrenadeKind.Flash},
                e with {ActualImpact=null},e with {ThrowId=null},e with {ThrowId=" "},e with {ActualImpact=new(float.NaN,0,0)},
                e with {Pitch=float.NaN},e with {Pitch=-89.01f},e with {Yaw=float.PositiveInfinity},e with {Feet=new(float.NaN,0,0)},
                e with {EyeHeight=float.NaN},e with {World="{}"}};
            assert(Select(invalid).Length==0,"incompatible or unthrown bookmark entered search");
            assert(PersonalSmokeHints.Select([e],Physics,Start,"de_dust2",World,Actions).Length==0,"current map mismatch accepted");
            assert(PersonalSmokeHints.Select([e],Physics,Start,"de_mirage",null,Actions).Length==0,"unknown current world accepted");
            assert(PersonalSmokeHints.Select([e],Physics with {Kind=GrenadeKind.Flash},Start,"de_mirage",World,Actions).Length==0,"smoke hints entered another grenade search");
            assert(PersonalSmokeHints.Select([e],Physics,Start with {Feet=new(float.NaN,0,0)},"de_mirage",World,Actions).Length==0,"invalid source accepted");
            assert(PersonalSmokeHints.Select([e],Physics,Start with {EyeHeight=float.NaN},"de_mirage",World,Actions).Length==0,"invalid stance accepted");
            assert(Select([e with {Feet=Start.Feet+new Vector3(8,0,0),EyeHeight=68}]).Length>0,"inclusive nearby boundaries rejected");
        });
        test("Personal smoke hints interleave two recent distinct nearby angles within twelve slots",()=>
        {
            var entries=Enumerable.Range(0,20).Select(i=>Entry() with {Pitch=-40+i,SavedAt=DateTimeOffset.UnixEpoch.AddSeconds(i)}).ToArray();
            var before=entries.ToArray();var seeds=Select(entries);
            assert(seeds.Length==12 && seeds.Distinct().Count()==12,"personal hint budget or uniqueness failed");
            assert(seeds.Select(a=>a.Pitch).Distinct().Order().SequenceEqual(new[]{-22f,-21f}),"older bookmarks crowded out the two recent angles");
            assert(seeds[0].Pitch==-21 && seeds[1].Pitch==-22 && seeds[0].EffectiveAction==ThrowAction.Jump && seeds[1].EffectiveAction==ThrowAction.Jump,
                "one bookmark monopolized early action hypotheses");
            assert(seeds[2].Pitch==-21 && seeds[3].Pitch==-22 && seeds[2].EffectiveAction==ThrowAction.AirForwardJump && seeds[3].EffectiveAction==ThrowAction.AirForwardJump,
                "second bookmark lost the controlled forward jump");
            assert(entries.SequenceEqual(before),"hint selection changed recorded data");
            var duplicate=entries[^1] with {Yaw=180,SavedAt=DateTimeOffset.UnixEpoch.AddMinutes(1)};
            var wrapped=duplicate with {Id=Guid.NewGuid(),Yaw=-180,SavedAt=duplicate.SavedAt.AddSeconds(1)};
            var distinct=Select([entries[^2],duplicate,wrapped]);
            assert(distinct.Length==12 && distinct.Select(a=>a.Pitch).Distinct().Count()==2,"equivalent yaw bookmarks consumed both source slots");
            assert(distinct.Where(a=>a.Pitch==duplicate.Pitch).All(a=>a.Yaw==-180),"yaw was not normalized");
        });
        test("Personal smoke hints honor the request action switch and refresh jump physics",()=>
        {
            var standing=Select([Entry()],new() {IncludeForward=true});
            assert(standing.Length==3 && standing.All(a=>a.EffectiveAction==ThrowAction.Standing),"bookmark enabled a disabled action or inferred a run");
            var changed=Physics with {JumpImpulse=320,PlayerGravity=700};
            var seeds=PersonalSmokeHints.Select([Entry()],changed,Start,"de_mirage",World,Actions);
            assert(seeds.Length==9 && seeds.Where(a=>a.Airborne).All(a=>MathF.Abs(a.ReleaseHeight-28.2265625f)<.001f && MathF.Abs(a.VerticalSpeed-247.265625f)<.001f),
                "bookmark supplied stale jump height or vertical speed");
        });
        test("Personal smoke hints use configured air speed and never enable its disabled family",()=>
        {
            var configured=Select([Entry()],Actions with {AirForwardSpeed=24});
            var forward=configured.Where(a=>a.EffectiveAction==ThrowAction.AirForwardJump).ToArray();
            assert(forward.Length==3 && forward.All(a=>a.ForwardSpeed==24 && MathF.Abs(a.ForwardOffset-2.4f)<.001f),"bookmark hardcoded forward speed or displacement");
            var disabled=Select([Entry()],Actions with {AirForwardSpeed=0});
            assert(disabled.Length==6 && disabled.All(a=>a.EffectiveAction!=ThrowAction.AirForwardJump),"disabled forward jump leaked through bookmark");
        });
        test("Personal smoke notes names and actual landing never infer action or change hypotheses",()=>
        {
            var e=Entry();var before=Select([e]);
            var edited=e with {Name="validated standing right-click",Instruction="180u/s run-up, W eight units, success 100%",ActualImpact=new(-2000,3000,400)};
            assert(before.Length>0 && Select([edited]).SequenceEqual(before),"free text or the landing reverse-engineered release action");
        });
        test("Smoke seed composition gives both sources opportunities and preserves action identities",()=>
        {
            var prior=Enumerable.Range(0,12).Select(i=>new Aim(-20,i,ThrowMode.Left)).ToArray();
            var personal=Select([Entry(),Entry() with {Pitch=-29}]);
            var combined=PersonalSmokeHints.Combine(prior,personal);
            assert(combined.Length==12 && combined.Distinct().Count()==12,"combined list exceeded seed capacity or duplicated candidates");
            assert(combined.Where((_,i)=>i%2==0).SequenceEqual(prior.Take(6)) && combined.Where((_,i)=>i%2==1).SequenceEqual(personal.Take(6)),
                "one hint source starved the other");
            var sameAngle=new Aim(-28,151,ThrowMode.Left);
            var actionVariant=sameAngle with {Action=ThrowAction.Jump};
            var deduplicated=PersonalSmokeHints.Combine([sameAngle,sameAngle],[sameAngle,actionVariant]);
            assert(deduplicated.SequenceEqual(new[]{sameAngle,actionVariant}),"deduplication merged distinct actions or kept identical hints");
            assert(PersonalSmokeHints.Combine(prior,[]).SequenceEqual(prior),"absent personal library reduced prior route capacity");
            assert(PersonalSmokeHints.Combine([],personal).SequenceEqual(personal),"absent route history reduced personal capacity");
        });
        test("Recorded smoke landing remains only a hint and cannot bypass current collision validation",()=>
        {
            var world=new AnalyticWorld();var e=Entry();var known=new Aim(e.Pitch,e.Yaw,ThrowMode.Left);
            var endpoint=SmokeSimulation.Run(world,Physics,Start,known).Endpoint;
            e=e with {ActualImpact=endpoint};var seeds=Select([e],new());
            assert(seeds.Length==3,"recorded angle missing from standing search");
            var target=new Target(endpoint,32,new Vector3(32,32,12));
            using var clear=new LineupSearch(world,Physics,Start,target,new() {MaxCandidates=1,SeedAims=seeds});
            Complete(clear);
            assert(clear.Result!.Experimental?.Any(c=>c.Aim==known && c.RobustTrials>0 && c.ModelTrials>0 && c.Reliability<.75f)==true,
                "recorded impact bypassed the current surface-sensitivity review");
            assert(clear.Result.Matches.Count==0,"recorded landing was promoted to robust success despite failed surface checks");
            var blocked=new AnalyticWorld(0,new Box(Start.Feet-new Vector3(100),Start.Feet+new Vector3(100,100,200)));
            using var closed=new LineupSearch(blocked,Physics,Start,target,new() {MaxCandidates=1,SeedAims=seeds});
            Complete(closed);
            assert(closed.Result!.Matches.Count==0 && (closed.Result.Experimental?.Count ?? 0)==0,"saved impact was published without current collision checks");
            assert(closed.Evaluated<=1 && e.ActualImpact==endpoint,"hint exceeded search budget or edited actual evidence");
        });
    }

    private static void Complete(LineupSearch search)
    {int count=0;while(search.Advance())if(++count>100000)throw new Exception("bounded smoke hint fixture did not finish");}
}
