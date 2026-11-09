using System.Numerics;
using NadeLab.Core;

internal static class SpawnTravelTests
{
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        test("Competitive spawns retain all tied match positions but exclude reserve priorities",()=>
        {
            var spots=Enumerable.Range(1,10).Select(i=>new SpawnLocation(i,new(i,0,0),0,true,0))
                .Concat(Enumerable.Range(11,7).Select(i=>new SpawnLocation(i,new(i,0,0),0,true,1)));
            var catalog=SpawnTravel.CompetitiveCatalog(spots.Reverse());
            assert(catalog.Count==10 && catalog.Select(x=>x.Location.Id).SequenceEqual(Enumerable.Range(1,10)),"competitive pool truncated to five or reserve sites included");
            assert(catalog.Select(x=>x.Number).SequenceEqual(Enumerable.Range(1,10)),"filtered numbering has holes");
        });
        test("Competitive pool includes the complete priority tier needed for five players",()=>
        {
            SpawnLocation[] spots=[new(1,new(1,0,0),0,true,2),new(2,new(2,0,0),0,true,2),
                new(3,new(3,0,0),0,true,3),new(4,new(4,0,0),0,true,3),new(5,new(5,0,0),0,true,3),new(6,new(6,0,0),0,true,3),
                new(7,new(7,0,0),0,true,4),new(8,new(8,0,0),0,false,0),new(9,new(float.NaN,0,0),0,true,0)];
            assert(SpawnTravel.CompetitiveCatalog(spots).Select(x=>x.Location.Id).SequenceEqual(new[]{1,2,3,4,5,6}),"priority boundary or disabled filtering incorrect");
            assert(SpawnTravel.CompetitiveCatalog([]).Count==0,"empty pool invents positions");
        });
        test("Ground spawn box follows supported surface and keeps label on the ground",()=>
        {
            var origin=new Vector3(100,200,40);
            Vector3? Ground(Vector3 p)=>new Vector3(p.X,p.Y,.1f*p.X);
            assert(SpawnGroundGeometry.TryCreate(origin,Ground,out var mark),"valid sloped ground rejected");
            assert(mark.Corners.Count==4 && mark.Corners.All(p=>MathF.Abs(p.Z-(p.X*.1f+.5f))<.001f),"box floats or cuts into slope");
            assert(mark.Center==new Vector3(100,200,10.5f),"label not grounded at spawn XY");
            assert(MathF.Abs(mark.TextAngles.X+5.710593f)<.001f && MathF.Abs(mark.TextAngles.Z)<.001f,"worldtext local XY plane does not follow ground normal");
            assert(SpawnGroundGeometry.TryCreate(origin,p=>new Vector3(p.X,p.Y,0),out var flat) && flat.TextAngles==Vector3.Zero,"flat-ground label is vertical");
            assert(MathF.Abs(mark.Corners[1].X-mark.Corners[0].X)==48 && MathF.Abs(mark.Corners[3].Y-mark.Corners[0].Y)==48,"box size wrong");
            assert(!SpawnGroundGeometry.TryCreate(origin,p=>p.X<100?null:Ground(p),out _),"partial floating frame accepted");
            assert(!SpawnGroundGeometry.TryCreate(origin,p=>new Vector3(float.NaN),out _),"invalid surface accepted");
        });
        test("Numbered spawn positions remain stable across enumeration entity IDs and occupancy",()=>
        {
            SpawnLocation[] spots=[new(1,new(30,2,1),90,true),new(2,new(10,2,1),0,false),new(3,new(20,2,1),45,true)];
            var catalog=SpawnTravel.Catalog(spots);
            assert(catalog.Select(s=>s.Number).SequenceEqual(new[]{1,2,3}),"missing or duplicate numbers");
            assert(catalog.Select(s=>s.Location.Position.X).SequenceEqual(new[]{10f,20f,30f}),"coordinates not stable");
            var reloaded=SpawnTravel.Catalog(spots.Reverse().Select(s=>s with {Id=s.Id+100,Enabled=true}));
            assert(catalog.Select(s=>s.Location.Position).SequenceEqual(reloaded.Select(s=>s.Location.Position)),"entity IDs or activation renumbered spawns");
            var tried=new List<int>();
            assert(!SpawnTravel.TryResolve(catalog,2,s=>{tried.Add(s.Id);return null;},out _),"blocked selection silently replaced");
            assert(tried.SequenceEqual(new[]{3}),"other spawn tried");
            assert(SpawnTravel.TryResolve(catalog,3,s=>s.Position-Vector3.UnitZ*10,out var selected) && selected.Id==1 && selected.Position==new Vector3(30,2,-9) && selected.Yaw==90,"exact feet or facing lost");
        });
        test("Numbered spawn selection rejects invalid disabled and horizontally displaced positions",()=>
        {
            var catalog=SpawnTravel.Catalog(new SpawnLocation[]{new(1,Vector3.One,0,true),new(2,new(2,1,1),0,false),new(3,new(float.NaN,0,0),0,true),new(4,Vector3.Zero,float.NaN,true)});
            assert(catalog.Count==2,"invalid transform in menu");
            foreach(int number in new[]{0,-1,2,3,int.MaxValue})
                assert(!SpawnTravel.TryResolve(catalog,number,s=>throw new Exception("invalid selection reached native trace"),out _),"invalid number accepted");
            assert(!SpawnTravel.TryResolve([],1,s=>s.Position,out _),"empty map invented spawn");
            assert(!SpawnTravel.TryResolve(catalog,1,s=>new Vector3(float.NaN),out _),"invalid floor accepted");
            assert(!SpawnTravel.TryResolve(catalog,1,s=>s.Position+Vector3.UnitX,out _),"moved sideways from numbered spawn");
        });
        test("Spawn travel skips disabled invalid and blocked spots and chooses a stable safe alternative",()=>
        {
            SpawnLocation[] spots=[new(7,new(7,0,0),90,true),new(1,new(1,0,0),0,false),
                new(2,new(float.NaN,0,0),0,true),new(3,new(3,0,0),float.NaN,true),
                new(4,new(4,0,0),0,true),new(5,new(5,0,0),45,true)];
            var checkedIds=new List<int>();
            Vector3? Resolve(SpawnLocation spot){checkedIds.Add(spot.Id);return spot.Id==4?null:spot.Position+Vector3.UnitZ;}
            assert(SpawnTravel.TrySelect(spots,Resolve,out var selected) && selected.Id==5 && selected.Position.Z==1 && selected.Yaw==45,"safe fallback or facing lost");
            assert(checkedIds.SequenceEqual(new[]{4,5}),"invalid spots reached native collision or ordering changed");
        });
        test("Spawn travel rejects empty and nonfinite native resolution without inventing an origin",()=>
        {
            assert(!SpawnTravel.TrySelect([],s=>s.Position,out _),"empty map teleported to zero");
            SpawnLocation[] spots=[new(1,Vector3.One,0,true)];
            assert(!SpawnTravel.TrySelect(spots,s=>new Vector3(float.NaN),out _),"nonfinite ground accepted");
            assert(!SpawnTravel.TrySelect(spots,s=>null,out _),"blocked spawn accepted");
        });
    }
}
