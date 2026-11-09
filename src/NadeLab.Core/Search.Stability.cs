namespace NadeLab.Core;

public sealed partial class LineupSearch
{
    private IEnumerable<int> StageCheckpoint(bool refine=true)
    {
        if(!target.IsArea || options.Exhaustive || !Budget) yield break;
        foreach(int unit in ReviewCandidates(2)) yield return unit;
        // One shared late-stage repair allowance, not a new budget per stage.
        // The two early repairs in the heading scan retain their own bound.
        if(refine && !HasReliable && Budget && stageRepairs==0 && matches.Any(c=>!stabilitySeeds.Contains(AimKey(c.Aim))))
        {
            stageRepairs++;
            foreach(int unit in RefineReliability()) yield return unit;
        }
    }

    private IEnumerable<int> RefineReliability()
    {
        Phase="robust-refine";
        var seed=matches.Where(c=>!Recommended(c) && !stabilitySeeds.Contains(AimKey(c.Aim)))
            .OrderByDescending(c=>c.ExecutionReliability).ThenByDescending(c=>c.Reliability).FirstOrDefault();
        if(seed is null || !stabilitySeeds.Add(AimKey(seed.Aim))) yield break;
        // Exact candidate proposals bypass the nominal-distance merge in Keep.
        // Angle-boundary and movement probes are only a ranking aid. Publication still
        // requires the same full envelope, motion and surface checks as before.
        var scored=new List<(Candidate Candidate,float Score,float Worst)>();
        float Score(int angleHits,int angleTrials,int motionHits,int motionTrials) =>
            MathF.Min(angleTrials==0 ? 0 : (float)angleHits/angleTrials,motionTrials==0 ? 1 : (float)motionHits/motionTrials);
        if(seed.Checks?.FirstOrDefault(c=>c.Group=="angle") is { } baseline)
        {
            var motion=seed.Checks.Where(c=>c.Group is "motion-speed" or "motion-timing").ToArray();
            scored.Add((seed,Score(baseline.Hits,baseline.Trials,motion.Sum(c=>c.Hits),motion.Sum(c=>c.Trials)),
                MathF.Max(baseline.WorstMiss,motion.Select(c=>c.WorstMiss).DefaultIfEmpty(0).Max())));
        }
        foreach(float step in new[]{.25f,.5f})
        {
            var center=scored.OrderByDescending(c=>c.Score).ThenBy(c=>c.Worst).FirstOrDefault().Candidate ?? seed;
            (float p,float y)[] offsets=[(0,-1),(0,1),(-1,0),(1,0),(-1,-1),(1,1)];
            foreach(var (pitch,yaw) in offsets)
            {
                if(!Budget) break;
                var aim=center.Aim with {Pitch=Math.Clamp(center.Aim.Pitch+pitch*step,-89,89),Yaw=Geometry.NormalizeYaw(center.Aim.Yaw+yaw*step)};
                foreach(int unit in Evaluate(aim)) yield return unit;
                if(lastCandidate is not { } candidate || !target.Accepts(world,candidate.Trajectory.Endpoint) || rejectedByEvidence?.Invoke(aim)==true) continue;
                int angleHits=0,angleTrials=0,motionHits=0,motionTrials=0; float worst=0; bool complete=true;
                foreach(var trial in ExecutionEnvelope.Cases(start,aim).Where(c=>c.Group=="angle").Concat(ManualExecution.Cases(start,aim,physics)))
                {
                    if(!Budget) {complete=false; break;}
                    foreach(int unit in SimulateChecked(trial.Start,trial.Aim,recordPoints:false)) yield return unit;
                    var sim=sampleSimulation;
                    bool hit=sim is not null && physics.Successful(sim.Status) && target.Accepts(world,sim.Position);
                    if(trial.Group=="angle") {angleTrials++; if(hit) angleHits++;}
                    else {motionTrials++; if(hit) motionHits++;}
                    float miss=sim is not null && physics.Successful(sim.Status) ? target.MissDistance(sim.Position) : target.Radius*4;
                    worst=MathF.Max(worst,hit ? 0 : MathF.Max(.01f,miss));
                }
                if(complete) {stabilityProbes++;scored.Add((candidate,Score(angleHits,angleTrials,motionHits,motionTrials),worst));}
            }
        }
        var proposals=scored.Where(c=>!reviewed.Contains(AimKey(c.Candidate.Aim)))
            .OrderByDescending(c=>c.Score).ThenBy(c=>c.Worst).Select(c=>c.Candidate).Take(2).ToArray();
        foreach(int unit in ReviewCandidates(2,proposals)) yield return unit;
    }
}
