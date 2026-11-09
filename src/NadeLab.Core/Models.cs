using System.Numerics;

namespace NadeLab.Core;

public enum ThrowMode { Left, Both, Right }
public enum ThrowAction { Standing, Run, Walk, Step, Jump, RunJump, Crouch, StepJump, AirForwardJump }
public enum SimulationStatus { Flying, Rested, TimedOut, Blocked, Detonated, Ignited, Extinguished }
public readonly record struct Aim(float Pitch, float Yaw, ThrowMode Mode, float ForwardSpeed = 0,
    ThrowAction Action = ThrowAction.Standing, float ReleaseHeight = 0, float VerticalSpeed = 0, float ForwardOffset = 0)
{
    public float Strength => Mode switch { ThrowMode.Left => 1, ThrowMode.Both => .5f, _ => 0 };
    public ThrowAction EffectiveAction => Action==ThrowAction.Standing && ForwardSpeed>0 ? ThrowAction.Run : Action;
    public bool Airborne => EffectiveAction is ThrowAction.Jump or ThrowAction.RunJump or ThrowAction.StepJump or ThrowAction.AirForwardJump;
    public bool NeedsRunUp => EffectiveAction is ThrowAction.Run or ThrowAction.Walk or ThrowAction.RunJump;
    public string Instruction
    {
        get
        {
            string button=Mode switch {ThrowMode.Left=>"左键",ThrowMode.Both=>"双键",_=>"右键"};
            return EffectiveAction switch
            {
                ThrowAction.Run=>$"W+{button} {ForwardSpeed:F0}u/s",
                ThrowAction.Walk=>$"W+Shift+{button} {ForwardSpeed:F0}u/s",
                ThrowAction.Step=>$"W一步+{button} {ForwardSpeed:F0}u/s",
                ThrowAction.Jump=>$"跳投+{button} 高{ReleaseHeight:F0}u",
                ThrowAction.RunJump=>$"{ThrowInstructions.RunUpKeys(this)}跳投+{button} {ForwardSpeed:F0}u/s",
                ThrowAction.StepJump=>$"W起步跳投+{button} {ForwardSpeed:F0}u/s（待实投）",
                ThrowAction.AirForwardJump=>$"原地前推跳投+{button}（轻点W）",
                ThrowAction.Crouch=>$"蹲投+{button}",_=>$"站投+{button}"
            };
        }
    }
    public Vector3 PlayerVelocity => Geometry.Direction(0,Yaw) * ForwardSpeed + new Vector3(0,0,VerticalSpeed);
    public Vector3 ReleaseFeet(ThrowStart start) => start.Feet+Geometry.Direction(0,Yaw)*ForwardOffset+new Vector3(0,0,ReleaseHeight);
    public float EyeHeight(ThrowStart start) => EffectiveAction==ThrowAction.Crouch ? 46 : Airborne ? 64 : start.EyeHeight;
}

public enum ThrowOrigin { Release, Preparation }
// Missing origin fields in old records retain the original release-reference contract.
public readonly record struct ThrowStart(Vector3 Feet, float EyeHeight = 64,
    ThrowOrigin Origin = ThrowOrigin.Release, float RunUpDistance = 100);
public readonly record struct Target(Vector3 Position, float Radius = 32,Vector3? AreaHalfSize=null,string? Label=null,
    IReadOnlyList<LandingRegion>? ExtraRegions=null,IReadOnlyList<LandingRegion>? Exclusions=null,SmokeObjective? Objective=null,bool Airspace=false)
{
    public bool IsArea => AreaHalfSize.HasValue;
    public IEnumerable<LandingRegion> AllowedRegions()
    {
        if(AreaHalfSize is { } size) yield return new(Position,size);
        foreach(var region in ExtraRegions ?? []) yield return region;
    }
    public Target Snapshot()=>this with {ExtraRegions=ExtraRegions?.ToArray(),Exclusions=Exclusions?.ToArray()};
    // Valid targets have at most two extra/excluded regions. Compare the small
    // multisets directly: sorting both sides for every library entry allocated
    // megabytes on the game thread when a larger result batch was published.
    private static bool SameRegions(IReadOnlyList<LandingRegion>? left,IReadOnlyList<LandingRegion>? right)
    {
        int count=left?.Count ?? 0;if(count!=(right?.Count ?? 0)) return false;
        for(int i=0;i<count;i++)
        {
            var region=left![i];bool counted=false;
            for(int j=0;j<i;j++) if(left[j]==region) {counted=true;break;}
            if(counted) continue;
            int a=0,b=0;
            for(int j=0;j<count;j++) {if(left[j]==region)a++;if(right![j]==region)b++;}
            if(a!=b) return false;
        }
        return true;
    }
    public bool SameDefinition(Target other)=>Position==other.Position && Radius==other.Radius && AreaHalfSize==other.AreaHalfSize &&
        Objective==other.Objective && Airspace==other.Airspace && SameRegions(ExtraRegions,other.ExtraRegions) && SameRegions(Exclusions,other.Exclusions);
    // Zero at the region boundary, positive toward its interior. Not a smoke
    // coverage probability: it only measures clearance from our landing bounds.
    public float InteriorMargin(Vector3 position)
    {
        if(AreaHalfSize is not { } size) return 1-Vector3.Distance(position,Position)/Radius;
        float margin=AllowedRegions().Max(r=>r.Margin(position));
        foreach(var excluded in Exclusions ?? []) margin=MathF.Min(margin,-excluded.Margin(position));
        return margin;
    }
    public bool Contains(Vector3 position)
    {
        if(!Geometry.Finite(position)) return false;
        if(AreaHalfSize is not { } size) return Vector3.Distance(position, Position) <= Radius;
        return AllowedRegions().Any(r=>r.Contains(position)) && !(Exclusions?.Any(r=>r.Contains(position)) ?? false);
    }
    // An area search measures the distance to the acceptable region rather than
    // forcing every seed toward its center. Error remains the center distance in telemetry.
    public float MissDistance(Vector3 position)
    {
        if(AreaHalfSize is not { } size) return MathF.Max(0,Vector3.Distance(position,Position)-Radius);
        float miss=AllowedRegions().Min(r=>r.Distance(position));
        foreach(var excluded in Exclusions ?? [])
            if(excluded.Contains(position)) miss=MathF.Max(miss,.01f+excluded.Margin(position)*MathF.Min(excluded.HalfSize.X,MathF.Min(excluded.HalfSize.Y,excluded.HalfSize.Z)));
        return miss;
    }
    public bool Accepts(ICollisionWorld world,Vector3 position)
    {
        if(!Contains(position)) return false;
        if(!IsArea) return true;
        // Start near the grenade so low shelves don't reject valid centres.
        // Higher links may pass floor irregularities, but both vertical legs
        // must be clear: never lift the test through a ceiling or solid.
        foreach(var region in AllowedRegions())
        {
            if(!region.Contains(position)) continue;
            if(Airspace)
            {if(!world.Sweep(position,region.Center,.1f).Hit) return true;continue;}
            var from=position+Vector3.UnitZ*.25f;
            var to=region.Center+Vector3.UnitZ*.25f;
            if(!world.Sweep(from,to,.1f).Hit) return true;
            ReadOnlySpan<float> lifts=[8,16,32,64];
            foreach(float height in lifts)
            {
                var a=position+Vector3.UnitZ*height;var b=region.Center+Vector3.UnitZ*height;
                if(!world.Sweep(from,a,.1f).Hit && !world.Sweep(to,b,.1f).Hit && !world.Sweep(a,b,.1f).Hit) return true;
            }
        }
        return false;
    }
    public void Validate()
    {
        if(!Geometry.Finite(Position) || !float.IsFinite(Radius) || Radius is <1 or >256 ||
            (AreaHalfSize is { } size && (!Geometry.Finite(size) || size.X is <1 or >256 || size.Y is <1 or >256 || size.Z is <1 or >64)))
            throw new ArgumentException("Invalid target.");
        if((ExtraRegions?.Count ?? 0)>2 || (Exclusions?.Count ?? 0)>2 ||
            (!IsArea && ((ExtraRegions?.Count ?? 0)>0 || (Exclusions?.Count ?? 0)>0))) throw new ArgumentException("Invalid region count.");
        foreach(var r in (ExtraRegions ?? []).Concat(Exclusions ?? []))
        {r.Validate();if(Vector3.Distance(r.Center,Position)>1024) throw new ArgumentException("Regions must belong to the same local objective.");}
        Objective?.Validate();
        if(Objective is { } objective && Vector3.Distance(objective.Center,Position)>1024) throw new ArgumentException("Objective too far from landing regions.");
    }
}
public readonly record struct SweepHit(float Fraction, Vector3 Position, Vector3 Normal, bool StartSolid = false)
{
    public bool Hit => Fraction < 1 || StartSolid;
    public static SweepHit Miss(Vector3 end) => new(1, end, Vector3.Zero);
}
public interface ICollisionWorld
{
    SweepHit Sweep(Vector3 start, Vector3 end, float halfSize);
}

// Partial Mirage measurements inform speed/gravity and tangential retention.
// Hull shape, surface-specific friction, breakables and timing still need held-out validation.
public sealed record PhysicsProfile
{
    public GrenadeKind Kind { get; init; }
    public float FuseSeconds { get; init; } = 1.5f;
    public float FireGroundNormal { get; init; } = .8660254f;
    public string Id { get; init; } = "mirage-partial-v4-jump-latch-unvalidated";
    public bool Calibrated { get; init; }
    public float Gravity { get; init; } = 320;
    public float ThrowSpeed { get; init; } = 675;
    public float Restitution { get; init; } = .45f;
    public float TangentialRetention { get; init; } = .45f;
    public float RestSpeed { get; init; } = 20;
    public float StepSeconds { get; init; } = 1f / 128;
    public float MinDetonateSeconds { get; init; } = 1.5f;
    public float MaxSeconds { get; init; } = 12;
    public float HullHalfSize { get; init; } = 2;
    // Provisional inheritance; moving captures are required before calibration.
    public float PlayerVelocityScale { get; init; } = 1.25f;
    // Heavy floor impacts in the captured Mirage throws lose additional energy.
    public float HeavyImpactSpeed { get; init; } = 330;
    public float ReleaseDelaySeconds { get; init; } = .1f;
    public float PlayerGravity { get; init; } = 800;
    public float JumpImpulse { get; init; } = 301.99338f;
    public float WalkSpeedRatio { get; init; } = .52f;
    public void Validate()
    {
        if (!Enum.IsDefined(Kind) || !float.IsFinite(FuseSeconds) || FuseSeconds is <.1f or >10 ||
            !float.IsFinite(FireGroundNormal) || FireGroundNormal is <0 or >1 ||
            string.IsNullOrWhiteSpace(Id) || !float.IsFinite(Gravity) || Gravity <= 0 || Gravity > 2000 ||
            !float.IsFinite(ThrowSpeed) || ThrowSpeed <= 0 || ThrowSpeed > 2000 ||
            !float.IsFinite(Restitution) || Restitution is < 0 or > 1 ||
            !float.IsFinite(TangentialRetention) || TangentialRetention is < 0 or > 1 ||
            !float.IsFinite(StepSeconds) || StepSeconds is < .001f or > .05f ||
            !float.IsFinite(RestSpeed) || RestSpeed <= 0 || RestSpeed > 100 ||
            !float.IsFinite(MaxSeconds) || MaxSeconds is < 1 or > 30 ||
            !float.IsFinite(MinDetonateSeconds) || MinDetonateSeconds < 0 || MinDetonateSeconds > MaxSeconds ||
            !float.IsFinite(HullHalfSize) || HullHalfSize is < .1f or > 8 ||
            !float.IsFinite(PlayerVelocityScale) || PlayerVelocityScale is <0 or >2 ||
            !float.IsFinite(HeavyImpactSpeed) || HeavyImpactSpeed is <100 or >1000 ||
            !float.IsFinite(ReleaseDelaySeconds) || ReleaseDelaySeconds is <0 or >.5f ||
            !float.IsFinite(PlayerGravity) || PlayerGravity is <100 or >2000 ||
            !float.IsFinite(JumpImpulse) || JumpImpulse is <100 or >600 ||
            !float.IsFinite(WalkSpeedRatio) || WalkSpeedRatio is <.2f or >1)
            throw new ArgumentException("Invalid physics profile.");
    }
}

public sealed record Trajectory(Vector3 Endpoint, SimulationStatus Status, float Time, int Bounces, IReadOnlyList<Vector3> Points,
    int WallBounces = 0,int CollisionRoute = 0);
public sealed record Candidate(Aim Aim, Trajectory Trajectory, float Error, int RobustHits = 0, int RobustTrials = 0, Vector3? RunUpFeet = null,
    int ExecutionHits = 0, int ExecutionTrials = 0, int ModelHits = 0, int ModelTrials = 0,
    int SourceStressHits = 0, int SourceStressTrials = 0,IReadOnlyList<CheckScore>? Checks=null,ReleaseWindow? ReleaseWindow=null)
{
    public float RobustFraction => RobustTrials == 0 ? 0 : (float)RobustHits / RobustTrials;
    public float ExecutionReliability => ExecutionTrials==0 ? RobustFraction : MathF.Min(RobustFraction,(float)ExecutionHits/ExecutionTrials);
    public float Reliability => MathF.Min(ExecutionReliability,
        ModelTrials==0 ? 1 : (float)ModelHits/ModelTrials);
}
public sealed record SearchOptions
{
    public float PitchStep { get; init; } = 10;
    public float YawStep { get; init; } = 12;
    public float YawSpan { get; init; } = 60;
    public int SeedCount { get; init; } = 8;
    public int ResultCount { get; init; } = SearchResultCapacity.Default;
    public int MaxCandidates { get; init; } = 2400;
    public bool GlobalFallback { get; init; } = true;
    public bool Exhaustive { get; init; }
    public bool IncludeForward { get; init; }
    public bool IncludeActions { get; init; }
    public float ForwardSpeed { get; init; } = 250;
    // Read the live air wish-speed cap in the plugin; zero disables this family.
    public float AirForwardSpeed { get; init; } = 30;
    public Aim? PreferredView { get; init; }
    // Hints only. Re-evaluate from this request's origin, then run every normal
    // robustness/publication check; never import a previous success score.
    public IReadOnlyList<Aim>? SeedAims { get; init; }
    public void Validate()
    {
        if (!float.IsFinite(PitchStep) || PitchStep is < 1 or > 30 ||
            !float.IsFinite(YawStep) || YawStep is < 1 or > 30 ||
            !float.IsFinite(YawSpan) || YawSpan is < 0 or > 180 ||
            SeedCount is < 1 or > 20 || ResultCount is < 1 or > SearchResultCapacity.Maximum || MaxCandidates is < 1 or > 50000 ||
            !float.IsFinite(ForwardSpeed) || ForwardSpeed is <100 or >300 ||
            !float.IsFinite(AirForwardSpeed) || AirForwardSpeed is <0 or >60 ||
            (SeedAims is {Count:>12}) ||
            (SeedAims?.Any(a=>!float.IsFinite(a.Pitch) || a.Pitch is <-89 or >89 || !float.IsFinite(a.Yaw) ||
                !Enum.IsDefined(a.Mode) || !Enum.IsDefined(a.Action) || !float.IsFinite(a.ForwardSpeed) || a.ForwardSpeed is <0 or >300 ||
                !float.IsFinite(a.ReleaseHeight) || a.ReleaseHeight is <0 or >100 || !float.IsFinite(a.VerticalSpeed) || a.VerticalSpeed is <-600 or >600 ||
                !float.IsFinite(a.ForwardOffset) || a.ForwardOffset is <0 or >100)==true) ||
            (PreferredView is { } view && (!float.IsFinite(view.Pitch) || !float.IsFinite(view.Yaw) || view.Pitch is <-89 or >89)))
            throw new ArgumentException("Invalid search options.");
    }
}
public sealed record FamilyCoverage(ThrowAction Action,ThrowMode Mode,int Attempted,int Evaluated,int Hits,int Reviewed);
public sealed record SearchResult(string PhysicsId, bool Calibrated, int Evaluated, bool Truncated,
    IReadOnlyList<Candidate> Matches, IReadOnlyList<Candidate> Nearest,
    IReadOnlyList<FamilyCoverage>? Coverage=null,int ResumePasses=0,IReadOnlyList<Candidate>? Experimental=null,int StabilityProbes=0,SearchObstacles? Obstacles=null,IReadOnlyList<RouteCoverage>? Routes=null,SearchEfficiency? Efficiency=null);
public sealed record SearchObstacles(int ActionSpaceRejected,int RunUpRejected,int BlockedFlights);

public static class Geometry
{
    public static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    public static float NormalizeYaw(float yaw) => ((yaw + 180) % 360 + 360) % 360 - 180;
    public static Vector3 Direction(float pitch, float yaw)
    {
        float p = pitch * MathF.PI / 180, y = yaw * MathF.PI / 180;
        return new(MathF.Cos(p) * MathF.Cos(y), MathF.Cos(p) * MathF.Sin(y), -MathF.Sin(p));
    }
}
