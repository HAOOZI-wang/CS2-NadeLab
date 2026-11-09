using System.Numerics;

namespace NadeLab.Core;

// Incremental: Step() does bounded work; native collision callers must stay on the game thread.
public sealed class SmokeSimulation
{
    private readonly ICollisionWorld world;
    private readonly PhysicsProfile profile;
    private readonly List<Vector3>? points;
    private Vector3 position, velocity;
    private float time;
    private int bounces, wallBounces, steps;
    private int collisionRoute,routeContacts;
    private void RememberContact(Vector3 point,Vector3 normal)
    {
        // Coarse first-contact identity only for keeping search branches alive.
        // No extra trace, no polyline allocation, no effect on physics/scoring.
        if(normal.Z>=.7f || routeContacts++>=2) return;
        unchecked
        {
            if(collisionRoute==0) collisionRoute=17;
            for(int axis=0;axis<3;axis++)
            {
                collisionRoute=collisionRoute*31+(int)MathF.Round(normal[axis]*4);
                collisionRoute=collisionRoute*31+(int)MathF.Floor(point[axis]/128);
            }
        }
    }
    private bool sleeping;
    public SimulationStatus Status { get; private set; } = SimulationStatus.Flying;
    public bool Done => Status != SimulationStatus.Flying;
    public Vector3 Position => position;
    public Vector3 Velocity => velocity;

    public SmokeSimulation(ICollisionWorld world,PhysicsProfile profile,Vector3 source,Vector3 initialVelocity,bool recordPoints=true)
    {
        profile.Validate();
        if(!Geometry.Finite(source) || !Geometry.Finite(initialVelocity)) throw new ArgumentException("Invalid observed launch.");
        this.world=world; this.profile=profile; position=source; velocity=initialVelocity;
        points=recordPoints ? [source] : null;
    }

    public SmokeSimulation(ICollisionWorld world, PhysicsProfile profile, ThrowStart start, Aim aim, bool recordPoints = true)
    {
        profile.Validate();
        if (!Geometry.Finite(start.Feet) || !float.IsFinite(start.EyeHeight) || start.EyeHeight is < 40 or > 80 ||
            !float.IsFinite(aim.Pitch) || aim.Pitch is < -89 or > 89 || !float.IsFinite(aim.Yaw) || !Enum.IsDefined(aim.Mode) ||
            !float.IsFinite(aim.ForwardSpeed) || aim.ForwardSpeed is <0 or >300 || !Enum.IsDefined(aim.Action) ||
            !float.IsFinite(aim.ReleaseHeight) || aim.ReleaseHeight is <0 or >100 ||
            !float.IsFinite(aim.VerticalSpeed) || aim.VerticalSpeed is <-600 or >600 ||
            !float.IsFinite(aim.ForwardOffset) || aim.ForwardOffset is <0 or >100)
            throw new ArgumentException("Invalid standing throw.");
        this.world = world;
        this.profile = profile;
        start = ThrowExecution.Resolve(start,aim,profile).SimulationStart;
        points = recordPoints ? [] : null;
        float adjustedPitch = aim.Pitch - (90 - MathF.Abs(aim.Pitch)) * (10f / 90);
        var forward = Geometry.Direction(adjustedPitch, aim.Yaw);
        var eye = aim.ReleaseFeet(start) + new Vector3(0, 0, aim.EyeHeight(start) + aim.Strength * 12 - 12);
        var launch = world.Sweep(eye, eye + forward * 22, profile.HullHalfSize);
        position = launch.Position - forward * 6;
        velocity = forward * (profile.ThrowSpeed * (.3f + .7f * aim.Strength)) + aim.PlayerVelocity * profile.PlayerVelocityScale;
        if (launch.StartSolid) Status = SimulationStatus.Blocked;
        if (launch.Hit && launch.Normal.Z < .7f) {wallBounces++;RememberContact(launch.Position,launch.Normal);}
        points?.Add(position);
    }

    public void Step()
    {
        if (Done) return;
        float dt = profile.Kind.Timed() || profile.Kind.Fire() ? MathF.Min(profile.StepSeconds,MathF.Max(0,profile.FuseSeconds-time)) : profile.StepSeconds;
        if (!sleeping)
        {
            var delta = velocity * dt - new Vector3(0, 0, .5f * profile.Gravity * dt * dt);
            velocity.Z -= profile.Gravity * dt;
            float remaining = dt;
            for (int contact = 0; contact < 4 && remaining > .00001f; contact++)
            {
                var hit = world.Sweep(position, position + delta, profile.HullHalfSize);
                if (hit.StartSolid || !Geometry.Finite(hit.Position) || !float.IsFinite(hit.Fraction))
                { Status = SimulationStatus.Blocked; break; }
                position = hit.Position;
                if (!hit.Hit) break;
                if (!Geometry.Finite(hit.Normal) || hit.Normal.LengthSquared() < .5f)
                { Status = SimulationStatus.Blocked; break; }
                var normal = Vector3.Normalize(hit.Normal);
                bounces++;
                if (normal.Z < .7f) wallBounces++;
                RememberContact(position,normal);
                points?.Add(position); // Preserve the actual contact instead of drawing across it.
                if(profile.Kind.Fire() && normal.Z>=profile.FireGroundNormal)
                {time+=dt-remaining+remaining*Math.Clamp(hit.Fraction,0,1);Status=SimulationStatus.Ignited;return;}
                float incoming = Vector3.Dot(velocity, normal);
                if (incoming < 0)
                {
                    var tangent = velocity - incoming * normal;
                    velocity = tangent * profile.TangentialRetention - incoming * normal * profile.Restitution;
                    if (normal.Z > .7f && velocity.LengthSquared() > profile.HeavyImpactSpeed * profile.HeavyImpactSpeed)
                    {
                        float incidence=Vector3.Dot(Vector3.Normalize(velocity),normal);
                        if (incidence>.5f) velocity *= 1.5f-incidence;
                    }
                }
                position += normal * .03125f;
                if (normal.Z > .7f && velocity.Length() < profile.RestSpeed)
                { velocity = Vector3.Zero; sleeping = true; break; }
                remaining *= 1 - Math.Clamp(hit.Fraction, 0, 1);
                delta = velocity * remaining;
                if (contact == 3 && remaining > .00001f) Status = SimulationStatus.Blocked;
            }
        }
        time += dt;
        steps++;
        if (steps % 4 == 0 || Done || sleeping) points?.Add(position);
        if(!Done && profile.Kind.Timed() && time>=profile.FuseSeconds) Status=SimulationStatus.Detonated;
        else if(!Done && profile.Kind.Fire() && time>=profile.FuseSeconds) Status=SimulationStatus.Extinguished;
        else if (!Done && !profile.Kind.Timed() && !profile.Kind.Fire() && sleeping && time >= profile.MinDetonateSeconds) Status = SimulationStatus.Rested;
        else if (!Done && time >= profile.MaxSeconds) Status = SimulationStatus.TimedOut;
    }

    public Trajectory Result()
    {
        if (!Done) throw new InvalidOperationException("Simulation is still running.");
        if (points is not null && (points.Count == 0 || points[^1] != position)) points.Add(position);
        return new(position, Status, time, bounces, points?.ToArray() ?? [], wallBounces,collisionRoute);
    }

    public static Trajectory Run(ICollisionWorld world, PhysicsProfile profile, ThrowStart start, Aim aim)
    {
        var simulation = new SmokeSimulation(world, profile, start, aim);
        while (!simulation.Done) simulation.Step();
        return simulation.Result();
    }
}
