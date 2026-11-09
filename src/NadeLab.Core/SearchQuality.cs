namespace NadeLab.Core;

// Policy over existing measured checks. These are sampled perturbations, not
// calibrated human success probabilities; source stress stays a separate label.
public static class SearchQuality
{
    public static float ExecutionScore(Candidate candidate)
    {
        float score=candidate.Reliability;
        foreach(var check in candidate.Checks ?? [])
            if(check.Trials>0 && check.Group is "angle" or "coupled" or "position" or "eye" or "motion-speed" or "motion-timing" or "latch" or "surface")
                score=MathF.Min(score,(float)check.Hits/check.Trials);
        return score;
    }
    public static bool Recommended(Candidate candidate)=>candidate.RobustTrials>0 && ExecutionScore(candidate)>=.75f;
    public static bool SourceUncertain(Candidate candidate)=>candidate.SourceStressTrials>0 && candidate.SourceStressHits<candidate.SourceStressTrials;
    public static bool EasyChoice(Candidate candidate,Target target)=>Recommended(candidate) &&
        !SceneAdvisor.HasSteepUpwardAim(candidate.Aim) && SceneAdvisor.ActionCost(candidate.Aim)<=.7f &&
        (!candidate.Aim.NeedsRunUp || candidate.ReleaseWindow is {Duration:>=.1f}) &&
        (!target.IsArea || target.InteriorMargin(candidate.Trajectory.Endpoint)>.3f);
    private static bool Distinct(Candidate a,Candidate b)=>a.Aim.EffectiveAction!=b.Aim.EffectiveAction || a.Aim.Mode!=b.Aim.Mode ||
        a.Trajectory.WallBounces!=b.Trajectory.WallBounces || SceneAdvisor.ViewTurn(a.Aim,b.Aim)>=8;
    public static bool EnoughChoices(IEnumerable<Candidate> candidates,Target target,int requested)
    {
        var useful=new List<Candidate>();
        foreach(var candidate in candidates)
            if(EasyChoice(candidate,target) && useful.All(other=>Distinct(candidate,other)))
            {
                useful.Add(candidate);
                if(useful.Count>=Math.Min(2,requested)) return true;
            }
        return false;
    }
}
