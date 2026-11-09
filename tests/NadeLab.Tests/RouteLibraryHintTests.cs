using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using NadeLab.Core;

internal static class RouteLibraryHintTests
{
    private static readonly ThrowStart Origin=new(Vector3.Zero);
    private static readonly Target Goal=new(new(400,0,2),32);
    private static readonly Aim Angle=new(-25,0,ThrowMode.Left);
    private static string Key(PhysicsProfile profile)=>JsonSerializer.Serialize(profile,NadeLabJsonContext.Default.PhysicsProfile);
    private static RouteLibrary Reload(RouteLibrary source)
    {
        string json=JsonSerializer.Serialize(source.Snapshot().ToArray(),NadeLabJsonContext.Default.SavedRouteArray);
        var restored=new RouteLibrary();
        restored.Import(JsonSerializer.Deserialize(json,NadeLabJsonContext.Default.SavedRouteArray)!);
        return restored;
    }

    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        test("Overlapping nearby areas reuse directions but never old endpoints across new walls",()=>
        {
            var world=new AnalyticWorld();var profile=new PhysicsProfile();
            var endpoint=SmokeSimulation.Run(world,profile,Origin,Angle).Endpoint;
            var original=new Target(endpoint,64,new Vector3(64,64,12));
            var moved=original with {Position=endpoint+new Vector3(32,0,0)};
            var library=new RouteLibrary();library.Remember(Origin,original,Angle,"w","p",true);
            var hints=library.Hints(Origin,moved,null,"w","p");
            assert(hints.SequenceEqual([Angle]),"nearby overlapping area lost direction hint");
            assert(library.Hints(Origin,moved with {Airspace=true},null,"w","p").Length==0,"air/ground objective mixed");
            assert(library.Hints(Origin,original with {Position=endpoint+new Vector3(0,0,80)},null,"w","p").Length==0,"disjoint floor reused");
            assert(library.Hints(Origin,original with {Position=endpoint+new Vector3(100,0,0)},null,"w","p").Length==0,"hint neighbourhood unbounded");
            using var clear=new LineupSearch(world,profile,Origin,moved,new(){MaxCandidates=1,SeedAims=hints});
            while(clear.Advance()){}
            assert(SearchPresentation.HasUsable(clear.Result),"nearby hint not fully checked against new area");
            var blocked=new AnalyticWorld(0,new Box(new(-100,-100,0),new(100,100,200)));
            using var changed=new LineupSearch(blocked,profile,Origin,moved,new(){MaxCandidates=1,SeedAims=hints});
            while(changed.Advance()){}
            assert(!SearchPresentation.HasUsable(changed.Result),"old endpoint accepted through changed collision world");
            assert(library.Snapshot()[0].Start==Origin && library.Snapshot()[0].Target.SameDefinition(original),"query changed saved geometry");
        });
        test("Low tolerance routes reload as angle proposals without robust or actual evidence",()=>
        {
            var library=new RouteLibrary();
            library.Remember(Origin,Goal,Angle,"world","physics",false,nominalValidated:true);
            library=Reload(library);
            var saved=library.Find(Origin,Goal,Angle,"world","physics")!;
            assert(saved.NominalValidated && !saved.SimulationPassed && saved.Trials.Count==0,"nominal hint became robust or actual evidence");
            assert(library.Hints(Origin,Goal,null,"world","physics").SequenceEqual([Angle]),"reviewed nominal hint was lost");
            assert(library.Hints(Origin,Goal,null,"world","physics",includeNominalValidated:false).Length==0,"nominal opt-out ignored");
            library.Remember(Origin,Goal,Angle,"world","physics",false,new("miss",false,false));
            saved=library.Find(Origin,Goal,Angle,"world","physics")!;
            assert(saved.NominalValidated && !saved.SimulationPassed && saved.Trials.Count==1 && !saved.Trials[0].Matched,"separate actual trial corrupted nominal evidence");
        });
        test("Route hints require the complete grenade physics identity and matching world",()=>
        {
            var library=new RouteLibrary();var smoke=new PhysicsProfile();
            var flash=smoke with {Kind=GrenadeKind.Flash};
            var he=smoke with {Kind=GrenadeKind.HighExplosive};
            library.Remember(Origin,Goal,Angle,"world",Key(flash),false,nominalValidated:true);
            assert(library.Hints(Origin,Goal,null,"world",Key(flash)).Length==1,"matching flash hint missing");
            assert(library.Hints(Origin,Goal,null,"world",Key(smoke)).Length==0 && library.Hints(Origin,Goal,null,"world",Key(he)).Length==0,"cross-grenade cache contamination");
            assert(library.Hints(Origin,Goal,null,"world",Key(flash with {PlayerVelocityScale=0})).Length==0,"changed trajectory physics reused hint");
            assert(library.Hints(Origin,Goal,null,"new-world",Key(flash)).Length==0,"changed world reused hint");
            assert(library.Hints(Origin with {Feet=new(65,0,0)},Goal,null,"world",Key(flash)).Length==0 &&
                library.Hints(Origin with {EyeHeight=69},Goal,null,"world",Key(flash)).Length==0,"distant stance reused hint");
        });
        test("Old route JSON defaults nominal validation to false without fabricating evidence",()=>
        {
            SavedRoute[] old=[new(Origin,Goal,Angle,"world","physics",true,[]),
                new(Origin,Goal,Angle with {Yaw=1},"world","physics",false,[])];
            var json=JsonNode.Parse(JsonSerializer.Serialize(old,NadeLabJsonContext.Default.SavedRouteArray))!.AsArray();
            foreach(var record in json) record!.AsObject().Remove("NominalValidated");
            var library=new RouteLibrary();library.Import(JsonSerializer.Deserialize(json.ToJsonString(),NadeLabJsonContext.Default.SavedRouteArray)!);
            assert(library.Count==2 && library.Snapshot().All(r=>!r.NominalValidated),"legacy record acquired nominal evidence");
            assert(library.Hints(Origin,Goal,null,"world","physics").SequenceEqual([Angle]),"legacy robust hint lost or unvalidated route admitted");
        });
        test("Hints rank actual landing evidence before robust simulations and nominal supplements",()=>
        {
            var library=new RouteLibrary();var nominal=Angle;var robust=Angle with {Yaw=3};
            var actual=Angle with {Yaw=10,Action=ThrowAction.Crouch};var nearbyActual=Angle with {Yaw=20};
            library.Remember(Origin,Goal,nominal,"w","p",false,nominalValidated:true);
            library.Remember(Origin,Goal,robust,"w","p",true);
            library.Remember(Origin,Goal,actual,"w","p",false,new("hit",true,true));
            library.Remember(Origin with {Feet=new(30,0,0)},Goal,nearbyActual,"w","p",false,new("guided-hit",false,true,GuidedMatched:true));
            library.Remember(Origin,Goal,Angle with {Yaw=30},"w","p",false,new("seal-only",true,false,"sealed"));
            library.Remember(Origin,Goal,Angle with {Yaw=40},"w","p",false,new("unguided-landing",false,true));
            assert(library.Hints(Origin,Goal,nominal,"w","p").SequenceEqual([actual,nearbyActual,robust,nominal]),"evidence order, nearest actual priority or trial qualification wrong");
        });
        test("Imported nominal routes filter corruption and keep bounded independent trial histories",()=>
        {
            var valid=new SavedRoute(Origin,Goal,Angle,"w","p",false,
                [new("same",false,false),new("same",true,true),new("",true,true),new("bad",true,true,"invented"),null!],true);
            var library=new RouteLibrary();
            library.Import([valid,valid with {Start=Origin with {Feet=new(float.NaN,0,0)}},
                valid with {Aim=Angle with {Pitch=float.PositiveInfinity}},valid with {Target=Goal with {Radius=0}},
                valid with {Trials=null!},valid with {World=""},null!]);
            assert(library.Count==1,"corrupted route was accepted");
            var saved=Reload(library).Snapshot().Single();
            assert(saved.NominalValidated && !saved.SimulationPassed && saved.Trials.Count==1 && saved.Trials[0].Matched,"reload lost nominal state or retained corrupt/duplicate trial");
        });
        test("Nominal cache stays bounded and returns at most twelve distinct angles",()=>
        {
            var library=new RouteLibrary();
            for(int i=0;i<210;i++) library.Remember(Origin,Goal,Angle with {Yaw=i-100},"w","p",false,nominalValidated:true);
            assert(library.Count==192 && Reload(library).Count==192,"route storage exceeded cap");
            var hints=library.Hints(Origin,Goal,null,"w","p");
            assert(hints.Length==12 && hints.Distinct().Count()==12,"hint cap or uniqueness failed");
            for(int i=0;i<40;i++) library.Remember(Origin,Goal,Angle,"w","p",false,new(i.ToString(),false,false),nominalValidated:true);
            assert(library.Find(Origin,Goal,Angle,"w","p")!.Trials.Count==32,"trial history exceeded cap");
        });
        test("A changed target rechecks cached nominal angles instead of reusing saved success",()=>
        {
            var world=new AnalyticWorld();var physics=new PhysicsProfile();
            var endpoint=SmokeSimulation.Run(world,physics,Origin,Angle).Endpoint;
            var initial=new Target(endpoint,32);var changed=new Target(endpoint+new Vector3(0,7,0),1);
            var library=new RouteLibrary();library.Remember(Origin,initial,Angle,"w",Key(physics),false,nominalValidated:true);
            var hints=Reload(library).Hints(Origin,changed,null,"w",Key(physics));
            assert(hints.SequenceEqual([Angle]) && !changed.Contains(endpoint),"fixture did not preserve the old angle as a now-invalid proposal");
            using var search=new LineupSearch(world,physics,Origin,changed,new(){SeedAims=hints,MaxCandidates=1});
            while(search.Advance()){}
            var found=search.Result!.Matches.Concat(search.Result.Experimental??[]).ToArray();
            assert(found.All(c=>c.Aim!=Angle && changed.Accepts(world,c.Trajectory.Endpoint)),"old target acceptance was reused");
        });
        test("New obstacles force cached nominal angles through current collision simulation",()=>
        {
            var open=new AnalyticWorld();var blocked=new CountingWorld(new AnalyticWorld(0,new Box(new(100,-2000,0),new(120,2000,2000))));
            var physics=new PhysicsProfile();var endpoint=SmokeSimulation.Run(open,physics,Origin,Angle).Endpoint;
            var goal=new Target(endpoint,8);var library=new RouteLibrary();
            library.Remember(Origin,goal,Angle,"w",Key(physics),false,nominalValidated:true);
            var hints=library.Hints(Origin,goal,null,"w",Key(physics));
            assert(!goal.Contains(SmokeSimulation.Run(blocked,physics,Origin,Angle).Endpoint),"fixture failed to obstruct the old route");
            blocked.Traces=0;
            using var search=new LineupSearch(blocked,physics,Origin,goal,new(){SeedAims=hints,MaxCandidates=1});
            while(search.Advance()){}
            var found=search.Result!.Matches.Concat(search.Result.Experimental??[]).ToArray();
            assert(blocked.Traces>0 && found.All(c=>c.Aim!=Angle && goal.Accepts(blocked,c.Trajectory.Endpoint)),"cache bypassed current collision checks");
            var saved=library.Snapshot().Single();
            assert(saved.NominalValidated && !saved.SimulationPassed && saved.Trials.Count==0,"solver wrote simulated revalidation as actual evidence");
        });
    }

    private sealed class CountingWorld(ICollisionWorld inner):ICollisionWorld
    {
        public int Traces;
        public SweepHit Sweep(Vector3 a,Vector3 b,float size){Traces++;return inner.Sweep(a,b,size);}
    }
}
