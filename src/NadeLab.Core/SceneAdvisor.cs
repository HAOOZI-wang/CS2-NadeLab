using System.Numerics;

namespace NadeLab.Core;

public static class SceneAdvisor
{
    // This is an angle cue only; it does not inspect visible map landmarks.
    public static bool HasSteepUpwardAim(Aim aim)=>aim.Pitch<=-60;
    public static float ViewTurn(Aim aim,Aim reference) => MathF.Sqrt(MathF.Pow(aim.Pitch-reference.Pitch,2)+MathF.Pow(Geometry.NormalizeYaw(aim.Yaw-reference.Yaw),2));
    public static float ActionCost(Aim aim) => aim.EffectiveAction switch
    {
        ThrowAction.Standing=>0,ThrowAction.Crouch=>.2f,ThrowAction.Jump=>.35f,ThrowAction.AirForwardJump=>.45f,
        ThrowAction.Walk=>.6f,ThrowAction.Run=>.7f,ThrowAction.RunJump=>1.1f,ThrowAction.Step=>1.6f,ThrowAction.StepJump=>2.5f,_=>.7f
    };
    // A sky-facing angle is only an aiming inconvenience proxy, not evidence
    // of missing landmarks. Reliability and interior clearance still come first.
    public static float ManualCost(Candidate c)=>ActionCost(c.Aim)+MathF.Max(0,-c.Aim.Pitch-45)*.025f+
        (c.Aim.NeedsRunUp ? c.ReleaseWindow is {Duration:>=.1f} ? 0 : .8f : 0);
    public static IReadOnlyList<Candidate> Order(IReadOnlyList<Candidate> candidates,Aim? preferredView=null,bool smokeArea=false,Target? target=null)
    {
        if (candidates.Count==0) return candidates;
        var best=smokeArea ? candidates.OrderByDescending(SearchQuality.ExecutionScore).ThenBy(c=>ActionCost(c.Aim)).ThenBy(c=>c.Trajectory.WallBounces).First() :
            candidates.OrderBy(c=>c.Trajectory.WallBounces).ThenByDescending(c=>c.Reliability).ThenBy(c=>c.Error).First();
        bool Interior(Candidate c)=>target is not {IsArea:true} region || region.InteriorMargin(c.Trajectory.Endpoint)>=.3f;
        var comparable=candidates.Where(c=>(smokeArea || c.Trajectory.WallBounces==best.Trajectory.WallBounces) &&
            (smokeArea ? SearchQuality.ExecutionScore(c)>=SearchQuality.ExecutionScore(best)-.12f : c.Reliability>=best.Reliability-.12f) && (smokeArea || c.Error<=MathF.Max(8,best.Error+5))).ToArray();
        // Prefer a reproducible stationary action; a substantially flatter,
        // shorter moving flight can justify the additional release-speed control.
        float Cost(Candidate c)=>c.Trajectory.Time + ActionCost(c.Aim)
            + MathF.Max(0,MathF.Abs(c.Aim.Pitch)-45)*.025f;
        var ordered=smokeArea ? comparable.OrderByDescending(Interior).ThenBy(ManualCost).ThenBy(SearchQuality.SourceUncertain).ThenBy(c=>preferredView is { } reference ? ViewTurn(c.Aim,reference) : 0).ThenBy(c=>c.Trajectory.WallBounces).ThenBy(Cost).ToArray() :
            preferredView is { } view ? comparable.OrderBy(c=>ViewTurn(c.Aim,view)).ThenBy(Cost).ThenBy(c=>c.Error).ToArray() :
            comparable.OrderBy(Cost).ThenBy(c=>c.Error).ToArray();
        return ordered.Concat(candidates.Where(c=>!ordered.Contains(c))).ToArray();
    }

    public static string Explain(ThrowStart start,Target target,Candidate selected,IReadOnlyList<Candidate> candidates)
    {
        var delta=target.Position-start.Feet;
        float distance=new Vector2(delta.X,delta.Y).Length();
        string scene=$"平距 {distance:F0}u / 高差 {delta.Z:+0;-0;0}u";
        if(HasSteepUpwardAim(selected.Aim))
            scene+=" · 大仰角，画面可能主要朝向天空；未识别可复现瞄点，可继续寻找更多路线";
        if(selected.ModelTrials>0) scene+=$" · 表面参数 {selected.ModelHits}/{selected.ModelTrials}";
        if(selected.SourceStressTrials>0 && selected.SourceStressHits<selected.SourceStressTrials)
            scene+=$" · 出手模型敏感 {selected.SourceStressHits}/{selected.SourceStressTrials}，需实投验证";
        if(selected.RobustTrials>0 && !SearchQuality.Recommended(selected)) return $"{scene} · 低容错投法：模拟可达，须严格对准 · {RobustChecks.Explain(selected)} · 模拟扰动 {selected.RobustHits}/{selected.RobustTrials}";
        if(selected.Aim.EffectiveAction==ThrowAction.AirForwardJump) return $"{scene} · 停稳起跳时轻点W并松鼠标，勿提前走动 · 动作参考扰动 {selected.ExecutionHits}/{selected.ExecutionTrials}；按键时序仍需实投确认";
        if(selected.Aim.ForwardSpeed>0 && selected.Aim.Airborne) return $"{scene} · 移动跳投需控水平速度 · 手动速度/时机扰动 {selected.ExecutionHits}/{selected.ExecutionTrials}；实验方案";
        if(selected.Aim.Airborne) return $"{scene} · 使用CS2锁定跳投；起跳同时松鼠标，无需追高度 · 扰动 {selected.RobustHits}/{selected.RobustTrials}";
        if(selected.Aim.EffectiveAction==ThrowAction.Step) return $"{scene} · 一步投从原站位向前起步，不需要后方助跑空间；参数待实投校准";
        if(selected.Aim.EffectiveAction==ThrowAction.Crouch) return $"{scene} · 蹲投降低眼位/出手点，使用{selected.Aim.Instruction}，参数待实投校准";
        if (selected.Aim.ForwardSpeed>0)
        {
            var stationary=candidates.Where(c=>c.Aim.EffectiveAction==ThrowAction.Standing).OrderBy(c=>c.Error).FirstOrDefault();
            string reason=stationary is null ? "本轮静止投未找到命中；移动增加射程" :
                selected.Trajectory.WallBounces<stationary.Trajectory.WallBounces ? "移动方案减少撞墙" :
                selected.Trajectory.Time+.5f<stationary.Trajectory.Time ? "移动方案飞行更短、弧线更平" : "可用的移动替代方案";
            return $"{scene} · {reason}；需控速，移动物理待实投校准";
        }
        return $"{scene} · {(selected.Trajectory.WallBounces==0 ? "静止直投可达，动作简单" : "本轮需要反弹路线")} · 模拟扰动 {selected.RobustHits}/{selected.RobustTrials}";
    }
}
