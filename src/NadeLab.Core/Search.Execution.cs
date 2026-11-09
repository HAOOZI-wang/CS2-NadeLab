namespace NadeLab.Core;

public sealed partial class LineupSearch
{
    private Func<ThrowStart,Aim,IEnumerable<bool?>>? executionSpace;
    private SmokeSimulation? sampleSimulation;
    private System.Numerics.Vector3 sampleSource,sampleVelocity;

    private IEnumerable<int> SimulateChecked(ThrowStart requested,Aim aim,bool recordPoints=true)
    {
        sampleSimulation=null;
        if(requested.Origin==ThrowOrigin.Preparation && aim.NeedsRunUp || aim.EffectiveAction==ThrowAction.AirForwardJump)
        {
            if(executionSpace is null) yield break;
            bool allowed=false;
            foreach(var check in executionSpace(requested,aim))
            {if(check is { } complete) allowed=complete;yield return 0;}
            if(!allowed) yield break;
        }
        var simulation=new SmokeSimulation(world,physics,requested,aim,recordPoints);
        sampleSource=simulation.Position;sampleVelocity=simulation.Velocity;
        yield return 0;
        while(!simulation.Done) {simulation.Step();yield return 0;}
        sampleSimulation=simulation;
    }
}
