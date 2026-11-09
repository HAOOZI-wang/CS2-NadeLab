using System.Numerics;
using NadeLab.Core;

public static class TargetConnectivityTests
{
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        test("Ground effect targets accept their placed centre beneath a low shelf",()=>
        {
            var world=new AnalyticWorld(0,new Box(new(-100,-100,16),new(100,100,100)));
            foreach(var kind in new[]{GrenadeKind.Smoke,GrenadeKind.Molotov,GrenadeKind.Incendiary,GrenadeKind.Decoy})
            {
                var placed=EffectPlacement.Resolve(world,kind,new(0,0,8));
                var target=new Target(placed.Position,48,new Vector3(48,48,12));
                assert(placed.Valid && target.Accepts(world,placed.Position),$"{kind}: own valid centre rejected");
                assert(target.Accepts(world,new(10,0,2)),"connected near-floor endpoint rejected");
            }
        });
        test("Ground target connectivity preserves walls and cannot escape through a ceiling",()=>
        {
            var target=new Target(new(-20,0,2),64,new Vector3(64,32,12));
            var wall=new AnalyticWorld(0,new Box(new(-2,-100,0),new(2,100,200)));
            assert(!target.Accepts(wall,new(20,0,2)),"accepted opposite side of a tall wall");
            var covered=new AnalyticWorld(0,new Box(new(-2,-100,0),new(2,100,16)),new Box(new(-100,-100,16),new(100,100,32)));
            assert(!target.Accepts(covered,new(20,0,2)),"lifted connectivity above solid ceiling");
        });
        test("Ground connectivity handles low steps and legacy floor-height centres",()=>
        {
            var step=new AnalyticWorld(0,new Box(new(-2,-100,0),new(2,100,4)));
            var target=new Target(new(-20,0,0),64,new Vector3(64,32,12));
            assert(target.Accepts(step,new(20,0,2)),"low floor obstacle rejected despite clear path above it");
            assert(target.Accepts(new AnalyticWorld(),new(-20,0,2)),"legacy ground-surface target rejected");
        });
        test("Air targets keep direct three-dimensional visibility",()=>
        {
            var target=new Target(new(-20,0,10),64,new Vector3(64,32,12),Airspace:true);
            var obstacle=new AnalyticWorld(0,new Box(new(-2,-100,0),new(2,100,14)));
            assert(!target.Accepts(obstacle,new(20,0,10)),"air target used a ground lift to bypass obstruction");
        });
    }
}
