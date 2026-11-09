using System.Numerics;
using NadeLab.Core;

public static class TargetIdentityTests
{
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        var a=new LandingRegion(new(150,0,0),new(64,64,16));var b=new LandingRegion(new(200,0,0),new(32,32,16));
        var target=new Target(new(100,0,0),32,new Vector3(64,64,16),ExtraRegions:[a,b],Exclusions:[new(new(120,0,0),new(8,8,8))]);
        var reordered=target with {ExtraRegions=[b,a],Exclusions=target.Exclusions!.ToArray()};
        test("Target identity preserves unordered region multiplicity and all semantic fields",()=>
        {
            assert(target.SameDefinition(reordered),"region order affected evidence identity");
            assert(!target.SameDefinition(target with {ExtraRegions=[a,a]}),"duplicate region replaced a distinct region");
            assert(!(target with {ExtraRegions=[a,a]}).SameDefinition(target with {ExtraRegions=[a]}),"multiplicity was collapsed");
            assert((target with {ExtraRegions=null}).SameDefinition(target with {ExtraRegions=[]}),"null/empty equivalence changed");
            assert(!target.SameDefinition(target with {Exclusions=[]}) && !target.SameDefinition(target with {Airspace=true}),"target semantics ignored");
        });
        test("Repeated target identity checks avoid sorting and transient allocations",()=>
        {
            for(int i=0;i<32;i++)target.SameDefinition(reordered);
            long before=GC.GetAllocatedBytesForCurrentThread();bool same=true;
            for(int i=0;i<256;i++)same &= target.SameDefinition(reordered);
            long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
            assert(same && allocated<1024,$"target comparison allocated {allocated} bytes in a search publication hot path");
        });
        test("Publishing twenty route hints keeps bounded allocations with a full route library",()=>
        {
            var library=new RouteLibrary();var start=new ThrowStart(Vector3.Zero,64,ThrowOrigin.Preparation);
            library.Import(Enumerable.Range(0,192).Select(i=>new SavedRoute(start,target,new(-20,i,ThrowMode.Left),"world","physics",true,[])));
            library.Remember(start,target,new(-30,0,ThrowMode.Left),"world","physics",true);
            long before=GC.GetAllocatedBytesForCurrentThread();
            for(int i=1;i<=20;i++)library.Remember(start,reordered,new(-30,i,ThrowMode.Left),"world","physics",true,nominalValidated:true);
            long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
            assert(allocated<100000,$"route publication allocated {allocated} bytes synchronously");
            assert(library.Count==192 && library.Find(start,target,new(-30,20,ThrowMode.Left),"world","physics") is not null,"performance fix lost recent result adoption or changed target identity");
        });
    }
}
