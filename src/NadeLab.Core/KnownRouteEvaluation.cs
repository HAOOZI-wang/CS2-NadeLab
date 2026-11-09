namespace NadeLab.Core;

public sealed class KnownRouteEvaluation
{
    private readonly ICollisionWorld world;
    private readonly PhysicsProfile physics;
    private readonly SmokeSimulation simulation;
    public ThrowStart Start {get;}
    public Aim Aim {get;}
    public Target Target {get;}
    public bool Done=>Cancelled || Result is not null;
    public bool Cancelled {get;private set;}
    public Trajectory? Result {get;private set;}
    public bool? TargetHit {get;private set;}
    public KnownRouteEvaluation(ICollisionWorld world,PhysicsProfile physics,ThrowStart start,Aim aim,Target target)
    {
        target.Validate();Start=start;Aim=aim;Target=target.Snapshot();this.world=world;this.physics=physics;
        simulation=new(world,physics,start,aim,recordPoints:false);
    }
    public void Advance(int maxSteps=8,double maxMilliseconds=.5)
    {
        if(maxSteps is <1 or >128 || !double.IsFinite(maxMilliseconds) || maxMilliseconds is <=0 or >100)
            throw new ArgumentOutOfRangeException(nameof(maxSteps));
        if(Done) return;
        var clock=System.Diagnostics.Stopwatch.StartNew();
        for(int steps=0;!simulation.Done && steps<maxSteps && clock.Elapsed.TotalMilliseconds<maxMilliseconds;steps++) simulation.Step();
        if(!simulation.Done) return;
        Result=simulation.Result();
        TargetHit=physics.Successful(Result.Status) && Target.Accepts(world,Result.Endpoint);
    }
    public void Cancel() {if(!Done) Cancelled=true;}
}
