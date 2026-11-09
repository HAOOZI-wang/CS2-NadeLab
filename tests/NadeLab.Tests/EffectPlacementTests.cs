using System.Numerics;
using NadeLab.Core;

public static class EffectPlacementTests
{
    public static void Run(Action<string, Action> test, Action<bool, string> assert)
    {
        test("Effect placement permits airborne flash and HE centres", () =>
        {
            var desired = new Vector3(20, 30, 200);
            foreach (var kind in new[] { GrenadeKind.Flash, GrenadeKind.HighExplosive })
            {
                var result = EffectPlacement.Resolve(new AnalyticWorld(), kind, desired);
                assert(result.Valid && result.Position == desired, "air effect was grounded or rejected");
            }
        });
        test("Effect placement rejects solid centres including fraction-only occupancy", () =>
        {
            var solid = new AnalyticWorld(0, new Box(new(-10, -10, 0), new(10, 10, 100)));
            foreach (var kind in Enum.GetValues<GrenadeKind>())
            {
                assert(!EffectPlacement.Resolve(solid, kind, new(0, 0, 50)).Valid, "solid accepted");
                var fractionOnly = new StubWorld((start, end, hull) => new(0, start, Vector3.Zero));
                assert(!EffectPlacement.Resolve(fractionOnly, kind, new(0, 0, 50)).Valid, "Fraction=0 accepted");
            }
        });
        test("Grounded effects require nearby support and preserve hull centre height", () =>
        {
            foreach (var kind in new[] { GrenadeKind.Smoke, GrenadeKind.Molotov, GrenadeKind.Incendiary, GrenadeKind.Decoy })
            {
                var result = EffectPlacement.Resolve(new AnalyticWorld(10), kind, new(20, 30, 60));
                assert(result.Valid && Vector3.Distance(result.Position, new(20, 30, 12)) < .001f, "floor/feet mistaken for centre");
                assert(!EffectPlacement.Resolve(new AnalyticWorld(-1000), kind, new(20, 30, 60)).Valid, "snapped to distant lower floor");
                var empty = new StubWorld((start, end, hull) => SweepHit.Miss(end));
                assert(!EffectPlacement.Resolve(empty, kind, new(20, 30, 60)).Valid, "missing ground accepted");
            }
        });
        test("Grounded effects reject steep slopes with stricter fire limits", () =>
        {
            var moderate = Slope(.8f);
            assert(EffectPlacement.Resolve(moderate, GrenadeKind.Smoke, new(0, 0, 20)).Valid, "smoke support rejected");
            assert(EffectPlacement.Resolve(moderate, GrenadeKind.Decoy, new(0, 0, 20)).Valid, "decoy support rejected");
            assert(!EffectPlacement.Resolve(moderate, GrenadeKind.Molotov, new(0, 0, 20)).Valid, "T fire accepted steep slope");
            assert(!EffectPlacement.Resolve(moderate, GrenadeKind.Incendiary, new(0, 0, 20)).Valid, "CT fire accepted steep slope");
            assert(!EffectPlacement.Resolve(Slope(.6f), GrenadeKind.Smoke, new(0, 0, 20)).Valid, "unstable slope accepted");
        });
        test("Ground ray hits place smoke fire and decoy on 20 and 30 degree slopes",()=>
        {
            foreach(float degrees in new[]{20f,30f})
            foreach(float yaw in new[]{0f,45f})
            {
                float tilt=degrees*MathF.PI/180,heading=yaw*MathF.PI/180;
                var normal=Vector3.Normalize(new Vector3(MathF.Sin(tilt)*MathF.Cos(heading),MathF.Sin(tilt)*MathF.Sin(heading),MathF.Cos(tilt)));
                var world=Plane(normal);
                assert(!EffectPlacement.Resolve(world,GrenadeKind.Smoke,normal*2.125f).Valid,"fixture must expose the old spherical-clearance overlap");
                foreach(var kind in new[]{GrenadeKind.Smoke,GrenadeKind.Molotov,GrenadeKind.Incendiary,GrenadeKind.Decoy})
                {
                    var result=EffectPlacement.ResolveGroundHit(world,kind,Vector3.Zero,normal*3);
                    float support=2*(MathF.Abs(normal.X)+MathF.Abs(normal.Y)+MathF.Abs(normal.Z));
                    assert(result.Valid,$"{kind} rejected valid {degrees} degree slope at yaw {yaw}");
                    assert(MathF.Abs(Vector3.Dot(result.Position,normal)-support)<.001f,"placed centre not tangent to the selected plane");
                }
            }
        });
        test("Ground ray clearance does not relax steep-slope limits",()=>
        {
            foreach(float degrees in new[]{35f,46f})
            {
                float angle=degrees*MathF.PI/180;var normal=new Vector3(MathF.Sin(angle),0,MathF.Cos(angle));var world=Plane(normal);
                foreach(var kind in new[]{GrenadeKind.Molotov,GrenadeKind.Incendiary})
                    assert(!EffectPlacement.ResolveGroundHit(world,kind,Vector3.Zero,normal).Valid,"fire accepted over-30-degree slope");
                foreach(var kind in new[]{GrenadeKind.Smoke,GrenadeKind.Decoy})
                    assert(EffectPlacement.ResolveGroundHit(world,kind,Vector3.Zero,normal).Valid==(degrees==35),"smoke/decoy slope threshold changed");
            }
        });
        test("Ground ray placement still rejects occupied offset and invalid normals",()=>
        {
            var occupied=new StubWorld((start,end,hull)=>new(0,start,Vector3.UnitZ,true));
            assert(!EffectPlacement.ResolveGroundHit(occupied,GrenadeKind.Smoke,Vector3.Zero,Vector3.UnitZ).Valid,"nearby obstruction was bypassed");
            foreach(var normal in new[]{Vector3.Zero,new Vector3(float.NaN,0,1)})
                assert(!EffectPlacement.ResolveGroundHit(new AnalyticWorld(),GrenadeKind.Decoy,Vector3.Zero,normal).Valid,"invalid surface normal accepted");
        });
        test("Effect placement fits grenade-sized gaps without requiring a player hull", () =>
        {
            var narrow = new AnalyticWorld(0,
                new Box(new(-20, -20, 0), new(-3, 20, 100)),
                new Box(new(3, -20, 0), new(20, 20, 100)));
            assert(narrow.Sweep(new(0, 0, 20), new(0, 0, 20), 16).StartSolid, "fixture accommodates player hull");
            var result = EffectPlacement.Resolve(narrow, GrenadeKind.Smoke, new(0, 0, 20));
            assert(result.Valid && MathF.Abs(result.Position.Z - 2) < .001f, "grenade gap rejected by player dimensions");
        });
        test("Effect placement stops at first surface and never lifts out of solid", () =>
        {
            var ledge = new AnalyticWorld(0, new Box(new(-10, -10, 30), new(10, 10, 35)));
            var result = EffectPlacement.Resolve(ledge, GrenadeKind.Smoke, new(0, 0, 60));
            assert(result.Valid && MathF.Abs(result.Position.Z - 37) < .001f, "crossed first surface");
            assert(!EffectPlacement.Resolve(ledge, GrenadeKind.Smoke, new(0, 0, 34)).Valid, "lifted marker through solid");
        });
        test("Effect placement rejects nonfinite input and malformed traces", () =>
        {
            var floor = new AnalyticWorld();
            assert(!EffectPlacement.Resolve(floor, GrenadeKind.Flash, new(float.NaN, 0, 10)).Valid, "NaN accepted");
            assert(!EffectPlacement.Resolve(floor, GrenadeKind.Smoke, new(0, 0, 20), groundReach: 1000).Valid, "unbounded ground reach accepted");
            var malformed = new StubWorld((start, end, hull) => new(float.NaN, start, Vector3.UnitZ));
            assert(!EffectPlacement.Resolve(malformed, GrenadeKind.HighExplosive, new(0, 0, 20)).Valid, "NaN trace accepted");
        });
        test("Effect drawing extents are independent per kind and explicitly approximate", () =>
        {
            assert(EffectReference.Radius(GrenadeKind.Molotov) != EffectReference.Radius(GrenadeKind.Incendiary), "fire extents conflated");
            assert(EffectReference.Radius(GrenadeKind.Decoy) == 0, "decoy gained effect rendering");
            assert(EffectReference.Radius(GrenadeKind.Flash)==0 && !EffectReference.HasAreaPreview(GrenadeKind.Flash), "removed flash preview was reenabled");
        });
    }

    private static ICollisionWorld Slope(float normalZ) => new StubWorld((start, end, hull) => start == end
        ? SweepHit.Miss(end)
        : new(.5f, Vector3.Lerp(start, end, .5f), new(MathF.Sqrt(1 - normalZ * normalZ), 0, normalZ)));

    private static ICollisionWorld Plane(Vector3 normal)=>new StubWorld((start,end,hull)=>
    {
        float extent=hull*(MathF.Abs(normal.X)+MathF.Abs(normal.Y)+MathF.Abs(normal.Z));
        float from=Vector3.Dot(start,normal)-extent,to=Vector3.Dot(end,normal)-extent;
        if(from<=0) return new(0,start,normal,true);
        if(to>=0) return SweepHit.Miss(end);
        float fraction=from/(from-to);
        return new(fraction,Vector3.Lerp(start,end,fraction),normal);
    });

    private sealed class StubWorld(Func<Vector3, Vector3, float, SweepHit> trace) : ICollisionWorld
    {
        public SweepHit Sweep(Vector3 start, Vector3 end, float halfSize) => trace(start, end, halfSize);
    }
}
