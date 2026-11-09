using System.Numerics;

namespace NadeLab.Core;

public static class SmokeTargets
{
    // Room-floor anchor from the captured Mirage VIP selections. These bounds
    // are an editable landing-region heuristic, not a smoke voxel simulation.
    // The observed (-1083,-673,-190) ledge landing matched the trajectory but
    // left the window open. Require the room floor; coverage still needs sight checks.
    public static Target MirageVip => new(new(-1216,-636,-168),96,new(96,80,12),"VIP窗口烟（室内落点，覆盖待确认）");
    public static bool NearMirageVip(Vector3 point) => point.X is >=-1400 and <=-1080 && point.Y is >=-840 and <=-440 && point.Z is >=-210 and <=40;
    public static Target Around(Vector3 floor,float radius=96) => new(floor,radius,new(radius,radius,24),"封烟区域（待实投确认）");
}
