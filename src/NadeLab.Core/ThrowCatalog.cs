namespace NadeLab.Core;

public static class ThrowCatalog
{
    // The community action names describe execution, not calibrated launch data.
    // The measured player cvars parameterize provisional airborne states.
    public static IReadOnlyList<Aim> Families(PhysicsProfile physics,SearchOptions options,float distance,float yaw)
    {
        var families=new List<Aim>();
        foreach(var mode in Enum.GetValues<ThrowMode>())
        {
            families.Add(new(0,yaw,mode));
            if(options.IncludeForward || options.IncludeActions) families.Add(new(0,yaw,mode,options.ForwardSpeed,ThrowAction.Run));
            if(!options.IncludeActions) continue;
            families.Add(new(0,yaw,mode,options.ForwardSpeed*physics.WalkSpeedRatio,ThrowAction.Walk));
            if(options.Exhaustive)
            {
                families.Add(new(0,yaw,mode,100,ThrowAction.Step,ForwardOffset:5));
                families.Add(new(0,yaw,mode,180,ThrowAction.Step,ForwardOffset:18));
            }
            families.Add(new(0,yaw,mode,Action:ThrowAction.Crouch));
            float height=JumpLatch.Height(physics),vertical=JumpLatch.VerticalSpeed(physics);
            families.Add(new(0,yaw,mode,Action:ThrowAction.Jump,ReleaseHeight:height,VerticalSpeed:vertical));
            if(options.AirForwardSpeed>0)
                families.Add(new(0,yaw,mode,options.AirForwardSpeed,ThrowAction.AirForwardJump,height,vertical,options.AirForwardSpeed*JumpLatch.Seconds));
            foreach(float speed in new[]{options.ForwardSpeed,options.ForwardSpeed*physics.WalkSpeedRatio})
                families.Add(new(0,yaw,mode,speed,ThrowAction.RunJump,height,vertical,speed*JumpLatch.Seconds));
            if(options.Exhaustive)
                foreach(float speed in new[]{60f,90f,120f,180f})
                    families.Add(new(0,yaw,mode,speed,ThrowAction.StepJump,height,vertical,5+speed*JumpLatch.Seconds));
        }
        // Prioritize plausible range families; all families still receive an
        // initial scan before the expensive all-heading stage.
        float Range(Aim a)
        {
            float speed=physics.ThrowSpeed*(.3f+.7f*a.Strength);
            return (speed+a.ForwardSpeed*physics.PlayerVelocityScale)*(speed+a.ForwardSpeed*physics.PlayerVelocityScale+MathF.Max(0,a.VerticalSpeed)*physics.PlayerVelocityScale)/physics.Gravity;
        }
        return families.OrderBy(a=>distance>Range(a)*1.2f ? 1 : 0)
            .ThenBy(a=>a.Airborne ? 1 : 0).ThenBy(a=>a.EffectiveAction==ThrowAction.Standing ? 0 : 1).ToArray();
    }
}
