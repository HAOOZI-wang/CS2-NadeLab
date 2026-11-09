using System.Numerics;
namespace NadeLab.Core;

public static class PersonalPose
{
    public static bool CanRecall(bool stable,float currentEyeHeight,float savedEyeHeight)=>
        stable && float.IsFinite(currentEyeHeight) && float.IsFinite(savedEyeHeight) &&
        currentEyeHeight is >=32 and <=80 && savedEyeHeight is >=32 and <=80 &&
        (currentEyeHeight<56)==(savedEyeHeight<56);

    public static bool Stable(bool walking,bool grounded,Vector3 velocity,float duck,float eyeHeight)=>
        walking && grounded && Geometry.Finite(velocity) && velocity.Length()<=1 &&
        float.IsFinite(duck) && float.IsFinite(eyeHeight) && eyeHeight is >=32 and <=80 &&
        (duck is >=0 and <.05f && eyeHeight>=60 || duck is >.95f and <=1 && eyeHeight<=50);
}
