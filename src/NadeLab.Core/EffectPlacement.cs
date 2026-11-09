using System.Numerics;

namespace NadeLab.Core;

public readonly record struct PlacementResult(bool Valid, Vector3 Position, string Reason);

/// <summary>Places a grenade centre, independently of whether a player fits there.</summary>
public static class EffectPlacement
{
    /// <summary>Starts a ground-placement hull outside a ray-hit plane, including slopes.</summary>
    public static PlacementResult ResolveGroundHit(ICollisionWorld world,GrenadeKind kind,Vector3 surface,Vector3 normal,float hull=2)
    {
        ArgumentNullException.ThrowIfNull(world);
        float length=normal.Length();
        if(!Enum.IsDefined(kind) || kind.Timed() || !Geometry.Finite(surface) || !Geometry.Finite(normal) ||
            !float.IsFinite(length) || length<.001f || !float.IsFinite(hull) || hull is <.1f or >8)
            return new(false,Geometry.Finite(surface) ? surface : Vector3.Zero,"地面选点参数无效");
        normal/=length;
        // NativeWorld sweeps an axis-aligned cube, not a sphere. Its support
        // radius against a sloped plane is h*(|nx|+|ny|+|nz|).
        float clearance=hull*(MathF.Abs(normal.X)+MathF.Abs(normal.Y)+MathF.Abs(normal.Z))+.125f;
        return Resolve(world,kind,surface+normal*clearance,hull);
    }

    public static PlacementResult Resolve(ICollisionWorld world, GrenadeKind kind, Vector3 desired,
        float hull = 2, float groundReach = 96)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!Enum.IsDefined(kind) || !Geometry.Finite(desired) || !float.IsFinite(hull) || hull is < .1f or > 8 ||
            !float.IsFinite(groundReach) || groundReach is <= 0 or > 96)
            return new(false, Geometry.Finite(desired) ? desired : Vector3.Zero, "定位参数无效");

        // Some trace providers report Fraction=0 without StartSolid. Either
        // form of an occupied zero-length hull trace must reject the marker.
        var occupied = world.Sweep(desired, desired, hull);
        if (!TraceValid(occupied) || occupied.Hit)
            return new(false, desired, "标记与墙体或地面重叠");

        if (kind.Timed()) return new(true, desired, "空中生效点");

        // Never lift through a ceiling or search far below the selected level.
        // The swept hull's returned position is already the grenade centre.
        var support = world.Sweep(desired, desired - Vector3.UnitZ * groundReach, hull);
        if (!TraceValid(support) || support.StartSolid)
            return new(false, desired, "地面检测无效");
        if (!support.Hit)
            return new(false, desired, "附近下方没有可放置的地面");
        if (Vector2.Distance(new(desired.X, desired.Y), new(support.Position.X, support.Position.Y)) > .01f ||
            support.Position.Z > desired.Z + .01f || desired.Z - support.Position.Z > groundReach + .01f)
            return new(false, desired, "地面检测超出选定范围");
        float normalLength = support.Normal.Length();
        float minimumZ = kind.Fire() ? .8660254f : .7f;
        if (normalLength < .001f || support.Normal.Z / normalLength < minimumZ)
            return new(false, support.Position, kind.Fire() ? "斜面过陡，不能作为起火点" : "斜面过陡，不能稳定落地");
        return new(true, support.Position, "落地生效点");
    }

    private static bool TraceValid(SweepHit hit) => float.IsFinite(hit.Fraction) && hit.Fraction is >= 0 and <= 1 &&
        Geometry.Finite(hit.Position) && Geometry.Finite(hit.Normal);
}

/// <summary>Provisional drawing extents, never success criteria or calibrated effect limits.</summary>
public static class EffectReference
{
    public static bool HasAreaPreview(GrenadeKind kind)=>kind is not (GrenadeKind.Flash or GrenadeKind.Decoy);
    public static float Radius(GrenadeKind kind) => kind switch
    {
        GrenadeKind.Smoke => 144,
        GrenadeKind.Molotov => 180,
        GrenadeKind.Incendiary => 150,
        GrenadeKind.HighExplosive => 350,
        GrenadeKind.Flash => 0,
        GrenadeKind.Decoy => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static string Label(GrenadeKind kind) => kind switch
    {
        GrenadeKind.Smoke => "灰色虚线：烟雾体积参考，非允许落点范围；边缘未校准",
        GrenadeKind.Molotov or GrenadeKind.Incendiary => "橙色短线：扩散参考；绿色十字：实测火格，不保证烧满",
        GrenadeKind.HighExplosive => "菜单重选目标变为手雷，定位菜单开关爆点预览与假人掉血粗估；非实测伤害",
        GrenadeKind.Flash => "菜单重选目标变为闪光，定位菜单开关爆点预览与假人致盲粗估；不产生实际白屏",
        GrenadeKind.Decoy => "诱饵生效点，无效果范围显示",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
