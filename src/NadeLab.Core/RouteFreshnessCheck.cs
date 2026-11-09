using System.Numerics;

namespace NadeLab.Core;

public sealed class RouteFreshnessCheck(ICollisionWorld world,PhysicsProfile physics,ThrowStart start,Candidate candidate,Target? target)
{
    private readonly SmokeSimulation simulation=new(world,physics,start,candidate.Aim,false);
    public bool Done {get;private set;}
    public bool Accepted {get;private set;}
    public static bool SameFlight(Trajectory a,Trajectory b)=>a.Status==b.Status && a.WallBounces==b.WallBounces &&
        a.Bounces==b.Bounces && a.CollisionRoute==b.CollisionRoute && Vector3.Distance(a.Endpoint,b.Endpoint)<=1;
    // One simulator step per call. The native adapter owns the time budget.
    public bool Advance()
    {
        if(Done) return false;
        if(!simulation.Done) {simulation.Step();return true;}
        var trajectory=simulation.Result();
        Accepted=physics.Successful(trajectory.Status) && SameFlight(trajectory,candidate.Trajectory) &&
            (target is not { } region || region.Accepts(world,trajectory.Endpoint));
        Done=true;return false;
    }
}
