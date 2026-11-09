using System.Numerics;

namespace NadeLab.Core;

public sealed class PreparationState
{
    private Vector3? previous;
    private float previousEye,stableSince;
    public bool Observe(float now,Vector3 feet,float eye,bool ready)
    {
        if(!ready || previous is not { } old || Vector3.Distance(old,feet)>.15f || MathF.Abs(previousEye-eye)>.1f)
        {
            stableSince=now;
            previous=feet; previousEye=eye;
        }
        return ready && now-stableSince>=.25f;
    }
}

public enum ReleaseStage { None, Correct, Approach, Soon, Passed, Steady, Window }
public static class ReleaseGuidance
{
    public static ReleaseStage Stage(Vector3 feet,Vector3 line,Vector3 velocity,Aim aim,bool aligned,ReleaseWindow? window=null,float releaseDelay=.1f)
    {
        var forward=Geometry.Direction(0,aim.Yaw);
        var delta=line-feet; delta.Z=0;
        float distance=Vector3.Dot(delta,forward);
        var zone=window?.ButtonOffsets(aim,releaseDelay);
        float nominal=aim.Airborne ? aim.ForwardOffset-aim.ForwardSpeed*JumpLatch.Seconds : aim.ForwardOffset-aim.ForwardSpeed*releaseDelay;
        float end=zone is { } bounds ? bounds.Y-nominal : ExecutionEnvelope.Position(aim);
        if(distance < -end) return ReleaseStage.Passed;
        float speed=Vector3.Dot(velocity,forward);
        bool speedValid=window is null ? MathF.Abs(speed-aim.ForwardSpeed)<=ExecutionEnvelope.Speed : speed>=window.MinSpeed && speed<=window.MaxSpeed;
        if(!aligned || (delta-forward*distance).Length()>ExecutionEnvelope.Position(aim) || !speedValid ||
            (velocity-forward*speed).Length()>ExecutionEnvelope.Speed) return ReleaseStage.Correct;
        if(speed<=1) return ReleaseStage.None;
        if(zone is { } area)
        {
            float entry=area.X-nominal;
            if(distance<=-entry) return ReleaseStage.Window;
            distance+=entry;
        }
        // These are advance notices, never a delayed 'release now' signal.
        return distance/speed<=.22f ? ReleaseStage.Soon : distance/speed<=.65f ? ReleaseStage.Approach : ReleaseStage.Steady;
    }
}
