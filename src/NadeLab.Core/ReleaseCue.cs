using System.Numerics;

namespace NadeLab.Core;

public sealed record ReleaseCueFrame(ReleaseStage Stage,float Progress,string Text);
public static class ReleaseCue
{
    public static ReleaseCueFrame Build(ThrowStart start,Aim aim,PhysicsProfile physics,Vector3 feet,Vector3 velocity,bool aligned,ReleaseWindow? window)
    {
        var line=ThrowExecution.Resolve(start,aim,physics).ButtonFeet;
        var forward=Geometry.Direction(0,aim.Yaw);
        float remaining=Vector3.Dot(line-feet,forward);
        var side=line-feet-forward*remaining;side.Z=0;
        bool onPath=side.Length()<=ExecutionEnvelope.Position(aim);
        float length=start.Origin==ThrowOrigin.Preparation ? start.RunUpDistance : ThrowExecution.RunUpDistance;
        float progress=Math.Clamp(1-remaining/length,0,1);
        var stage=ReleaseGuidance.Stage(feet,line,velocity,aim,aligned,window,start.Origin==ThrowOrigin.Preparation ? 0 : physics.ReleaseDelaySeconds);
        string release=aim.Airborne ? "跳+松鼠标" : "松鼠标并继续前进";
        string action=ThrowInstructions.RunUpKeys(aim)+" → "+release;
        string status=stage switch
        {
            ReleaseStage.Window=>"进入采样区 · "+release,
            ReleaseStage.Passed=>"已过线 · 重新就位",
            ReleaseStage.Correct when aligned && onPath && velocity.Length()<1 && remaining>20=>"蓄力后起步",
            ReleaseStage.Correct=>"调整速度 / 方向",
            ReleaseStage.Soon=>"准备 · 即将到线",
            _=>"准备 · 保持方向"
        };
        int filled=(int)MathF.Round(progress*10);
        string bar=new string('■',filled)+new string('□',10-filled);
        return new(stage,progress,$"{action}\n[{bar}] {MathF.Max(0,remaining):F0}u · {status}");
    }
}
