using System.Numerics;

namespace NadeLab.Core;

public enum FlashMeasurementState { Pending, ObservedBlind, NoBlindObserved, Unknown }
public readonly record struct FlashNativeObservation(uint Id,uint Pawn,Vector3 Eye,Aim View,bool Live,
    float BlindUntil,float FlashDuration,float BlindStart);
public sealed record FlashMeasurement(uint Id,string Label,FlashRole Role,FlashMeasurementState State,
    float? EventDuration,string? Reason,bool NativeIncreased=false,int SampleCount=0,float? EventAt=null);

public enum FlashRequirementState { Satisfied, Failed, Unknown, Ignored }
public static class FlashVerificationRequirements
{
    // This threshold concerns player_blind event duration, never full-white time.
    public const float MinimumEventSeconds=1f;
    public static FlashRequirementState Evaluate(FlashMeasurement row)=>row.Role switch
    {
        FlashRole.Ignore=>FlashRequirementState.Ignored,
        _ when row.State is FlashMeasurementState.Pending or FlashMeasurementState.Unknown=>FlashRequirementState.Unknown,
        FlashRole.MustFlash when row.State==FlashMeasurementState.NoBlindObserved=>FlashRequirementState.Failed,
        FlashRole.MustFlash when row.EventDuration is { } duration && float.IsFinite(duration)=>duration>=MinimumEventSeconds ? FlashRequirementState.Satisfied : FlashRequirementState.Failed,
        FlashRole.Protect when row.State==FlashMeasurementState.NoBlindObserved=>FlashRequirementState.Satisfied,
        FlashRole.Protect when row.State==FlashMeasurementState.ObservedBlind && row.EventDuration is >0=>FlashRequirementState.Failed,
        _=>FlashRequirementState.Unknown
    };
    public static FlashRequirementState Overall(IEnumerable<FlashMeasurement> measurements)
    {
        var states=measurements.Select(Evaluate).Where(x=>x!=FlashRequirementState.Ignored).ToArray();
        if(states.Contains(FlashRequirementState.Failed)) return FlashRequirementState.Failed;
        return states.Length>0 && states.All(x=>x==FlashRequirementState.Satisfied) ? FlashRequirementState.Satisfied : FlashRequirementState.Unknown;
    }
    public static string Text(FlashMeasurement row)
    {
        string verdict=Evaluate(row) switch {FlashRequirementState.Satisfied=>"本次达标",FlashRequirementState.Failed=>"未达标",FlashRequirementState.Ignored=>"不参与",_=>"未知"};
        string observation=row.State switch
        {
            FlashMeasurementState.ObservedBlind=>$"致盲事件{row.EventDuration:0.00}s",
            FlashMeasurementState.NoBlindObserved=>"本次未观察致盲",
            FlashMeasurementState.Unknown=>row.Reason ?? "观测不完整",_=>"等待本次手投结果"
        };
        return PanelText.Bounded(verdict+"·"+observation,90,1);
    }
}

/// <summary>
/// One isolated, observed manual throw. Missing player_blind is never converted
/// to a measured zero. Complete, clean native observation is required even for
/// the weaker statement "no blindness observed in this throw".
/// </summary>
public sealed class FlashVerificationTrial
{
    private sealed class Observer(FlashObserver expected,FlashNativeObservation baseline)
    {
        public readonly FlashObserver Expected=expected;
        public readonly FlashNativeObservation Baseline=baseline;
        public float? Duration;
        public float? EventAt;
        public int Samples=1;
        public bool NativeIncreased;
        public string? Unknown;
    }
    private readonly Dictionary<uint,Observer> observers=[];
    public float StartedAt {get;}
    public float LastSampleAt {get;private set;}
    public float? ReleasedAt {get;private set;}
    public float? DetonatedAt {get;private set;}
    public bool Complete {get;private set;}
    public string? InvalidReason {get;private set;}
    public bool BaselineClean=>InvalidReason is null && observers.Count>0 && observers.Values.All(x=>x.Unknown is null);
    public FlashNativeObservation[] Baselines=>observers.Values.Select(x=>x.Baseline).ToArray();
    public const float PostEventWindow=.5f,MaximumSampleGap=.25f;

    public FlashVerificationTrial(IReadOnlyList<FlashObserver> expected,IReadOnlyList<FlashNativeObservation> baseline,float now)
    {
        StartedAt=LastSampleAt=now;
        if(!float.IsFinite(now) || expected.Count is <1 or >8 || expected.Select(x=>x.Id).Distinct().Count()!=expected.Count ||
            baseline.Select(x=>x.Id).Distinct().Count()!=baseline.Count)
        {Invalidate("假人快照无效");return;}
        foreach(var item in expected)
        {
            var samples=baseline.Where(x=>x.Id==item.Id).ToArray();
            if(samples.Length!=1) {Invalidate("缺少假人初始状态");return;}
            var row=new Observer(item,samples[0]);observers.Add(item.Id,row);
            if(!Matches(row,samples[0])) row.Unknown="假人位置或身份已变化";
            else if(!Finite(samples[0]) || Active(samples[0],now)) row.Unknown="开始前仍有致盲状态";
        }
        if(!BaselineClean) Invalidate("开始前状态未清，请复位假人后再试");
    }

    private static bool Finite(FlashNativeObservation state)=>float.IsFinite(state.BlindUntil) &&
        float.IsFinite(state.FlashDuration) && float.IsFinite(state.BlindStart) && state.FlashDuration>=0;
    private static bool Active(FlashNativeObservation state,float now)=>state.BlindUntil>now+.01f ||
        (state.FlashDuration>.01f && (state.BlindStart<=0 || state.BlindStart+state.FlashDuration>now+.01f));
    private static bool Matches(Observer row,FlashNativeObservation state)=>state.Live && state.Pawn==row.Baseline.Pawn &&
        Geometry.Finite(state.Eye) && Vector3.Distance(state.Eye,row.Expected.Eye)<2 &&
        float.IsFinite(state.View.Pitch) && float.IsFinite(state.View.Yaw) &&
        MathF.Abs(state.View.Pitch-row.Expected.View.Pitch)<1 &&
        MathF.Abs(Geometry.NormalizeYaw(state.View.Yaw-row.Expected.View.Yaw))<1;

    public void Release(float now)
    {
        if(Complete) return;
        if(ReleasedAt is not null) {Invalidate("同期有另一颗闪光");return;}
        if(!float.IsFinite(now) || now<LastSampleAt || now-LastSampleAt>MaximumSampleGap)
        {Invalidate("出手前观测不完整");return;}
        ReleasedAt=now;
    }
    public void Detonate(float now)
    {
        if(Complete) return;
        if(ReleasedAt is null || DetonatedAt is not null || !float.IsFinite(now) || now<ReleasedAt)
        {Invalidate("爆炸事件无法唯一关联");return;}
        DetonatedAt=now;
    }
    public void Observe(IReadOnlyList<FlashNativeObservation> current,float now)
    {
        if(Complete) return;
        if(!float.IsFinite(now) || now<LastSampleAt || now-LastSampleAt>MaximumSampleGap)
        {Invalidate("连续观测中断");return;}
        LastSampleAt=now;
        foreach(var (id,row) in observers)
        {
            var samples=current.Where(x=>x.Id==id).ToArray();
            if(samples.Length!=1 || !Matches(row,samples[0])) {row.Unknown??="假人移动、转向、死亡或被替换";continue;}
            var sample=samples[0];
            if(!Finite(sample)) {row.Unknown??="原生致盲状态无效";continue;}
            row.Samples++;
            bool increased=sample.BlindUntil>row.Baseline.BlindUntil+.01f || sample.FlashDuration>row.Baseline.FlashDuration+.01f ||
                sample.BlindStart>row.Baseline.BlindStart+.01f;
            if(increased)
            {
                row.NativeIncreased=true;
                if(ReleasedAt is null) row.Unknown??="出手前出现其他致盲";
            }
        }
        if(ReleasedAt is null && now-StartedAt>90) Invalidate("等待手投已过期");
        else if(ReleasedAt is { } release && DetonatedAt is null && now-release>8) Invalidate("没有收到对应爆炸事件");
        else if(DetonatedAt is { } det && now>=det+PostEventWindow) Complete=true;
    }
    public void Blind(uint id,uint pawn,Vector3 eye,Aim view,float duration,float eventTime,bool attributed)
    {
        if(Complete || !observers.TryGetValue(id,out var row)) return;
        var atEvent=new FlashNativeObservation(id,pawn,eye,view,true,0,0,0);
        if(!attributed || DetonatedAt is not { } det || !float.IsFinite(eventTime) || MathF.Abs(eventTime-det)>.25f)
        {row.Unknown??="致盲事件归属不确定";return;}
        if(!Matches(row,atEvent)) {row.Unknown??="事件时假人位置或朝向已变化";return;}
        if(!float.IsFinite(duration) || duration<0) {row.Unknown??="致盲事件时长无效";return;}
        if(row.Duration is not null) {row.Unknown??="收到重复致盲事件";return;}
        row.Duration=duration;row.EventAt=eventTime;
    }
    public void Invalidate(string reason)
    {
        InvalidReason??=reason;Complete=true;
    }
    public FlashMeasurement[] Results()=>observers.Values.Select(row=>
    {
        string? reason=InvalidReason ?? row.Unknown ?? (Complete && row.NativeIncreased && row.Duration is not >0 ? "原生状态增加但缺少对应致盲事件" : null);
        var state=reason is not null ? FlashMeasurementState.Unknown : !Complete ? FlashMeasurementState.Pending :
            row.Duration is >0 ? FlashMeasurementState.ObservedBlind : FlashMeasurementState.NoBlindObserved;
        return new FlashMeasurement(row.Expected.Id,row.Expected.Label,row.Expected.Role,state,row.Duration,reason,row.NativeIncreased,row.Samples,row.EventAt);
    }).ToArray();
}
