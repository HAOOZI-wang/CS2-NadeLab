using System.Numerics;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using NadeLab.Core;
using CsVector = CounterStrikeSharp.API.Modules.Utils.Vector;
using CsTrace = CounterStrikeSharp.API.Modules.Utils.Trace;

namespace NadeLab.Plugin;

// Only instantiate/use on the CS2 game thread. Never run this in Task.Run.
internal sealed class NativeWorld(CCSPlayerPawn? ignoredPawn) : ICollisionWorld
{
    public long TraceCount { get; private set; }
    public long HitCount { get; private set; }
    public long SolidStartCount { get; private set; }
    // Reuse native input buffers: a search can issue hundreds of thousands of traces.
    private readonly CsVector startBuffer = new(), endBuffer = new(), mins = new(), maxs = new();
    private static readonly TraceOptions Options = new()
    {
        InteractsAs = Contents.CsgoThrownGrenade,
        InteractsWith = Contents.Solid | Contents.Window | Contents.PassBullets | Contents.PhysicsProp |
                        Contents.CsgoGrenadeClip | Contents.Debris
    };
    public SweepHit Sweep(Vector3 start, Vector3 end, float halfSize)
    {
        startBuffer.X = start.X; startBuffer.Y = start.Y; startBuffer.Z = start.Z;
        endBuffer.X = end.X; endBuffer.Y = end.Y; endBuffer.Z = end.Z;
        mins.X = mins.Y = mins.Z = -halfSize; maxs.X = maxs.Y = maxs.Z = halfSize;
        var result = CsTrace.TraceHullShape(startBuffer,endBuffer,mins,maxs,ignoredPawn,Options);
        TraceCount++;
        if (result.DidHit()) HitCount++;
        if (result.IsAllSolid) SolidStartCount++;
        return new(result.Fraction, FromNative(result.EndPos), FromNative(result.Normal), result.IsAllSolid);
    }
    public static Vector3 FromNative(CsVector v) => new(v.X,v.Y,v.Z);
    public static CsVector ToNative(Vector3 v) => new(v.X,v.Y,v.Z);
}
