using CounterStrikeSharp.API.Core;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private void MergeSearchResults(Session session,SearchResult update)
    {
        session.Result=SearchPresentation.MergePublished(session.Result,update);
        if(session.Selected<1 && session.Result.Matches.Count>0) session.Selected=1;
        // Existing candidate references, Arm, Pending, observations and menu
        // focus belong to the player. An append must not reset any of them.
        session.NextHudAt=0;
    }
    private void PublishLiveSearch(SearchJob current,Session session,CCSPlayerController? owner)
    {
        if(owner is null || current.PublishedCount==current.Search.PublishedCandidates.Count) return;
        current.PublishedCount=current.Search.PublishedCandidates.Count;
        if(current.SearchedStart is { } start) session.Start=start;
        MergeSearchResults(session,current.Search.PublishedResult());
    }
    private void StopPlayerSearch(CCSPlayerController player)
    {
        if(job?.Slot!=player.Slot && flashJob?.Slot!=player.Slot) return;
        CancelSearch();
        Say(player,"搜索已停止，已找到的路线保留。");
        OpenCurrentTask(player);
    }
    private void PublishFlashResults(FlashSearchJob current,Session session,FlashSearchResult update)
    {
        var kept=(current.Plan.Result?.Matches ?? []).ToList();
        foreach(var candidate in update.Matches)
            if(kept.Count<SearchResultCapacity.Default && candidate.Candidate.RobustTrials>0 && candidate.GeometryAccepted &&
                !kept.Any(old=>old.Candidate.Aim==candidate.Candidate.Aim && RouteFreshnessCheck.SameFlight(old.Candidate.Trajectory,candidate.Candidate.Trajectory))) kept.Add(candidate);
        current.Plan.Result=update with {Matches=Array.AsReadOnly(kept.ToArray())};
        if(kept.Count>0)
        {
            if(current.Plan.Trials is null) current.Plan.Trials=new(kept.Count,current.Plan.Observers);
            else current.Plan.Trials.Grow(kept.Count);
        }
        session.Result=new(update.PhysicsId,false,update.Evaluated,update.Truncated,Array.AsReadOnly(kept.Select(c=>c.Candidate).ToArray()),[]);
        if(session.Selected<1 && kept.Count>0) session.Selected=1;
        session.NextHudAt=0;
    }
}
