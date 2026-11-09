using System.Numerics;

namespace NadeLab.Core;

public readonly record struct PredictionVisibility(int Clear,int Total,bool Valid=true)
{
    public bool Usable=>Valid && Total>0 && Clear>=0 && Clear<=Total;
    public bool Open=>Usable && Clear==Total;
    public string Label=>!Usable ? "遮挡未知" : Open ? "无遮挡采样" : Clear==0 ? "遮挡" : "部分遮挡";
}

/// <summary>
/// A provisional interval, NOT a confidence interval, engine simulation, or
/// guaranteed bound. Actual damage and player_blind events remain authoritative.
/// </summary>
public readonly record struct EffectEstimate(bool Valid,GrenadeKind Kind,float Minimum,float Maximum,
    float Distance,float Facing,PredictionVisibility Visibility)
{
    public string Text=>!Valid ? "预测未知（未校准）" : Kind==GrenadeKind.HighExplosive ?
        $"预测掉血{MathF.Floor(Minimum):0}–{MathF.Ceiling(Maximum):0} · {Qualifier}" :
        $"预测致盲{MathF.Floor(Minimum*10)/10:0.0}–{MathF.Ceiling(Maximum*10)/10:0.0}s · {Qualifier}";
    private string Qualifier=>Visibility.Open ? "未校准粗估" : Visibility.Label+"粗估";
}

/// <summary>
/// Deliberately broad, tool-authored effect heuristics; these are not Valve's
/// CS2 formulas. Native weapon Damage/ArmorRatio fields describe weapon data and
/// do not expose the full blast/flash algorithm, so they are not silently used
/// as exact explosive damage coefficients.
///
/// Flash distance scale was sanity-checked against this workspace's attributed
/// Mirage event samples (2026-10-13): forward-facing observers at ~660/892/1048u
/// had ~4.18/3.76/3.49s player_blind event durations. This does not calibrate walls,
/// materials, peripheral vision, other distances, or later engine versions.
/// HE widths intentionally express model uncertainty, not statistical coverage.
/// </summary>
public static class EffectPrediction
{
    public static EffectEstimate HighExplosive(Vector3 center,Vector3 body,int armor,int health,PredictionVisibility visibility)
    {
        if(!Geometry.Finite(center) || !Geometry.Finite(body) || armor<0 || health<0 || !visibility.Usable)
            return new(false,GrenadeKind.HighExplosive,0,0,0,0,visibility);
        float distance=Vector3.Distance(center,body);
        // Two loose falloff curves bracket a useful training estimate. They are
        // intentionally independent of EffectReference's old cosmetic radius.
        float lower=95*MathF.Exp(-MathF.Pow(distance/170,2));
        float upper=140*MathF.Exp(-MathF.Pow(distance/235,2));
        if(armor>0)
        {
            lower=ArmorDamage(lower,armor,.45f);
            upper=ArmorDamage(upper,armor,.65f);
        }
        // A blocked sample cannot prove that the engine gives zero damage:
        // material treatment and multi-point exposure are not reproduced here.
        if(!visibility.Open) lower=0;
        return new(true,GrenadeKind.HighExplosive,Math.Clamp(lower,0,health),Math.Clamp(upper,0,health),distance,0,visibility);
    }

    private static float ArmorDamage(float damage,int armor,float healthRatio)=>
        MathF.Max(damage*healthRatio,damage-2*armor);

    public static EffectEstimate Flash(Vector3 center,Vector3 eye,Aim view,PredictionVisibility visibility)
    {
        if(!Geometry.Finite(center) || !Geometry.Finite(eye) || !float.IsFinite(view.Pitch) ||
            !float.IsFinite(view.Yaw) || view.Pitch is <-89 or >89 || !visibility.Usable)
            return new(false,GrenadeKind.Flash,0,0,0,0,visibility);
        var delta=center-eye;float distance=delta.Length();
        float facing=distance<.01f ? 1 : Math.Clamp(Vector3.Dot(Geometry.Direction(view.Pitch,view.Yaw),delta/distance),-1,1);
        // A continuous orientation weight avoids jumps when turning the dummy.
        // The back of the head is not treated as fully immune to a flash.
        float toward=Math.Clamp((facing+.35f)/1.1f,0,1);
        float weight=.18f+.82f*toward;
        float nominal=MathF.Max(0,5.4f-distance*.0018f)*weight;
        float spread=.35f+nominal*.25f;
        float lower=MathF.Max(0,nominal-spread),upper=MathF.Min(6,nominal+spread);
        // The observed distance band is limited. Never imply a known nonzero
        // minimum outside it, nor claim a hard zero from one occlusion ray.
        if(!visibility.Open || distance>1500) lower=0;
        return new(true,GrenadeKind.Flash,lower,upper,distance,facing,visibility);
    }
}
