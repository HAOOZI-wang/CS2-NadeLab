using System.Numerics;
using NadeLab.Core;

public static class GrenadeTargetTransferTests
{
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        test("Grenade switch converts compatible precise points to editable areas",()=>
        {
            var source=new Target(new(4,5,6),24);
            foreach(var kind in new[]{GrenadeKind.Molotov,GrenadeKind.Incendiary,GrenadeKind.Decoy})
            {
                var result=GrenadeTargetTransfer.Resolve(source,GrenadeKind.Smoke,kind);
                assert(result.Kind==TargetTransferKind.PointToArea && result.Target is {IsArea:true,Airspace:false},"point not converted");
                assert(result.Target!.Value.Position==source.Position && result.Target.Value.AreaHalfSize==new Vector3(24,24,12),"converted region changed centre or radius");
                result.Target.Value.Validate();
            }
            assert(!source.IsArea,"source point was mutated");
        });
        test("Grenade switch preserves only compatible simple target geometry",()=>
        {
            var ground=new Target(new(20,30,2),48,new Vector3(32,64,12));
            var kept=GrenadeTargetTransfer.Resolve(ground,GrenadeKind.Molotov,GrenadeKind.Incendiary);
            assert(kept.Kind==TargetTransferKind.KeptArea && kept.Target is { } g && g.SameDefinition(ground),"simple ground region altered");
            var air=ground with {Airspace=true,AreaHalfSize=new Vector3(48,48,32)};
            var flash=GrenadeTargetTransfer.Resolve(air,GrenadeKind.HighExplosive,GrenadeKind.Flash);
            assert(flash.Target is { } f && f.SameDefinition(air),"compatible air target altered");
        });
        test("Grenade switch rejects spatial type changes instead of inheriting targets",()=>
        {
            var ground=new Target(Vector3.Zero,32,new Vector3(32,32,12));
            assert(GrenadeTargetTransfer.Resolve(ground,GrenadeKind.Smoke,GrenadeKind.Flash).Kind==TargetTransferKind.SpaceChanged,"ground inherited by timed kind");
            assert(GrenadeTargetTransfer.Resolve(ground with {Airspace=true},GrenadeKind.Flash,GrenadeKind.Molotov).Target is null,"air inherited by fire");
            assert(GrenadeTargetTransfer.Resolve(ground with {Airspace=true},GrenadeKind.Smoke,GrenadeKind.Decoy).Target is null,"inconsistent target spatial tag accepted");
        });
        test("Grenade switch clears complete complex objectives instead of hiding constraints",()=>
        {
            var simple=new Target(Vector3.Zero,32,new Vector3(32,32,12));
            var extra=new[]{new LandingRegion(new(80,0,0),new(16,16,12))};
            var objective=SmokeObjective.FromCorners(new(0,0,0),new(0,32,64),Vector3.UnitX);
            foreach(var source in new[]{simple with {ExtraRegions=extra},simple with {Exclusions=extra},simple with {Objective=objective}})
            {
                var result=GrenadeTargetTransfer.Resolve(source,GrenadeKind.Smoke,GrenadeKind.Molotov);
                assert(result.Kind==TargetTransferKind.ComplexTarget && result.Target is null,"complex target silently simplified or retained");
                var unchanged=GrenadeTargetTransfer.Resolve(source,GrenadeKind.Smoke,GrenadeKind.Smoke);
                assert(unchanged.Target is { } saved && saved.SameDefinition(source),"reselecting same kind destroyed constraints");
            }
        });
        test("Grenade switch keeps missing targets missing",()=>
        {
            var result=GrenadeTargetTransfer.Resolve(null,GrenadeKind.Smoke,GrenadeKind.Decoy);
            assert(result.Kind==TargetTransferKind.NoTarget && result.Target is null,"invented target on switch");
        });
    }
}
