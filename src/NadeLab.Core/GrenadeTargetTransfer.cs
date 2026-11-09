using System.Numerics;

namespace NadeLab.Core;

public enum TargetTransferKind { NoTarget, Unchanged, KeptArea, PointToArea, SpaceChanged, ComplexTarget }
public readonly record struct GrenadeTargetTransferResult(Target? Target,TargetTransferKind Kind);

public static class GrenadeTargetTransfer
{
    // Switching grenades must not silently discard hidden geometric constraints.
    // The new grenade editor supports one simple region, so complex objectives
    // require a fresh choice instead of inheriting invisible regions/exclusions.
    public static GrenadeTargetTransferResult Resolve(Target? source,GrenadeKind from,GrenadeKind to)
    {
        if(!Enum.IsDefined(from) || !Enum.IsDefined(to)) throw new ArgumentOutOfRangeException(nameof(to));
        if(source is not { } target) return new(null,TargetTransferKind.NoTarget);
        target.Validate();
        if(from==to) return new(target.Snapshot(),TargetTransferKind.Unchanged);
        if(from.Timed()!=to.Timed() || target.Airspace!=to.Timed()) return new(null,TargetTransferKind.SpaceChanged);
        if(target.Objective is not null || target.ExtraRegions is {Count:>0} || target.Exclusions is {Count:>0})
            return new(null,TargetTransferKind.ComplexTarget);
        bool point=!target.IsArea;
        var converted=target with
        {
            AreaHalfSize=target.AreaHalfSize ?? new Vector3(target.Radius,target.Radius,MathF.Min(target.Radius,to.Timed() ? 48 : 12)),
            Airspace=to.Timed(),Label=to.Timed() ? "三维爆点区域" : "地面生效区域"
        };
        converted.Validate();
        return new(converted.Snapshot(),point ? TargetTransferKind.PointToArea : TargetTransferKind.KeptArea);
    }
}
