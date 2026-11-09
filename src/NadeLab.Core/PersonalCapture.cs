namespace NadeLab.Core;

public sealed class PersonalCapture(ImpactOwner owner,uint pawn,GrenadeKind kind,float started)
{
    private float? release;
    private bool closed;
    public bool AwaitingImpact=>release is not null && !closed;
    private bool Matches(ImpactOwner current,uint currentPawn,GrenadeKind currentKind)=>
        current==owner && owner.Controller!=0 && owner.Steam!=0 && pawn!=0 && currentPawn==pawn && currentKind==kind && Enum.IsDefined(kind);
    public bool Release(ImpactOwner current,uint currentPawn,GrenadeKind currentKind,float at)
    {
        if(closed || !Matches(current,currentPawn,currentKind) || !float.IsFinite(at) || !float.IsFinite(started) || at<started || at-started>120) return false;
        if(release is not null) {closed=true;return false;}
        release=at;return true;
    }
    public bool Complete(ImpactOwner current,uint currentPawn,GrenadeKind currentKind,float releaseAt,float now)
    {
        if(closed || !Matches(current,currentPawn,currentKind) || release is null || release!=releaseAt || !float.IsFinite(now) || now<releaseAt || now-releaseAt>30) return false;
        closed=true;return true;
    }
}
