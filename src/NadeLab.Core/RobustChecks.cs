namespace NadeLab.Core;

public readonly record struct ExecutionTrial(string Group,ThrowStart Start,Aim Aim);
public sealed record CheckScore(string Group,int Hits,int Trials,float WorstMiss,float MeanMiss);

public sealed class RobustChecks
{
    private readonly Dictionary<string,(int Hits,int Trials,float Worst,float Total)> scores=[];
    public void Observe(string group,bool hit,float miss)
    {
        var old=scores.GetValueOrDefault(group);
        miss=float.IsFinite(miss) ? MathF.Max(0,miss) : 10000;
        scores[group]=(old.Hits+(hit ? 1 : 0),old.Trials+1,MathF.Max(old.Worst,miss),old.Total+miss);
    }
    public IReadOnlyList<CheckScore> Results()=>scores.Select(p=>new CheckScore(p.Key,p.Value.Hits,p.Value.Trials,p.Value.Worst,p.Value.Total/p.Value.Trials)).ToArray();
    public static string Label(string group)=>group switch
    {
        "position"=>"站位","angle"=>"角度","coupled"=>"角度与站位","eye"=>"眼高",
        "motion-speed"=>"移动速度","motion-timing"=>"速度与松键","latch"=>"跳投状态",
        "surface"=>"反弹参数","source"=>"出手模型",_=>group
    };
    public static string Explain(Candidate candidate)
    {
        var weak=candidate.Checks?.Where(c=>c.Hits<c.Trials && c.Group!="source")
            .OrderBy(c=>(float)c.Hits/c.Trials).ThenByDescending(c=>c.WorstMiss).Take(2).ToArray();
        return weak is not {Length:>0} ? "" : "敏感项："+string.Join("、",weak.Select(c=>$"{Label(c.Group)} {c.Hits}/{c.Trials}"));
    }
}
