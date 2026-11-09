namespace NadeLab.Core;

public sealed partial class LineupSearch
{
    private IReadOnlyList<Candidate> publishedCandidates=Array.Empty<Candidate>();
    private int? qualityContinuationLimit;
    // Immutable snapshots; publication does not stop, rewind or expose a mutable
    // finalist list. The adapter owns its stable player numbering independently.
    public IReadOnlyList<Candidate> PublishedCandidates=>publishedCandidates;
    private void PublishReviewed(Candidate candidate)
    {
        if(publishedCandidates.Count>=options.ResultCount*2 || publishedCandidates.Any(c=>AimKey(c.Aim)==AimKey(candidate.Aim))) return;
        publishedCandidates=Array.AsReadOnly(publishedCandidates.Append(candidate).ToArray());
        // A usable route unlocks practice immediately. Quick mode spends only
        // a shared extra allowance on better choices, even if every route is
        // narrow/steep. Expanded mode retains its separate explicit budget.
        if(!options.Exhaustive && target.IsArea)
            qualityContinuationLimit ??= Math.Min(options.MaxCandidates,Evaluated+480);
    }
    public SearchResult PublishedResult()=>new(physics.Id,physics.Calibrated,Evaluated,false,publishedCandidates,[]);
}
