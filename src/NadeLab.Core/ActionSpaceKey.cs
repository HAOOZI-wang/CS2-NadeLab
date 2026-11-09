namespace NadeLab.Core;

// Only inputs that alter the player hull sweep or its support check belong in
// this key. Grenade strength and aim pitch do not alter player body clearance.
public readonly record struct ActionSpaceKey(bool Crouched,bool SupportAtStart,int Yaw,float Height,float Offset)
{
    public static ActionSpaceKey For(Aim aim)=>new(aim.EffectiveAction==ThrowAction.Crouch,aim.Airborne,
        aim.ForwardOffset==0 ? 0 : (int)MathF.Round(Geometry.NormalizeYaw(aim.Yaw)*100),aim.ReleaseHeight,aim.ForwardOffset);
}
