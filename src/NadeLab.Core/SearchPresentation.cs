namespace NadeLab.Core;

/// <summary>
/// Player-facing ordering of completed, collision-rechecked search results.
/// The solver's recommendation/evidence collections remain unchanged: this is
/// a view used by selection controls, never a replacement for raw telemetry.
/// </summary>
public static class SearchPresentation
{
    public const int PageSize=5;
    public static bool IsRecommended(Candidate candidate) => SearchQuality.Recommended(candidate);
    public static bool IsReviewed(Candidate candidate) => candidate.RobustTrials>0;
    public static string Label(Candidate candidate) => (!IsReviewed(candidate) ? "尚未完成复核" :
        IsRecommended(candidate) ? "优先尝试 · 待实投" : "低容错 · 待实投")+
        (SceneAdvisor.HasSteepUpwardAim(candidate.Aim) ? " · 大仰角" : "")+
        (SearchQuality.SourceUncertain(candidate) ? " · 模型待验证" : "");

    private static bool Completed(Candidate candidate) => candidate.RobustTrials>0 &&
        candidate.Trajectory.Status is SimulationStatus.Rested or SimulationStatus.Detonated or SimulationStatus.Ignited &&
        Geometry.Finite(candidate.Trajectory.Endpoint);

    /// <summary>
    /// Append all reviewed low-tolerance choices after the normal recommendations.
    /// Preserve the exact candidate references: pending selections, observations,
    /// and saved throw identities must continue to refer to the same objects.
    /// Nearest misses and unfinished simulations are deliberately never exposed.
    /// </summary>
    public static SearchResult ForPlayer(SearchResult result)
    {
        var candidates=result.Matches.Concat(result.Experimental ?? []).Where(Completed).Distinct().ToArray();
        var recommended=candidates.Where(IsRecommended);
        var fragile=candidates.Where(c=>!IsRecommended(c)).OrderByDescending(c=>c.Reliability)
            .ThenBy(c=>SceneAdvisor.ActionCost(c.Aim)).ThenBy(c=>c.Trajectory.WallBounces);
        return result with {Matches=recommended.Concat(fragile).ToArray()};
    }

    public static int RecommendedCount(SearchResult result) => result.Matches.Where(Completed).Count(IsRecommended);
    public static int LowToleranceCount(SearchResult result) => result.Matches.Concat(result.Experimental ?? [])
        .Where(Completed).Distinct().Count(c=>!IsRecommended(c));
    public static bool HasUsable(SearchResult? result) => result is not null && result.Matches.Concat(result.Experimental ?? []).Any(Completed);

    // IDs are the append-only positions of immutable candidates. A new ranking
    // or final geometry check must never retarget a prepared/in-flight throw.
    public static SearchResult MergePublished(SearchResult? current,SearchResult update)
    {
        var kept=(current?.Matches ?? []).Where(Completed).ToList();
        int recommended=kept.Count(IsRecommended),fragile=kept.Count-recommended;
        foreach(var candidate in ForPlayer(update).Matches)
        {
            if(kept.Any(old=>old.Aim==candidate.Aim && RouteFreshnessCheck.SameFlight(old.Trajectory,candidate.Trajectory))) continue;
            bool good=IsRecommended(candidate);
            if(good ? recommended>=SearchResultCapacity.Default : fragile>=SearchResultCapacity.Default) continue;
            kept.Add(candidate);if(good) recommended++;else fragile++;
        }
        return update with {Matches=Array.AsReadOnly(kept.ToArray()),Experimental=[]};
    }

    // Useful choices can move up in a newly opened list, but their original
    // stable IDs/objects never change. An already-open menu keeps its snapshot.
    public static SearchCandidatePage Page(SearchResult result,int requestedPage)
    {
        var choices=result.Matches.Select((candidate,index)=>new SearchCandidateChoice(index+1,candidate))
            .Where(choice=>Completed(choice.Candidate)).OrderByDescending(choice=>IsRecommended(choice.Candidate))
            .ThenBy(choice=>SceneAdvisor.ManualCost(choice.Candidate)).ToArray();
        int pages=Math.Max(1,(choices.Length+PageSize-1)/PageSize);
        int page=Math.Clamp(requestedPage,0,pages-1);
        return new(page,pages,choices.Length,choices.Skip(page*PageSize).Take(PageSize).ToArray());
    }
}

public sealed record SearchCandidateChoice(int Number,Candidate Candidate);
public sealed record SearchCandidatePage(int PageIndex,int PageCount,int TotalCount,IReadOnlyList<SearchCandidateChoice> Choices);
