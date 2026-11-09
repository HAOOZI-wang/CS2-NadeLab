using System.Numerics;

namespace NadeLab.Core;

// Only used by the standalone demo/tests. The server plugin uses native map hull traces.
public readonly record struct Box(Vector3 Min, Vector3 Max);
public sealed class AnalyticWorld(float groundZ = 0, params Box[] boxes) : ICollisionWorld
{
    public SweepHit Sweep(Vector3 start, Vector3 end, float halfSize)
    {
        float floor = groundZ + halfSize;
        if (start.Z < floor - .001f) return new(0, start, Vector3.UnitZ, true);
        var nearest = SweepHit.Miss(end);
        if (end.Z < floor && start.Z >= floor)
        {
            float f = (start.Z - floor) / (start.Z - end.Z);
            nearest = new(f, Vector3.Lerp(start, end, f), Vector3.UnitZ);
        }
        foreach (var box in boxes)
        {
            var min = box.Min - new Vector3(halfSize);
            var max = box.Max + new Vector3(halfSize);
            if (start.X > min.X && start.X < max.X && start.Y > min.Y && start.Y < max.Y && start.Z > min.Z && start.Z < max.Z)
                return new(0, start, Vector3.Zero, true);
            var delta = end - start;
            float enter = 0, leave = 1;
            Vector3 normal = Vector3.Zero;
            bool missed = false;
            for (int axis = 0; axis < 3; axis++)
            {
                float s = start[axis], d = delta[axis];
                if (MathF.Abs(d) < 1e-8f) { if (s < min[axis] || s > max[axis]) missed = true; continue; }
                float t1 = (min[axis] - s) / d, t2 = (max[axis] - s) / d;
                var n = Vector3.Zero; n[axis] = d > 0 ? -1 : 1;
                if (t1 > t2) (t1, t2) = (t2, t1);
                if (t1 >= enter) { enter = t1; normal = n; }
                leave = MathF.Min(leave, t2);
                if (enter > leave) missed = true;
            }
            if (!missed && normal != Vector3.Zero && enter >= 0 && enter < nearest.Fraction)
                nearest = new(enter, Vector3.Lerp(start, end, enter), normal);
        }
        return nearest;
    }
}
