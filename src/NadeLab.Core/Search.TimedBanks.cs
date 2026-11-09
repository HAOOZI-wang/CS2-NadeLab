using System.Numerics;

namespace NadeLab.Core;

public sealed partial class LineupSearch
{
    // Invert one vertical-plane bounce at the fixed fuse time. This only
    // proposes an angle: finite walls, floors, additional contacts and the
    // actual throw speed are checked by the unmodified simulator/reviewer.
    private IEnumerable<Aim> TimedBankProposals(Vector3 anchor,Vector3 plane,Vector3 normal,IReadOnlyList<Aim> actions)
    {
        if(physics.Restitution<=.01f || physics.TangentialRetention<=.01f) yield break;
        normal=Vector3.Normalize(new Vector3(normal.X,normal.Y,0));
        float destinationSide=Vector3.Dot(anchor-plane,normal);
        if(destinationSide<=0) yield break;
        foreach(var action in actions)
        {
            var mirrored=anchor-2*destinationSide*normal-start.Feet;
            var aim=action with {Pitch=-20,Yaw=MathF.Atan2(mirrored.Y,mirrored.X)*180/MathF.PI};
            bool usable=true;
            // Launch offset and inherited motion depend on the proposed view.
            // A few bounded fixed-point updates are sufficient for a seed;
            // no extra collision traces or engine calls are performed here.
            for(int iteration=0;iteration<4;iteration++)
            {
                float corrected=aim.Pitch-(90-MathF.Abs(aim.Pitch))*10/90;
                var source=ThrowExecution.Resolve(start,aim,physics).ReleaseFeet+new Vector3(0,0,aim.EyeHeight(start)+aim.Strength*12-12)+Geometry.Direction(corrected,aim.Yaw)*16;
                float sourceSide=Vector3.Dot(source-plane,normal),time=physics.FuseSeconds;
                if(sourceSide<=0) {usable=false;break;}
                float normalSpeed=-(destinationSide+physics.Restitution*sourceSide)/(physics.Restitution*time);
                float before=-sourceSide/normalSpeed,after=time-before;
                if(before<=0 || after<=0) {usable=false;break;}
                float travel=before+physics.TangentialRetention*after;
                var delta=anchor-source;
                var required=(delta-Vector3.Dot(delta,normal)*normal)/travel+normalSpeed*normal;
                required.Z+=.5f*physics.Gravity*(before*before+2*physics.TangentialRetention*before*after+after*after)/travel;
                var released=required-aim.PlayerVelocity*physics.PlayerVelocityScale;
                float horizontal=new Vector2(released.X,released.Y).Length();
                if(horizontal<1 || !Geometry.Finite(released)) {usable=false;break;}
                float physical=-MathF.Atan2(released.Z,horizontal)*180/MathF.PI;
                aim=aim with {Pitch=Math.Clamp((physical+10)*(physical<=-10 ? 9f/8 : 9f/10),-89,89),Yaw=MathF.Atan2(released.Y,released.X)*180/MathF.PI};
            }
            if(usable) yield return aim;
        }
    }
}
