using System.Numerics;
using NadeLab.Core;

public static class GrenadeBodyCameraTests
{
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        test("Grenade first-person camera is exactly the current centre, independent of angle",()=>
        {
            var center=new Vector3(30,20,140);var world=new FixedCameraWorld(new(1,center,Vector3.UnitZ,false));
            foreach(float pitch in new[]{-80f,0,80}) foreach(float yaw in new[]{-179f,0,179})
            {var camera=GrenadeBodyCamera.Resolve(world,center,pitch,yaw,false);assert(camera.Position==center,"first-person view added an eye/forward offset");}
            assert(world.Traces==0,"first-person camera spent collision traces");
        });
        test("Grenade third-person camera looks at the same centre without moving the explosion point",()=>
        {
            var center=new Vector3(0,0,100);var camera=GrenadeBodyCamera.Resolve(new AnalyticWorld(),center,20,40,true);
            assert(Vector3.Distance(camera.Position,center)>50,"camera did not move outside the body");
            assert(Vector3.Dot(Vector3.Normalize(center-camera.Position),Geometry.Direction(camera.Angles.X,camera.Angles.Y))>.9999f,"third-person view misses grenade centre");
        });
        test("Grenade third-person camera retracts before walls",()=>
        {
            var center=new Vector3(0,0,64);var wall=new AnalyticWorld(0,new Box(new(-30,-100,0),new(-25,100,150)));
            var camera=GrenadeBodyCamera.Resolve(wall,center,0,0,true);
            assert(camera.Position.X>-23 && camera.Position.X<0,"camera crossed wall or failed to retract");
            assert(!wall.Sweep(center,camera.Position,2).Hit,"retracted camera is obstructed");
        });
        test("Grenade camera never ignores an obstruction at the end of its sweep",()=>
        {
            var center=new Vector3(0,0,64);var desired=new Vector3(-72,0,76);
            var world=new FixedCameraWorld(new(.999f,Vector3.Lerp(center,desired,.999f),Vector3.UnitX,false));
            var camera=GrenadeBodyCamera.Resolve(world,center,0,0,true);
            assert(Vector3.Distance(center,camera.Position)<Vector3.Distance(center,desired)*.999f,"camera crossed thin endpoint obstruction");
        });
        test("Grenade camera fails closed for solid or malformed native traces",()=>
        {
            var center=new Vector3(0,0,64);
            foreach(var hit in new[]{new SweepHit(0,center,Vector3.UnitZ,true),new SweepHit(float.NaN,center,Vector3.UnitZ,false),new SweepHit(1,new(float.NaN,0,0),Vector3.UnitZ,false)})
            {
                var camera=GrenadeBodyCamera.Resolve(new FixedCameraWorld(hit),center,0,0,true);
                assert(camera.Position==center,"invalid trace moved camera into unverified space");
            }
        });
    }

    private sealed class FixedCameraWorld(SweepHit hit) : ICollisionWorld
    {
        public int Traces;
        public SweepHit Sweep(Vector3 start,Vector3 end,float halfSize) {Traces++;return hit;}
    }
}
