namespace NadeLab.Core;

public enum GrenadeKind { Smoke, Flash, HighExplosive, Molotov, Incendiary, Decoy }

public static class Grenades
{
    public static string Label(this GrenadeKind kind)=>kind switch
    {GrenadeKind.Smoke=>"烟雾弹",GrenadeKind.Flash=>"闪光弹",GrenadeKind.HighExplosive=>"高爆手雷",GrenadeKind.Molotov=>"T燃烧瓶",GrenadeKind.Incendiary=>"CT燃烧弹",GrenadeKind.Decoy=>"诱饵弹",_=>throw new ArgumentOutOfRangeException(nameof(kind))};
    public static string Weapon(this GrenadeKind kind)=>kind switch
    {GrenadeKind.Smoke=>"weapon_smokegrenade",GrenadeKind.Flash=>"weapon_flashbang",GrenadeKind.HighExplosive=>"weapon_hegrenade",GrenadeKind.Molotov=>"weapon_molotov",GrenadeKind.Incendiary=>"weapon_incgrenade",GrenadeKind.Decoy=>"weapon_decoy",_=>throw new ArgumentOutOfRangeException(nameof(kind))};
    public static string Projectile(this GrenadeKind kind)=>kind switch
    {GrenadeKind.Smoke=>"smokegrenade_projectile",GrenadeKind.Flash=>"flashbang_projectile",GrenadeKind.HighExplosive=>"hegrenade_projectile",GrenadeKind.Molotov or GrenadeKind.Incendiary=>"molotov_projectile",GrenadeKind.Decoy=>"decoy_projectile",_=>throw new ArgumentOutOfRangeException(nameof(kind))};
    public static string Slot(this GrenadeKind kind)=>kind switch
    {GrenadeKind.Smoke=>"slot8",GrenadeKind.Flash=>"slot7",GrenadeKind.HighExplosive=>"slot6",GrenadeKind.Decoy=>"slot9",_=>"slot10"};
    public static bool Fire(this GrenadeKind kind)=>kind is GrenadeKind.Molotov or GrenadeKind.Incendiary;
    public static bool Timed(this GrenadeKind kind)=>kind is GrenadeKind.Flash or GrenadeKind.HighExplosive;
    public static bool Successful(this PhysicsProfile profile,SimulationStatus status)=>profile.Kind switch
    {GrenadeKind.Flash or GrenadeKind.HighExplosive=>status==SimulationStatus.Detonated,
        GrenadeKind.Molotov or GrenadeKind.Incendiary=>status==SimulationStatus.Ignited,_=>status==SimulationStatus.Rested};
    // Bootstrap profiles are deliberately uncalibrated. Smoke measurements do
    // not establish another grenade's flight or effect model.
    public static PhysicsProfile Bootstrap(GrenadeKind kind,PhysicsProfile smoke)=>kind==GrenadeKind.Smoke ? smoke : smoke with
    {Kind=kind,Id=$"mirage-{kind.ToString().ToLowerInvariant()}-bootstrap-v1-unvalidated",Calibrated=false,
        // On local CS2 build 14190 the scheduled 1.5 s timer produced the
        // detonation event at 1.625 s in repeated engine throws. This effective
        // fuse is provisional and kept separate from the recorded engine timer.
        MinDetonateSeconds=kind==GrenadeKind.Decoy ? 2 : 1.5f,FuseSeconds=kind.Fire() ? 3.5f : 1.625f};
}
