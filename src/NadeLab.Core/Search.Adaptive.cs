using System.Numerics;

namespace NadeLab.Core;

public sealed record SearchEfficiency(int DuplicateProposals,int CachedResults,int RefinementProposals,int RefinementMoves,int RevalidatedAngles=0);

public sealed partial class LineupSearch
{
    private int refinementProposals,refinementMoves;
    private sealed class RefinementCursor(Candidate seed,float pitchStep,float yawStep,Func<Candidate,float> score,Vector3 goal,bool inverseEnabled)
    {
        private static readonly (float P,float Y)[] directions=[(0,1),(0,-1),(1,0),(-1,0),(1,1),(1,-1),(-1,1),(-1,-1)];
        private Candidate center=seed,best=seed;
        private int direction,level,rounds;
        private readonly Candidate?[] probes=new Candidate?[4];
        private Aim? inverse;
        private readonly (float P,float Y)[] steps=[(pitchStep,yawStep),(MathF.Min(1,pitchStep),MathF.Min(1,yawStep)),(.25f,.25f),(.08f,.08f)];
        public bool Done=>level>=steps.Length;
        public Aim Next()
        {
            if(direction==directions.Length && inverse is { } proposal) return proposal;
            var d=directions[direction];var step=steps[level];
            return center.Aim with {Pitch=Math.Clamp(center.Aim.Pitch+d.P*step.P,-89,89),Yaw=Geometry.NormalizeYaw(center.Aim.Yaw+d.Y*step.Y)};
        }
        public bool Observe(Candidate? candidate)
        {
            if(direction<probes.Length) probes[direction]=candidate;
            if(candidate is not null && score(candidate)<score(best)) best=candidate;
            if(++direction<directions.Length) return false;
            // Reuse the already simulated central differences. Only extrapolate
            // within one observed contact branch, and always simulate the new
            // angle normally: a local derivative is never collision evidence.
            if(direction==directions.Length && TryInverse() is { } proposal)
            {inverse=proposal;return false;}
            inverse=null;Array.Clear(probes);
            direction=0;rounds++;
            bool improved=score(best)<score(center)-.01f;
            if(improved) center=best;
            // Move the center after improvement; shrink only on a plateau or
            // after a bounded number of moves at this scale.
            if(!improved || rounds>=3) {level++;rounds=0;}
            return improved;
        }
        private Aim? TryInverse()
        {
            // Enable only for the smoke model covered by the comparison suite.
            // Other effects retain their established refinement until validated.
            if(!inverseEnabled) return null;
            if(probes.Any(c=>c is null || c.Trajectory.CollisionRoute!=center.Trajectory.CollisionRoute ||
                c.Trajectory.WallBounces!=center.Trajectory.WallBounces || c.Trajectory.Bounces!=center.Trajectory.Bounces ||
                c.Trajectory.Status!=center.Trajectory.Status)) return null;
            var step=steps[level];
            // Pitch clipping would invalidate the symmetric denominator.
            if(center.Aim.Pitch-step.P < -89 || center.Aim.Pitch+step.P > 89) return null;
            var dp=(probes[2]!.Trajectory.Endpoint-probes[3]!.Trajectory.Endpoint)/(2*step.P);
            var dy=(probes[0]!.Trajectory.Endpoint-probes[1]!.Trajectory.Endpoint)/(2*step.Y);
            var residual=goal-center.Trajectory.Endpoint;
            float a=Vector3.Dot(dp,dp),b=Vector3.Dot(dp,dy),d=Vector3.Dot(dy,dy);
            float determinant=a*d-b*b;
            if(!float.IsFinite(determinant) || a<.01f || d<.01f || determinant<a*d*.01f) return null;
            float p=(Vector3.Dot(dp,residual)*d-Vector3.Dot(dy,residual)*b)/determinant;
            float y=(Vector3.Dot(dy,residual)*a-Vector3.Dot(dp,residual)*b)/determinant;
            if(!float.IsFinite(p) || !float.IsFinite(y)) return null;
            float scale=MathF.Max(1,MathF.Max(MathF.Abs(p)/MathF.Min(6,step.P*3),MathF.Abs(y)/MathF.Min(9,step.Y*3)));
            p/=scale;y/=scale;
            if(MathF.Abs(p)<.01f && MathF.Abs(y)<.01f) return null;
            return center.Aim with {Pitch=Math.Clamp(center.Aim.Pitch+p,-89,89),Yaw=Geometry.NormalizeYaw(center.Aim.Yaw+y)};
        }
    }
    private IEnumerable<int> AdaptiveRefine(IEnumerable<(RouteLane? Lane,Candidate Seed)> proposals,float pitchStep,float yawStep,int allocation)
    {
        int limit=Math.Min(options.MaxCandidates,Evaluated+allocation);
        int nextReview=Evaluated+24;
        var cursors=new List<(RouteLane? Lane,RefinementCursor Cursor)>();
        bool more=true;
        try
        {
            foreach(var (lane,seed) in proposals.DistinctBy(p=>AimKey(p.Seed.Aim)).ToArray())
            {
                if(!Budget || Evaluated>=limit) yield break;
                activeLane=lane;
                foreach(int unit in Evaluate(seed.Aim,true)) yield return unit;
                if(lastCandidate is { } current)
                {
                    var goal=target.IsArea ? target.AllowedRegions().OrderBy(r=>r.Distance(current.Trajectory.Endpoint)).First().Center : target.Position;
                    cursors.Add((lane,new(current,pitchStep,yawStep,Objective,goal,physics.Kind==GrenadeKind.Smoke)));
                }
                activeLane=null;
            }
            while(more && Budget && Evaluated<limit)
            {
                more=false;
                foreach(var (lane,cursor) in cursors)
                {
                    if(!Budget || Evaluated>=limit) yield break;
                    if(cursor.Done) continue;
                    more=true;activeLane=lane;
                    if(lane is not null) Phase="route-"+lane.Name;
                    refinementProposals++;
                    foreach(int unit in Evaluate(cursor.Next(),true)) yield return unit;
                    if(cursor.Observe(lastCandidate)) refinementMoves++;
                    activeLane=null;
                }
                if(target.IsArea && !options.Exhaustive && Budget && Evaluated>=nextReview && hits.Any(c=>!reviewed.Contains(AimKey(c.Aim))))
                {
                    string priorPhase=Phase;
                    foreach(int unit in ReviewCandidates(2)) yield return unit;
                    Phase=priorPhase;nextReview=Evaluated+48;
                    if(SimpleAreaHit) {seedSatisfied=true;yield break;}
                }
            }
        }
        finally {activeLane=null;}
    }
}
