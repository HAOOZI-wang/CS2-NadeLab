using System.Numerics;
using NadeLab.Core;

public static class EffectPredictionTests
{
    public static void Run(Action<string,Action> test,Action<bool,string> assert)
    {
        test("HE prediction weakens with distance and armor without exceeding remaining health",()=>
        {
            var clear=new PredictionVisibility(3,3);var center=Vector3.Zero;
            var near=EffectPrediction.HighExplosive(center,new(60,0,0),0,100,clear);
            var far=EffectPrediction.HighExplosive(center,new(260,0,0),0,100,clear);
            var armor=EffectPrediction.HighExplosive(center,new(60,0,0),100,100,clear);
            var lowArmor=EffectPrediction.HighExplosive(center,new(60,0,0),1,100,clear);
            var weak=EffectPrediction.HighExplosive(center,new(60,0,0),0,20,clear);
            assert(near.Valid && far.Maximum<near.Maximum && far.Minimum<near.Minimum,"distance does not change HE estimate");
            assert(armor.Maximum<near.Maximum && armor.Minimum<near.Minimum && lowArmor.Maximum>=armor.Maximum,"armor/depleted armor ignored");
            assert(weak.Maximum<=20 && weak.Minimum<=weak.Maximum,"predicted health loss exceeds current health");
        });
        test("Effect occlusion never claims guaranteed safety and invalid traces remain unknown",()=>
        {
            var open=EffectPrediction.HighExplosive(Vector3.Zero,new(60,0,0),0,100,new(3,3));
            var blocked=EffectPrediction.HighExplosive(Vector3.Zero,new(60,0,0),0,100,new(0,3));
            var partial=EffectPrediction.HighExplosive(Vector3.Zero,new(60,0,0),0,100,new(1,3));
            assert(blocked.Minimum==0 && blocked.Maximum>0 && blocked.Maximum==open.Maximum,"one blocked sample falsely establishes zero damage");
            assert(partial.Minimum==0 && partial.Text.Contains("部分遮挡"),"partial exposure not distinguished");
            assert(!EffectPrediction.HighExplosive(Vector3.Zero,new(60,0,0),0,100,new(0,3,false)).Valid,"invalid native query manufactures known damage");
            assert(!EffectPrediction.Flash(new(float.NaN,0,0),Vector3.Zero,new(0,0,ThrowMode.Left),new(1,1)).Valid,"nonfinite position manufactured a flash estimate");
        });
        test("Flash uses target eyes and facing including a nonzero rear estimate",()=>
        {
            var point=new Vector3(650,0,64);var eye=new Vector3(0,0,64);var clear=new PredictionVisibility(1,1);
            var front=EffectPrediction.Flash(point,eye,new(0,0,ThrowMode.Left),clear);
            var side=EffectPrediction.Flash(point,eye,new(0,90,ThrowMode.Left),clear);
            var back=EffectPrediction.Flash(point,eye,new(0,180,ThrowMode.Left),clear);
            var far=EffectPrediction.Flash(new(1100,0,64),eye,new(0,0,ThrowMode.Left),clear);
            assert(front.Maximum>side.Maximum && side.Maximum>back.Maximum && back.Maximum>0,"orientation is ignored or looking away falsely guarantees immunity");
            assert(far.Maximum<front.Maximum && front.Minimum<=front.Maximum && front.Maximum<=6,"flash distance/order bounds broken");
            assert(front.Minimum<4.18f && front.Maximum>4.18f,"workspace's forward-facing ~660u anchor falls outside provisional interval");
            var blocked=EffectPrediction.Flash(point,eye,new(0,0,ThrowMode.Left),new(0,1));
            assert(blocked.Minimum==0 && blocked.Maximum>0 && blocked.Text.Contains("遮挡"),"blocked flash reported as known zero");
        });
        test("Prediction outputs explicitly remain estimates and never become effect verification",()=>
        {
            var he=EffectPrediction.HighExplosive(Vector3.Zero,new(60,0,0),100,100,new(3,3));
            var flash=EffectPrediction.Flash(new(100,0,64),new(0,0,64),new(0,0,ThrowMode.Left),new(1,1));
            assert(he.Text.Contains("预测") && he.Text.Contains("粗估") && flash.Text.Contains("预测") && flash.Text.Contains("粗估"),"numeric estimate loses uncertainty label");
            assert(he.Text.Length<=32 && flash.Text.Length<=32,"prediction would overflow a dummy status row");
            assert(!new EffectEstimate(false,GrenadeKind.Flash,0,0,0,0,new(0,1,false)).Text.Contains("0.0"),"unknown trace displayed as zero effect");
        });
    }
}
