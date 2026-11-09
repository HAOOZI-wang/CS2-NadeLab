using System.Collections.ObjectModel;
using System.Numerics;

namespace NadeLab.Core;

public enum FlashRole { Ignore, MustFlash, Protect }

// A search owns these poses, rather than retaining live entities or mutable rosters.
public readonly record struct FlashObserver(uint Id,string Label,Vector3 Eye,Aim View,FlashRole Role);

public sealed record FlashObserverEvaluation(FlashObserver Observer,EffectEstimate Estimate,bool GeometryAccepted)
{
    public bool Participates=>Observer.Role!=FlashRole.Ignore;
    public bool StrictProtectionVerified=>false;
    public string Description=>Observer.Role switch
    {
        FlashRole.Ignore=>"不参与",
        FlashRole.Protect when GeometryAccepted=>"遮挡候选·不闪待实投",
        FlashRole.Protect when !Estimate.Valid=>"遮挡未知·不采纳",
        FlashRole.Protect=>"仍有暴露·不采纳",
        _ when GeometryAccepted=>"预计可闪·待实投",
        _ when !Estimate.Valid=>"预测未知·不采纳",
        _=>"预计闪效不足"
    };
}

/// <summary>
/// Per-observer geometric screening, not the engine flash algorithm. Every
/// participating observer must pass independently; a protected player's exposure
/// can never be traded against the benefit to another player.
/// </summary>
public sealed class FlashObjective
{
    public IReadOnlyList<FlashObserver> Observers { get; }
    public float MinimumFlashSeconds { get; }
    public FlashObjective(IEnumerable<FlashObserver> observers,float minimumFlashSeconds=1f)
    {
        ArgumentNullException.ThrowIfNull(observers);
        var snapshot=observers.ToArray();
        if(snapshot.Length is <1 or >8 || snapshot.Select(x=>x.Id).Distinct().Count()!=snapshot.Length ||
            !snapshot.Any(x=>x.Role==FlashRole.MustFlash) || !float.IsFinite(minimumFlashSeconds) || minimumFlashSeconds is <.1f or >4)
            throw new ArgumentException("A flash objective needs 1–8 unique observers and at least one must-flash observer.");
        if(snapshot.Any(x=>!Enum.IsDefined(x.Role) || string.IsNullOrWhiteSpace(x.Label) || x.Label.Length>64 ||
            !Geometry.Finite(x.Eye) || !float.IsFinite(x.View.Pitch) || x.View.Pitch is <-89 or >89 || !float.IsFinite(x.View.Yaw)))
            throw new ArgumentException("Invalid flash observer pose.");
        Observers=new ReadOnlyCollection<FlashObserver>(snapshot);
        MinimumFlashSeconds=minimumFlashSeconds;
    }

    public FlashObserverEvaluation Evaluate(Vector3 detonation,FlashObserver observer,PredictionVisibility visibility)
    {
        if(observer.Role==FlashRole.Ignore)
            return new(observer,new(false,GrenadeKind.Flash,0,0,0,0,new(0,0,false)),true);
        var estimate=EffectPrediction.Flash(detonation,observer.Eye,observer.View,visibility);
        bool finite=estimate.Valid && float.IsFinite(estimate.Distance) && float.IsFinite(estimate.Facing) &&
            float.IsFinite(estimate.Minimum) && float.IsFinite(estimate.Maximum);
        if(!finite)estimate=estimate with {Valid=false};
        bool accepted=finite && (observer.Role==FlashRole.MustFlash
            ? visibility.Open && estimate.Minimum>=MinimumFlashSeconds
            : observer.Role==FlashRole.Protect && visibility.Total>=3 && visibility.Clear==0);
        return new(observer,estimate,accepted);
    }
}

public sealed record FlashSearchOptions
{
    public int MaxCandidates { get; init; }=2400;
    public int ResultCount { get; init; }=SearchResultCapacity.Default;
    public Aim? PreferredView { get; init; }
    public IReadOnlyList<Aim>? SeedAims { get; init; }
    public void Validate()
    {
        if(MaxCandidates is <1 or >12000 || ResultCount is <1 or >SearchResultCapacity.Maximum || (SeedAims?.Count ?? 0)>24 ||
            (PreferredView is { } view && (!float.IsFinite(view.Pitch) || view.Pitch is <-89 or >89 || !float.IsFinite(view.Yaw))) ||
            (SeedAims?.Any(a=>!FixedAim(a)) ?? false))
            throw new ArgumentException("Invalid flash search options.");
    }
    internal static bool FixedAim(Aim a)=>float.IsFinite(a.Pitch) && a.Pitch is >=-89 and <=89 && float.IsFinite(a.Yaw) &&
        Enum.IsDefined(a.Mode) && a.EffectiveAction is ThrowAction.Standing or ThrowAction.Jump &&
        a.ForwardSpeed==0 && a.ForwardOffset==0 && float.IsFinite(a.ReleaseHeight) && a.ReleaseHeight is >=0 and <=100 &&
        float.IsFinite(a.VerticalSpeed) && a.VerticalSpeed is >=-600 and <=600 &&
        (a.EffectiveAction==ThrowAction.Jump || (a.ReleaseHeight==0 && a.VerticalSpeed==0));
}

public sealed record FlashSearchCandidate(Candidate Candidate,IReadOnlyList<FlashObserverEvaluation> Effects)
{
    public bool CandidateOnly=>true;
    public bool StrictProtectionVerified=>false;
    public bool GeometryAccepted=>Effects.All(e=>e.GeometryAccepted);
    public string ReviewScope=>Candidate.Aim.Airborne ? "角度与跳投时机几何采样·真实闪效待实投" : "角度几何采样·真实闪效待实投";
    public string Status=>GeometryAccepted ? "预测候选·待实投" : "尚未满足全部筛选条件";
}

public sealed record FlashSearchResult(string PhysicsId,int Evaluated,bool Truncated,
    IReadOnlyList<FlashSearchCandidate> Matches,IReadOnlyList<FlashSearchCandidate> Nearest)
{
    public bool CandidateOnly=>true;
    public bool StrictProtectionVerified=>false;
    public string Conclusion=>Matches.Count>0 ? "找到预测候选，需手投核验每个假人" : "当前搜索未找到符合全部条件的候选，不代表理论无解";
}
