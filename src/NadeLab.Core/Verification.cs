using System.Numerics;

namespace NadeLab.Core;

public sealed record ThrowObservation(Vector3 Feet,float EyeHeight,Aim Aim,float Strength,Vector3 Velocity,bool Standing,float Time,bool GroundedUpright=false,bool OnGround=false,bool JumpThrow=false);
public static class Verification
{
    // This is a distinct execution contract. Do not use it to relabel the
    // original fixed-origin/velocity calibration record as Matched.
    public static bool TryGuidedSetup(ThrowStart start,Candidate candidate,ThrowObservation? observation,float time,out ThrowStart adjustedStart,out Aim adjustedAim,PhysicsProfile? physics=null)
    {
        adjustedStart=start;adjustedAim=candidate.Aim;
        if(candidate.ReleaseWindow is not { } window || !candidate.Aim.NeedsRunUp || observation is not { } o ||
            !Geometry.Finite(o.Feet) || !Geometry.Finite(o.Velocity) || !float.IsFinite(time) || !float.IsFinite(o.Time) || time-o.Time is <0 or >=.1f) return false;
        var forward=Geometry.Direction(0,candidate.Aim.Yaw);
        float speed=Vector3.Dot(o.Velocity,forward);
        if(speed<window.MinSpeed || speed>window.MaxSpeed) return false;
        adjustedAim=candidate.Aim with {ForwardSpeed=speed,ForwardOffset=candidate.Aim.ForwardOffset+(candidate.Aim.Airborne ? (speed-candidate.Aim.ForwardSpeed)*JumpLatch.Seconds : 0)};
        var release=o.Feet+o.Velocity*(time-o.Time);
        float offset=Vector3.Dot(release-ThrowExecution.Resolve(start,adjustedAim,physics ?? new()).ReleaseFeet,forward);
        // The visible region is defined at nominal speed; stay within that
        // region AND the sampled time allowance at the observed speed.
        float distanceScale=MathF.Min(speed,candidate.Aim.ForwardSpeed);
        if(offset < -window.EarlySeconds*distanceScale || offset > window.LateSeconds*distanceScale) return false;
        adjustedStart=start.Origin==ThrowOrigin.Preparation ? start with {RunUpDistance=start.RunUpDistance+offset} : start with {Feet=start.Feet+forward*offset};
        return Matches(adjustedStart,adjustedAim,o,time,physics);
    }
    // A nearby endpoint does not verify a lineup unless the observed action matches it.
    public static bool Matches(ThrowStart start,Aim aim,ThrowObservation? observed,float releaseTime,PhysicsProfile? physics=null)
        => Evaluate(start,aim,observed,releaseTime,null,physics);
    public static IReadOnlyList<string> ExplainMismatch(ThrowStart start,Aim aim,ThrowObservation? observed,float releaseTime,PhysicsProfile? physics=null)
    {
        var reasons=new List<string>(); Evaluate(start,aim,observed,releaseTime,reasons,physics); return reasons;
    }
    private static bool Evaluate(ThrowStart start,Aim aim,ThrowObservation? observed,float releaseTime,List<string>? reasons,PhysicsProfile? physics)
    {
        if(!ThrowExecution.Valid(start)) {reasons?.Add("投掷起点数据无效");return false;}
        start=ThrowExecution.Resolve(start,aim,physics ?? new()).SimulationStart;
        if (observed is not { } o || !Geometry.Finite(o.Feet) || !Geometry.Finite(o.Velocity) ||
            !float.IsFinite(o.Strength) || !float.IsFinite(o.Time) || !float.IsFinite(releaseTime) ||
            !float.IsFinite(o.EyeHeight) || !float.IsFinite(o.Aim.Pitch) || !float.IsFinite(o.Aim.Yaw))
        {reasons?.Add("缺少有效出手记录");return false;}
        // A latched jump uses the cached launch pose, not the live player height
        // and vertical velocity at construction. Check its actual launch below.
        bool motion=aim.Airborne ? o.JumpThrow && !o.OnGround &&
            new Vector2(o.Velocity.X-aim.PlayerVelocity.X,o.Velocity.Y-aim.PlayerVelocity.Y).Length()<=ExecutionEnvelope.Speed :
            aim.EffectiveAction==ThrowAction.Crouch ? o.OnGround && o.Velocity.Length()<=1 :
            aim.ForwardSpeed<=0 ? o.Standing && o.Velocity.Length()<=1 :
            o.GroundedUpright && MathF.Abs(o.Velocity.Z)<=2 && Vector3.Distance(o.Velocity,aim.PlayerVelocity)<=ExecutionEnvelope.Speed;
        // A one-tick observation may precede the moving release by ~4 units.
        var releaseFeet=o.Feet+o.Velocity*Math.Clamp(releaseTime-o.Time,0,.1f);
        bool position=aim.Airborne ? o.Feet.Z-start.Feet.Z is >=-2 and <=65 &&
            new Vector2(releaseFeet.X-aim.ReleaseFeet(start).X,releaseFeet.Y-aim.ReleaseFeet(start).Y).Length()<= (aim.ForwardSpeed>0 ? 80 : 8) :
            Vector3.Distance(releaseFeet,aim.ReleaseFeet(start))<= ExecutionEnvelope.Position(aim);
        bool timing=releaseTime-o.Time is >=0 and <.1f;
        bool eye=MathF.Abs(o.EyeHeight-aim.EyeHeight(start))<=ExecutionEnvelope.EyeHeight;
        bool angles=MathF.Abs(o.Aim.Pitch-aim.Pitch)<=ExecutionEnvelope.Angle && MathF.Abs(Geometry.NormalizeYaw(o.Aim.Yaw-aim.Yaw))<=ExecutionEnvelope.Angle;
        bool strength=MathF.Abs(o.Strength-aim.Strength)<=.08f;
        if(reasons is not null)
        {
            if(!timing) reasons.Add("出手快照超时，无法可靠核对");
            if(!position)
            {
                var difference=releaseFeet-aim.ReleaseFeet(start);
                reasons.Add($"出手位置偏差：水平 {new Vector2(difference.X,difference.Y).Length():F1}u / 高度 {difference.Z:+0.0;-0.0;0.0}u");
            }
            if(!angles) reasons.Add($"角度超差：上下 {o.Aim.Pitch-aim.Pitch:+0.00;-0.00;0.00}° / 水平 {Geometry.NormalizeYaw(o.Aim.Yaw-aim.Yaw):+0.00;-0.00;0.00}°，允许±{ExecutionEnvelope.Angle:F1}°");
            if(!eye) reasons.Add($"眼高偏差 {o.EyeHeight-aim.EyeHeight(start):+0.0;-0.0;0.0}u，需站稳重搜");
            if(!strength) reasons.Add("鼠标力度与方案不符");
            if(!motion) reasons.Add(aim.Airborne && !o.JumpThrow ? "未触发跳投锁定，起跳同时松鼠标" :
                aim.ForwardSpeed>0 ? $"移动速度/姿态不符：水平速度 {new Vector2(o.Velocity.X,o.Velocity.Y).Length():F0}/{aim.ForwardSpeed:F0}u/s" :
                "落地、蹲站姿态或静止条件不符");
        }
        return timing && motion && position && eye && angles && strength;
    }

    // Native initial velocity plus the first observed position confirm the
    // cached jump state; a matching live snapshot alone cannot verify it.
    public static bool MatchesJumpLaunch(Vector3 source,Vector3 expectedVelocity,Vector3 actualVelocity,Vector3 firstPosition,float step,float gravity=320)
    {
        if(!Geometry.Finite(source) || !Geometry.Finite(expectedVelocity) || !Geometry.Finite(actualVelocity) || !Geometry.Finite(firstPosition)) return false;
        return Vector3.Distance(expectedVelocity,actualVelocity)<=8 &&
            MathF.Min(Vector3.Distance(firstPosition,source),Vector3.Distance(firstPosition,source+actualVelocity*step-new Vector3(0,0,.5f*gravity*step*step)))<=6;
    }

}
