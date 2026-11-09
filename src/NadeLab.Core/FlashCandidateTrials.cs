namespace NadeLab.Core;

/// <summary>Past matched attempts scoped to one immutable search plan, never an effect guarantee.</summary>
public sealed class FlashCandidateTrials
{
    private readonly Dictionary<uint,FlashRole> expected;
    private IReadOnlyList<FlashMeasurement>?[] history;
    public FlashCandidateTrials(int candidateCount,IReadOnlyList<FlashObserver> observers)
    {
        if(candidateCount is <1 or >128 || observers.Count is <1 or >8 ||
            observers.Any(o=>o.Role is not (FlashRole.MustFlash or FlashRole.Protect)) ||
            observers.Select(o=>o.Id).Distinct().Count()!=observers.Count)
            throw new ArgumentException("Invalid flash candidate history scope.");
        expected=observers.ToDictionary(o=>o.Id,o=>o.Role);
        history=new IReadOnlyList<FlashMeasurement>?[candidateCount];
    }
    public void Grow(int candidateCount)
    {
        if(candidateCount is <1 or >128) throw new ArgumentOutOfRangeException(nameof(candidateCount));
        if(candidateCount>history.Length) Array.Resize(ref history,candidateCount);
    }
    public bool Record(int candidate,bool complete,bool executionMatched,string? invalidReason,IReadOnlyList<FlashMeasurement> rows)
    {
        if(candidate<1 || candidate>history.Length || !complete || !executionMatched || invalidReason is not null ||
            rows.Count!=expected.Count || rows.Select(r=>r.Id).Distinct().Count()!=expected.Count ||
            rows.Any(r=>!expected.TryGetValue(r.Id,out var role) || role!=r.Role || r.Reason is not null || r.SampleCount<=0 ||
                (r.State==FlashMeasurementState.ObservedBlind &&
                    (!r.NativeIncreased || r.EventDuration is not { } duration || !float.IsFinite(duration) || duration<=0)) ||
                (r.State==FlashMeasurementState.NoBlindObserved && (r.NativeIncreased || r.EventDuration is not (null or 0))) ||
                FlashVerificationRequirements.Evaluate(r) is not (FlashRequirementState.Satisfied or FlashRequirementState.Failed))) return false;
        history[candidate-1]=Array.AsReadOnly(rows.ToArray());
        return true;
    }
    public IReadOnlyList<FlashMeasurement>? Measurements(int candidate)=>candidate>=1 && candidate<=history.Length ? history[candidate-1] : null;
    public FlashRequirementState? Outcome(int candidate)=>Measurements(candidate) is { } rows ? FlashVerificationRequirements.Overall(rows) : null;
    public int? Next(int current)
    {
        if(current<1 || current>history.Length)current=0;
        // Never call the current selection a different candidate. Explicit preparation remains a retry.
        for(int offset=1;offset<=(current==0 ? history.Length : history.Length-1);offset++)
        {
            int candidate=(current+offset-1)%history.Length+1;
            if(Outcome(candidate)!=FlashRequirementState.Failed)return candidate;
        }
        return null;
    }
    public string? Summary(int candidate)=>Outcome(candidate) switch
    {
        FlashRequirementState.Failed=>"本轮上次匹配实投未达标 · 可重试或换投法",
        FlashRequirementState.Satisfied=>"本轮上次匹配实投达标 · 不保证每次复现",
        _=>null
    };
}
