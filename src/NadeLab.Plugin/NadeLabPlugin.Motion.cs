using System.Numerics;
using CounterStrikeSharp.API.Core;
using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using NadeLab.Core;
using CsTrace=CounterStrikeSharp.API.Modules.Utils.Trace;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private static bool Upright(CCSPlayerPawn pawn) => pawn.MovementServices is { } movement ? movement.As<CCSPlayer_MovementServices>().DuckAmount<.05f : pawn.ViewOffset.Z>=60;
    private static bool GroundedUpright(CCSPlayerPawn pawn) => pawn.MoveType==MoveType_t.MOVETYPE_WALK && (pawn.Flags&1)!=0 && Upright(pawn);
    private static float SmokeRunSpeed(CCSPlayerPawn? pawn=null,GrenadeKind kind=GrenadeKind.Smoke)
    {
        var data=pawn?.WeaponServices?.MyWeapons.Select(h=>h.Value).FirstOrDefault(w=>w is {IsValid:true} && w.DesignerName==kind.Weapon())?.As<CBaseCSGrenade>().VData;
        float value=data?.MaxSpeed.Values[0] ?? 250;
        return float.IsFinite(value) && value is >=100 and <=300 ? value : 250;
    }
    private static float AirForwardSpeed()
    {
        float speed=CounterStrikeSharp.API.Modules.Cvars.ConVar.Find("sv_air_max_wishspeed")?.GetPrimitiveValue<float>() ?? 0;
        float acceleration=CounterStrikeSharp.API.Modules.Cvars.ConVar.Find("sv_airaccelerate")?.GetPrimitiveValue<float>() ?? 0;
        // This recipe is evidenced for stock air acceleration. Unknown/custom
        // behavior must not silently acquire the same controllability claim.
        return MathF.Abs(speed-30)<.01f && MathF.Abs(acceleration-12)<.01f ? speed : 0;
    }
    private static Vector3? FindRunUp(CCSPlayerPawn? pawn,Vector3 release,Aim aim,float maxDistance=100)
        => FindRunUpSteps(pawn,release,aim,maxDistance).LastOrDefault();
    private static IEnumerable<Vector3?> FindRunUpSteps(CCSPlayerPawn? pawn,Vector3 release,Aim aim,float maxDistance=100)
    {
        if (pawn is {IsValid:false}) yield break;
        // Console replay has no player. Use the standard standing hull only in
        // that diagnostic; actual players always supply their engine hull.
        var mins=pawn?.Collision?.Mins ?? new CounterStrikeSharp.API.Modules.Utils.Vector(-16,-16,0);
        var maxs=pawn?.Collision?.Maxs ?? new CounterStrikeSharp.API.Modules.Utils.Vector(16,16,72);
        var heading=Geometry.Direction(0,aim.Yaw);
        var options=new TraceOptions {InteractsAs=Contents.Player,InteractsWith=Masks.PlayerSolid|Contents.PhysicsProp};
        Vector3 previous=release;
        Vector3? safe=null;
        // Follow the support of the full player hull. A fixed-height test rejects
        // runnable ramps; point-only support can miss holes under the player.
        for(int distance=10;distance<=maxDistance;distance+=10)
        {
            var xy=release-heading*distance;
            xy.Z=previous.Z;
            var floor=CsTrace.TraceHullShape(NativeWorld.ToNative(xy+new Vector3(0,0,18)),NativeWorld.ToNative(xy-new Vector3(0,0,18)),mins,maxs,pawn,options);
            yield return null;
            if(!floor.DidHit() || floor.IsAllSolid || floor.Normal.Z<.7f) break;
            var next=NativeWorld.FromNative(floor.EndPos)+new Vector3(0,0,.5f);
            if(MathF.Abs(next.Z-previous.Z)>18) break;
            // Conservative step clearance includes the space swept by stepping
            // over floor detail, with the complete standing hull.
            var sweep=CsTrace.TraceHullShape(NativeWorld.ToNative(previous+new Vector3(0,0,18)),NativeWorld.ToNative(next+new Vector3(0,0,18)),mins,maxs,pawn,options);
            yield return null;
            if(sweep.IsAllSolid || sweep.DidHit()) break;
            previous=next;
            if(distance>=60) safe=next;
        }
        yield return safe;
    }
    private Func<Aim,IEnumerable<Vector3?>> IncrementalRunUpFilter(CCSPlayerPawn? pawn,ThrowStart start)
    {
        var cache=new Dictionary<int,Vector3?>();
        IEnumerable<Vector3?> Steps(Aim aim)
        {
            if(start.Origin==ThrowOrigin.Preparation) {yield return start.Feet;yield break;}
            int yaw=(int)MathF.Round(Geometry.NormalizeYaw(aim.Yaw)*100);
            if(cache.TryGetValue(yaw,out var known)) {yield return known; yield break;}
            Vector3? last=null;
            foreach(var point in FindRunUpSteps(pawn,start.Feet,aim)) {last=point; yield return point;}
            cache[yaw]=last;
        }
        return Steps;
    }
    private Func<Aim,Vector3?> RunUpFilter(CCSPlayerPawn? pawn,ThrowStart start)
    {
        var cache=new Dictionary<int,Vector3?>();
        return aim=>
        {
            if(start.Origin==ThrowOrigin.Preparation) return start.Feet;
            int yaw=(int)MathF.Round(Geometry.NormalizeYaw(aim.Yaw)*100);
            if(!cache.TryGetValue(yaw,out var feet)) cache[yaw]=feet=FindRunUp(pawn,start.Feet,aim);
            return feet;
        };
    }
    private Func<Aim,bool> ActionFilter(CCSPlayerPawn? pawn,ThrowStart start)
    {
        var cache=new Dictionary<ActionSpaceKey,bool>();
        return aim=>
        {
            // The incremental execution-space callback checks the complete
            // forward corridor and release pose for this exact trial.
            if(aim.EffectiveAction==ThrowAction.AirForwardJump) return AirForwardSpeed()>0;
            if(start.Origin==ThrowOrigin.Preparation && aim.NeedsRunUp) return true;
            var key=ActionSpaceKey.For(aim);
            if(cache.TryGetValue(key,out bool allowed)) return allowed;
            var mins=pawn?.Collision?.Mins ?? new CounterStrikeSharp.API.Modules.Utils.Vector(-16,-16,0);
            var maxs=new CounterStrikeSharp.API.Modules.Utils.Vector(16,16,aim.EffectiveAction==ThrowAction.Crouch ? 54 : 72);
            var options=new TraceOptions {InteractsAs=Contents.Player,InteractsWith=Masks.PlayerSolid|Contents.PhysicsProp};
            var lifted=start.Feet+new Vector3(0,0,1);
            var release=aim.ReleaseFeet(start)+new Vector3(0,0,1);
            var clearance=CsTrace.TraceHullShape(NativeWorld.ToNative(lifted),NativeWorld.ToNative(release),mins,maxs,pawn,options);
            allowed=!clearance.IsAllSolid && !clearance.DidHit();
            if(allowed)
            {
                // A jump must have support at takeoff too. Standing and crouch
                // proposals previously bypassed every player-space check.
                var support=aim.Airborne ? lifted : release;
                var floor=CsTrace.TraceHullShape(NativeWorld.ToNative(support),NativeWorld.ToNative(support-new Vector3(0,0,3)),mins,maxs,pawn,options);
                allowed=floor.DidHit() && !floor.IsAllSolid && floor.Normal.Z>.7f;
            }
            cache[key]=allowed; return allowed;
        };
    }

    // Own the native input memory for the entire search, as NativeWorld does.
    // Reuse input buffers only: every support/body trace still queries the live
    // map, and its output is copied before the iterator yields to another tick.
    private sealed class PlayerHullInputs
    {
        public readonly CounterStrikeSharp.API.Modules.Utils.Vector From=new(),To=new(),Mins=new(),Maxs=new();
        public void Bounds(CCSPlayerPawn? pawn)
        {
            var collision=pawn?.Collision;
            Mins.X=collision?.Mins.X ?? -16;Mins.Y=collision?.Mins.Y ?? -16;Mins.Z=collision?.Mins.Z ?? 0;
            Maxs.X=collision?.Maxs.X ?? 16;Maxs.Y=collision?.Maxs.Y ?? 16;Maxs.Z=MathF.Max(72,collision?.Maxs.Z ?? 72);
        }
        public void Segment(Vector3 from,Vector3 to)
        {From.X=from.X;From.Y=from.Y;From.Z=from.Z;To.X=to.X;To.Y=to.Y;To.Z=to.Z;}
    }
    private static readonly TraceOptions PlayerHullTraceOptions=new()
        {InteractsAs=Contents.Player,InteractsWith=Masks.PlayerSolid|Contents.PhysicsProp};
    private static Func<ThrowStart,Aim,IEnumerable<bool?>> ExecutionSpaceFilter(CCSPlayerPawn? pawn,PhysicsProfile profile)
    {
        var inputs=new PlayerHullInputs();
        return (start,aim)=>CheckPreparationPathSteps(pawn,start,aim,profile,inputs:inputs);
    }

    private static Vector3? FindPreparation(CCSPlayerPawn? pawn,ThrowStart start,Aim aim,PhysicsProfile profile)
        => aim.EffectiveAction==ThrowAction.AirForwardJump ? CheckPreparationPathSteps(pawn,start,aim,profile).LastOrDefault()==true ? start.Feet : null :
            !aim.NeedsRunUp ? start.Feet : start.Origin==ThrowOrigin.Release ? FindRunUp(pawn,start.Feet,aim) :
            CheckPreparationPathSteps(pawn,start,aim,profile).LastOrDefault()==true ? start.Feet : null;

    private static IEnumerable<bool?> CheckPreparationPathSteps(CCSPlayerPawn? pawn,ThrowStart start,Aim aim,PhysicsProfile profile,Action<string>? rejection=null,PlayerHullInputs? inputs=null)
    {
        if(aim.EffectiveAction!=ThrowAction.AirForwardJump && (start.Origin!=ThrowOrigin.Preparation || !aim.NeedsRunUp)) {yield return true;yield break;}
        // Recheck live settings even for retained results or a continuation.
        // A previously supported recipe must not survive changed air physics.
        if(aim.EffectiveAction==ThrowAction.AirForwardJump && AirForwardSpeed()<=0)
        {rejection?.Invoke("controlled air-jump settings changed; search again");yield return false;yield break;}
        if(pawn is {IsValid:false} || pawn is not null && pawn.Collision is null)
        {rejection?.Invoke("player hull unavailable");yield return false;yield break;}
        // Always test the standing body, even if the player crouches while a
        // search is running. Console diagnostics use the standard standing hull.
        inputs ??=new PlayerHullInputs();inputs.Bounds(pawn);
        SweepHit Trace(Vector3 from,Vector3 to)
        {
            if(pawn is {IsValid:false}) return new(0,from,Vector3.UnitZ,true);
            inputs.Segment(from,to);
            var hit=CsTrace.TraceHullShape(inputs.From,inputs.To,inputs.Mins,inputs.Maxs,pawn,PlayerHullTraceOptions);
            // No native trace result or vector is retained across an iterator yield.
            return new(hit.Fraction,NativeWorld.FromNative(hit.EndPos),NativeWorld.FromNative(hit.Normal),hit.IsAllSolid);
        }
        foreach(var step in CheckPreparationPathSteps(start,aim,profile,Trace,rejection)) yield return step;
    }

    // The injected sweep is the complete standing player hull, not a grenade
    // or a point. This keeps native lifetime concerns outside path validation.
    private static IEnumerable<bool?> CheckPreparationPathSteps(ThrowStart start,Aim aim,PhysicsProfile profile,
        Func<Vector3,Vector3,SweepHit> hullSweep,Action<string>? rejection=null)
    {
        if(aim.EffectiveAction!=ThrowAction.AirForwardJump && (start.Origin!=ThrowOrigin.Preparation || !aim.NeedsRunUp)) {yield return true;yield break;}
        if(!ThrowExecution.Valid(start) || !float.IsFinite(aim.Yaw) || !float.IsFinite(aim.ForwardSpeed) || aim.ForwardSpeed is <=0 or >300 ||
            !float.IsFinite(aim.ForwardOffset) || aim.ForwardOffset is <0 or >100 || !float.IsFinite(aim.ReleaseHeight) || aim.ReleaseHeight is <0 or >100 ||
            !float.IsFinite(profile.ReleaseDelaySeconds) || profile.ReleaseDelaySeconds is <0 or >.5f ||
            !float.IsFinite(profile.PlayerGravity) || profile.PlayerGravity is <=0 or >2000)
        {rejection?.Invoke("invalid preparation origin or action parameters");yield return false;yield break;}
        var plan=ThrowExecution.Resolve(start,aim,profile);
        var heading=Geometry.Direction(0,aim.Yaw);
        var groundEnd=aim.Airborne ? plan.ButtonFeet : plan.ReleaseFeet;
        float groundDistance=Vector3.Dot(groundEnd-start.Feet,heading);
        if(!Geometry.Finite(groundEnd) || !float.IsFinite(groundDistance) || groundDistance<0 ||
            MathF.Abs(groundEnd.Z-start.Feet.Z)>.01f)
        {rejection?.Invoke("ground endpoint does not match the preparation plane");yield return false;yield break;}
        Vector3? previous=null;
        int samples=(int)MathF.Ceiling(groundDistance/4);
        for(int i=0;i<=samples;i++)
        {
            var point=start.Feet+heading*MathF.Min(i*4,groundDistance);
            var floor=hullSweep(point+Vector3.UnitZ*3,point-Vector3.UnitZ*3);
            yield return null;
            if(!floor.Hit || floor.StartSolid || !Geometry.Finite(floor.Position) || !Geometry.Finite(floor.Normal) || floor.Normal.Z<.7f ||
                new Vector2(floor.Position.X-point.X,floor.Position.Y-point.Y).Length()>.01f)
            {rejection?.Invoke($"missing or invalid player-hull support at {MathF.Min(i*4,groundDistance):F1}u");yield return false;yield break;}
            if(MathF.Abs(floor.Position.Z-start.Feet.Z)>2)
            {rejection?.Invoke($"ground drift {floor.Position.Z-start.Feet.Z:F2}u at {MathF.Min(i*4,groundDistance):F1}u exceeds 2u");yield return false;yield break;}
            var supported=floor.Position+Vector3.UnitZ*.5f;
            // Check occupancy at the origin too. Never infer body clearance
            // from a grounded flag or from a successful downward support hit.
            var clearance=hullSweep(previous ?? supported,supported);
            yield return null;
            if(clearance.Hit || clearance.StartSolid)
            {rejection?.Invoke($"standing body blocked at {MathF.Min(i*4,groundDistance):F1}u");yield return false;yield break;}
            previous=supported;
        }
        if(aim.Airborne)
        {
            var takeoff=plan.ButtonFeet+Vector3.UnitZ*.5f;
            var align=hullSweep(previous!.Value,takeoff);
            yield return null;
            if(align.Hit || align.StartSolid)
            {rejection?.Invoke("nominal takeoff intersects actual support");yield return false;yield break;}
            previous=takeoff;
            for(int i=1;i<=4;i++)
            {
                float fraction=i/4f;
                // Four chords follow the latched jump arc rather than cutting
                // straight from the takeoff feet to the release feet. The
                // half-unit clearance covers each chord's sub-unit arc bulge.
                float curve=.5f*profile.PlayerGravity*JumpLatch.Seconds*JumpLatch.Seconds*fraction*(1-fraction);
                var point=Vector3.Lerp(plan.ButtonFeet,plan.ReleaseFeet,fraction)+Vector3.UnitZ*(.5f+curve);
                var clearance=hullSweep(previous.Value,point);
                yield return null;
                if(clearance.Hit || clearance.StartSolid)
                {rejection?.Invoke($"jump body blocked at {fraction*JumpLatch.Seconds:F3}s");yield return false;yield break;}
                previous=point;
            }
        }
        yield return true;
    }

    private void PreparationPathDiagnostic(CCSPlayerController? caller,CommandInfo command)
    {
        if(caller is not null || !Practice || !BenchmarkIdle || Utilities.GetPlayers().Any(p=>!p.IsBot))
        {Say(null,"PATHCHECK refused: empty idle practice server required.");return;}
        if(command.ArgCount is not (6 or 7))
        {Say(null,"PATHCHECK usage: css_nl_pathcheck x y z yaw run|walk|runjump|airjump [pitch]");return;}
        var values=new float[5];
        for(int i=0;i<4;i++)
            if(!float.TryParse(command.ArgByIndex(i+1),NumberStyles.Float,CultureInfo.InvariantCulture,out values[i]) || !float.IsFinite(values[i]))
            {Say(null,"PATHCHECK refused: finite coordinates and yaw required.");return;}
        values[4]=-25;
        if(command.ArgCount==7 && (!float.TryParse(command.ArgByIndex(6),NumberStyles.Float,CultureInfo.InvariantCulture,out values[4]) || !float.IsFinite(values[4])))
        {Say(null,"PATHCHECK refused: finite pitch required.");return;}
        if(values.Take(3).Any(v=>MathF.Abs(v)>32768) || values[4] is <-89 or >89)
        {Say(null,"PATHCHECK refused: coordinates or pitch outside supported bounds.");return;}
        var action=command.ArgByIndex(5).ToLowerInvariant() switch
        {"run"=>ThrowAction.Run,"walk"=>ThrowAction.Walk,"runjump"=>ThrowAction.RunJump,"airjump"=>ThrowAction.AirForwardJump,_=>(ThrowAction)(-1)};
        if(!Enum.IsDefined(action)) {Say(null,"PATHCHECK refused: choose run, walk, runjump or airjump.");return;}
        var profile=physics;
        var aim=ThrowCatalog.Families(profile,new SearchOptions {IncludeActions=true,ForwardSpeed=SmokeRunSpeed()},0,Geometry.NormalizeYaw(values[3]))
            .First(a=>a.EffectiveAction==action && a.Mode==ThrowMode.Left) with {Pitch=values[4]};
        var start=new ThrowStart(new(values[0],values[1],values[2]),64,ThrowOrigin.Preparation);
        var plan=ThrowExecution.Resolve(start,aim,profile);
        string reason="standing corridor, support and release clearance passed";bool? passed=null;int queries=0;
        foreach(var step in CheckPreparationPathSteps(null,start,aim,profile,issue=>reason=issue))
        {if(step is null) queries++;else passed=step;}
        Say(null,$"PATHCHECK {(passed==true ? "PASS" : "REJECTED")}: {aim.Instruction}; preparation={plan.PreparationFeet}; button={plan.ButtonFeet}; release={plan.ReleaseFeet}; hullQueries={queries}; {reason}; read-only, no player moved or record changed.");
    }

    private string ExecutionHint(Aim aim,ThrowStart? start=null)
    {
        if(start is {Origin:ThrowOrigin.Preparation} origin && aim.NeedsRunUp)
            return $"记录点是助跑起点。就位后先蓄力，保持{ThrowInstructions.RunUpKeys(aim)}，以 {aim.ForwardSpeed:F0}u/s 沿瞄准方向前进 {origin.RunUpDistance:F0}u；到松键标记{(aim.Airborne ? "起跳并松鼠标" : "松鼠标")}，继续原方向。";
        return aim.EffectiveAction switch
    {
        ThrowAction.AirForwardJump=>$"原地停稳并蓄力，起跳时轻点W并松鼠标，不要先在地面走一步；保持朝向。参考水平速度 {aim.ForwardSpeed:F0}u/s，按键先后与真实落点仍需实投确认，不要求追着速度数字松键。",
        ThrowAction.Step=>$"按指定鼠标键蓄力，从基准站位按W；{(aim.ForwardSpeed<150 ? "同时松鼠标" : "约0.1秒后松鼠标")}并继续W。预计前移 {aim.ForwardOffset:F0}u 后实际出手、速度 {aim.ForwardSpeed:F0}u/s。参数待实投校准。",
        ThrowAction.Jump=>"先停稳并蓄力，按空格起跳时松指定鼠标键；CS2锁定跳投状态，无需追出手高度，也不要等到落下才松键。",
        ThrowAction.RunJump=>$"从助跑起点先蓄力，以 {aim.ForwardSpeed:F0}u/s 保持{ThrowInstructions.RunUpKeys(aim)}助跑；到起跳松键位置再起跳并松鼠标，继续原方向。当前搜索记录的是起跳基准，助跑起点另在后方。",
        ThrowAction.StepJump=>$"先停稳蓄力，W短起步后起跳同时松键，保持方向；锁定时水平速度约 {aim.ForwardSpeed:F0}u/s。此动作需实投校准，优先试容错更高的站投/普通跳投。",
        ThrowAction.Walk=>"按指定鼠标键蓄力，保持W+Shift助跑，以提示速度到绿色松键线松鼠标，继续W+Shift。",
        ThrowAction.Run=>"按指定鼠标键蓄力，W助跑，以提示速度到绿色松键线松鼠标，继续W。",
        ThrowAction.Crouch=>"回位后按住Ctrl，完全蹲稳、对准后松指定鼠标键。",
        _=>"站立静止，按指定鼠标键蓄力、对准后松键。"
        };
    }
}
