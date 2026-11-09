using System.Globalization;

namespace NadeLab.Core;

public readonly record struct DummyRosterItem(int LabelIndex,bool Selected,int Health,int Armor,int LastDamage,
    float BlindSeconds,bool Preparing=false,bool Dead=false,string? Prediction=null,string? Objective=null);

// Shared limits and identities keep creation, prediction and presentation in
// agreement. Eight single-line rows fit the existing client panel protocol.
public static class DummyRoster
{
    public const int PlayerLimit=8,ServerLimit=8,MaxBytes=900,MaxLines=9;
    public static string Label(int index)=>index switch
    {
        0=>"A红",1=>"B蓝",2=>"C绿",3=>"D黄",4=>"E紫",5=>"F青",6=>"G橙",7=>"H白",
        _=>throw new ArgumentOutOfRangeException(nameof(index))
    };
    public static uint ColorArgb(int index)=>index switch
    {
        0=>0xFFFF5F5F,1=>0xFF5AA5FF,2=>0xFF6EEB82,3=>0xFFFFDC5A,
        4=>0xFFBE8CFF,5=>0xFF5AE6E6,6=>0xFFFFAA55,7=>0xFFF0F0F0,
        _=>throw new ArgumentOutOfRangeException(nameof(index))
    };
    public static bool CanAdd(int playerCount,int serverCount)=>
        playerCount is >=0 and <PlayerLimit && serverCount is >=0 and <ServerLimit;

    public static string Prediction(EffectEstimate estimate)
    {
        if(!estimate.Valid) return "预未知";
        string occlusion=estimate.Visibility.Open ? "" : estimate.Visibility.Clear==0 ? "(挡)" : "(部分挡)";
        return estimate.Kind==GrenadeKind.HighExplosive ?
            FormattableString.Invariant($"预雷{MathF.Floor(estimate.Minimum):0}–{MathF.Ceiling(estimate.Maximum):0}HP{occlusion}") :
            FormattableString.Invariant($"预闪{MathF.Floor(estimate.Minimum*10)/10:0.0}–{MathF.Ceiling(estimate.Maximum*10)/10:0.0}s{occlusion}");
    }

    // A search prediction or a completed event is context for the live row,
    // never a replacement for current health, armor or remaining blindness.
    public static string FlashContext(FlashRole role,FlashMeasurement? measurement,EffectEstimate? estimate,string? preview=null)
    {
        string requirement=role switch {FlashRole.MustFlash=>"要闪",FlashRole.Protect=>"不闪",_=>"不参与"};
        // While the player positions a grenade, the current centre's preview
        // is more relevant than a saved candidate or a previous throw record.
        if(!string.IsNullOrWhiteSpace(preview)) return requirement+"·"+preview;
        if(role==FlashRole.Ignore) return requirement;
        string status=measurement?.State switch
        {
            FlashMeasurementState.ObservedBlind when measurement.EventDuration is { } duration && float.IsFinite(duration)=>
                "记录闪"+Math.Clamp(duration,0,99.9f).ToString("0.0",CultureInfo.InvariantCulture)+"s",
            FlashMeasurementState.NoBlindObserved=>"记录未见闪",
            FlashMeasurementState.Unknown=>"记录未知",
            FlashMeasurementState.Pending=>"观测中",
            _ when measurement is not null=>"记录未知",
            _=>estimate is not { } prediction ? "未搜索" : !prediction.Valid ? "预未知" :
                role==FlashRole.Protect ? prediction.Visibility.Usable && prediction.Visibility.Clear==0 ? "预测遮挡" : "预测暴露" : Prediction(prediction)
        };
        return requirement+"·"+status;
    }

    public static string? Format(IEnumerable<DummyRosterItem> items)
    {
        var rows=items.OrderBy(row=>row.LabelIndex).Take(PlayerLimit).ToArray();
        if(rows.Length==0) return null;
        bool objective=rows.Any(row=>!string.IsNullOrWhiteSpace(row.Objective));
        string heading=$"{(objective ? "假人实况" : "测试假人")} {rows.Length}/{PlayerLimit}";
        if(rows.Any(row=>!string.IsNullOrWhiteSpace(row.Prediction) || row.Objective?.Contains("预",StringComparison.Ordinal)==true)) heading+=" · 预测未校准";
        else if(objective) heading+=" · 右侧：要求/记录";
        var lines=new List<string>{heading};
        foreach(var row in rows)
        {
            string health=row.Preparing ? "准备中" : row.Dead ? "已死亡" : $"{Math.Clamp(row.Health,0,999)}HP 甲{Math.Clamp(row.Armor,0,999)}";
            float remaining=float.IsFinite(row.BlindSeconds) && !row.Preparing && !row.Dead ? Math.Clamp(row.BlindSeconds,0,99.9f) : 0;
            string blind=remaining>.05f ? "致盲"+remaining.ToString("0.0",CultureInfo.InvariantCulture)+"s" : "未致盲";
            string text=$"{(row.Selected ? "▶" : " ")}{Label(row.LabelIndex)} {health} 掉血{Math.Clamp(row.LastDamage,0,99999)} {blind}";
            string? context=row.Objective ?? row.Prediction;
            if(!string.IsNullOrWhiteSpace(context)) text+=" | "+PanelText.Line(context,26);
            lines.Add(PanelText.Bounded(text,104,1));
        }
        return PanelText.Bounded(string.Join('\n',lines),MaxBytes,MaxLines);
    }
}
