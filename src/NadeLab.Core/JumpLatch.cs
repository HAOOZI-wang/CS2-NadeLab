namespace NadeLab.Core;

public static class JumpLatch
{
    public const float Seconds = .1f;
    // Current CS2 standing jump applies a half 128-Hz gravity step before the
    // latch. Verified against the four identical local right-jump launches.
    // Source and measured limits: docs/failure-review-20261007.md.
    public static float TakeoffSpeed(PhysicsProfile p) => p.JumpImpulse-p.PlayerGravity/256;
    public static float Height(PhysicsProfile p) => TakeoffSpeed(p)*Seconds-.5f*p.PlayerGravity*Seconds*Seconds;
    public static float VerticalSpeed(PhysicsProfile p) => TakeoffSpeed(p)-p.PlayerGravity*Seconds;
}
