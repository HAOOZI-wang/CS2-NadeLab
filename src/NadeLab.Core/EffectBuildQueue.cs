namespace NadeLab.Core;

/// <summary>One in-progress geometry build and one latest replacement; movement cannot restart active work.</summary>
public sealed class EffectBuildQueue<T>
{
    public readonly record struct Request(long Version, T Value);
    public long LatestVersion { get; private set; }
    public Request? Active { get; private set; }
    public Request? Pending { get; private set; }

    public long Enqueue(T value)
    {
        var request = new Request(++LatestVersion, value);
        if (Active is null) Active = request;
        else Pending = request;
        return request.Version;
    }

    public Request Complete()
    {
        var completed = Active ?? throw new InvalidOperationException("No effect build is active.");
        Active = Pending;
        Pending = null;
        return completed;
    }

    public void Clear()
    {
        LatestVersion++;
        Active = Pending = null;
    }
}

public static class EffectDrawRules
{
    // Native traces use 1 for a complete unobstructed segment. A near-end wall
    // is still a wall: a percentage tolerance grows with the ray's length.
    public static bool RayClear(bool allSolid, bool didHit, float fraction) =>
        !allSolid && !didHit && float.IsFinite(fraction) && fraction == 1;

    public static bool Current(long appliedVersion, long requiredVersion) =>
        requiredVersion > 0 && appliedVersion == requiredVersion;
}
