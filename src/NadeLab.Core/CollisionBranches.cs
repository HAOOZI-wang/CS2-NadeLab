namespace NadeLab.Core;

// Keep distinct early collision sequences rather than filling every seed slot
// with slightly different misses on the same side of a window.
public sealed class CollisionBranches
{
    private readonly Dictionary<(int Route,int Contacts,ThrowAction Action,ThrowMode Mode),Candidate> branches=[];
    public void Observe(Candidate candidate,Func<Candidate,float> score)
    {
        if(candidate.Aim.EffectiveAction is not (ThrowAction.Jump or ThrowAction.AirForwardJump) || candidate.Trajectory.WallBounces==0) return;
        var key=(candidate.Trajectory.CollisionRoute,Math.Min(3,candidate.Trajectory.WallBounces),candidate.Aim.EffectiveAction,candidate.Aim.Mode);
        if(!branches.TryGetValue(key,out var previous) || score(candidate)<score(previous)) branches[key]=candidate;
        if(branches.Count>24)
        {
            // Retain representatives across one and multiple wall contacts too.
            var crowded=branches.GroupBy(p=>(Math.Min(3,p.Value.Trajectory.WallBounces),p.Value.Aim.EffectiveAction)).OrderByDescending(g=>g.Count()).First();
            var worst=crowded.OrderByDescending(p=>score(p.Value)).First().Key;
            branches.Remove(worst);
        }
    }
    public Candidate[] Seeds(Func<Candidate,float> score,int count=8)=>branches.Values
        .GroupBy(c=>(Math.Min(3,c.Trajectory.WallBounces),c.Aim.EffectiveAction)).SelectMany(g=>g.OrderBy(score).Select((c,i)=>(c,i)))
        .OrderBy(x=>x.i).ThenBy(x=>score(x.c)).Take(count).Select(x=>x.c).ToArray();
}
