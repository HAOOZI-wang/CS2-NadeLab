using System.Diagnostics;
using System.Numerics;
using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private bool flashSearchCheckPending;
    private Action? stopFlashSearchCheck;
    private void StopFlashSearchDiagnostic()=>stopFlashSearchCheck?.Invoke();
    private void RegisterFlashSearchDiagnostic()=>AddCommand("css_nl_flashcheck",
        "Empty-server native flash inverse-search and objective-isolation diagnostic",(caller,command)=>
    {
        if(caller is not null || !loaded || !Practice || Utilities.GetPlayers().Any(p=>!p.IsBot) ||
            flashSearchCheckPending || !BenchmarkIdle)
        {Say(caller,"FLASHCHECK refused: server console, idle practice server and zero humans required.");return;}
        var owner=Utilities.GetPlayers().FirstOrDefault(p=>p.IsValid && p.IsBot &&
            p.PlayerPawn.Value is {IsValid:true,LifeState:0,AbsOrigin:not null} && !ThrowInProgress(p));
        if(owner?.PlayerPawn.Value is not {IsValid:true,LifeState:0,AbsOrigin:not null} source)
        {Say(null,"FLASHCHECK refused: one existing living idle bot is required.");return;}

        // Read-only native world queries. Never replace a session or spawn a real
        // grenade/dummy; no inventory, pose, HP or blinded-state restoration is needed.
        int epoch=grenadeEpoch,passed=0,failed=0,phase=0,fixtureIndex=0;
        uint ownerHandle=owner.EntityHandle.Raw,pawnHandle=source.EntityHandle.Raw;
        string map=Server.MapName;
        var start=new ThrowStart(NativeWorld.FromNative(source.AbsOrigin),source.ViewOffset.Z);
        var initialView=new Aim(source.EyeAngles.X,source.EyeAngles.Y,ThrowMode.Left);
        int initialHealth=source.Health,initialArmor=source.ArmorValue;
        float initialBlind=source.FlashDuration,initialUntil=source.BlindUntilTime;
        var profile=Grenades.Bootstrap(GrenadeKind.Flash,physics);
        var world=new NativeWorld(source);
        var fixtureAims=new[]{new Aim(-35,initialView.Yaw,ThrowMode.Right),new Aim(-60,initialView.Yaw,ThrowMode.Right),
            new Aim(-20,initialView.Yaw+90,ThrowMode.Right),new Aim(-50,initialView.Yaw-90,ThrowMode.Both),
            new Aim(-60,initialView.Yaw+180,ThrowMode.Right)};
        SmokeSimulation? fixture=null;FlashEffectSearch? search=null;FlashObserver observer=default;
        Aim fixtureAim=default;Trajectory? known=null;bool stopped=false,advanceBounded=true;
        long began=Stopwatch.GetTimestamp();
        void Check(bool ok,string message)
        {if(ok)passed++;else failed++;Say(null,$"FLASHCHECK {(ok ? "PASS" : "FAIL")}: {message}");}
        bool Current()=>loaded && Practice && grenadeEpoch==epoch && Server.MapName==map &&
            !Utilities.GetPlayers().Any(p=>!p.IsBot) && owner.IsValid && owner.EntityHandle.Raw==ownerHandle &&
            owner.PlayerPawn.Value is {IsValid:true,LifeState:0,AbsOrigin:not null} current && current.EntityHandle.Raw==pawnHandle &&
            Vector3.Distance(NativeWorld.FromNative(current.AbsOrigin),start.Feet)<2 &&
            job is null && flashJob is null;
        void Finish(string? error=null)
        {
            if(stopped)return;stopped=true;search?.Dispose();search=null;
            if(error is not null)Check(false,error);
            if(source.IsValid && source.EntityHandle.Raw==pawnHandle && grenadeEpoch==epoch)
                Check(source.Health==initialHealth && source.ArmorValue==initialArmor && source.FlashDuration==initialBlind &&
                    source.BlindUntilTime==initialUntil,"native HP, armor and flash state unchanged; no real effect was produced");
            flashSearchCheckPending=false;stopFlashSearchCheck=null;
            Say(null,$"FLASHCHECK DONE {(failed==0 ? "PASS" : "FAIL")}: {passed} passed, {failed} failed; native collision/sight plus synthetic observers, not client pixels or real blindness verification.");
        }
        FlashObserver? FindVisibleObserver(Vector3 point)
        {
            foreach(var offset in new[]{new Vector3(96,0,32),new Vector3(0,96,32),new Vector3(-96,0,32),new Vector3(0,-96,32),new Vector3(32,0,96)})
            {
                var eye=point+offset;var direction=point-eye;
                var view=new Aim(-MathF.Atan2(direction.Z,new Vector2(direction.X,direction.Y).Length())*180/MathF.PI,
                    MathF.Atan2(direction.Y,direction.X)*180/MathF.PI,ThrowMode.Left);
                var item=new FlashObserver(1,"合成观察点",eye,view,FlashRole.MustFlash);
                if(FlashSearchSight(source,point,item).Open)return item;
            }
            return null;
        }
        void Step()
        {
            if(stopped)return;
            try
            {
                if(!Current()){Finish("world, zero-human, pawn or fixed-origin guard changed");return;}
                if(Stopwatch.GetElapsedTime(began).TotalSeconds>25){Finish("bounded native diagnostic timed out");return;}
                var watch=Stopwatch.StartNew();
                if(phase==0)
                {
                    while(watch.Elapsed.TotalMilliseconds<.5)
                    {
                        if(fixture is null)
                        {
                            if(fixtureIndex>=fixtureAims.Length){Finish("no clear native throw fixture near the bot; use another safe standing position");return;}
                            fixtureAim=fixtureAims[fixtureIndex++];fixture=new(world,profile,start,fixtureAim);
                        }
                        if(!fixture.Done){fixture.Step();continue;}
                        known=fixture.Result();fixture=null;
                        if(known.Status!=SimulationStatus.Detonated || !Geometry.Finite(known.Endpoint))continue;
                        var found=FindVisibleObserver(known.Endpoint);if(found is null)continue;
                        observer=found.Value;
                        Check(world.TraceCount>0 && known.Status==SimulationStatus.Detonated,"native-collision timed trajectory reaches a finite detonation point");
                        var visible=FlashSearchSight(source,known.Endpoint,observer);
                        Check(visible.Usable && visible.Total==5 && visible.Open,"production five-ray visibility accepts the clear synthetic eye position");
                        var objective=new FlashObjective([observer]);
                        Check(objective.Evaluate(known.Endpoint,observer,visible).GeometryAccepted,"native visibility and facing meet the must-flash geometric screen");
                        var syntheticEntry=new PersonalLineup(Guid.NewGuid(),"native diagnostic angle",GrenadeKind.Flash,start.Feet,start.EyeHeight,
                            fixtureAim.Pitch,fixtureAim.Yaw,"synthetic fixture, not a real recorded throw",known.Endpoint,"diagnostic-only",worldVersion!.ToJsonString(),DateTimeOffset.UtcNow);
                        var collected=PersonalFlashHints.Select([syntheticEntry],profile,start,map,worldVersion.ToJsonString());
                        Check(collected.Count==6 && collected.Any(a=>a==fixtureAim with {Yaw=Geometry.NormalizeYaw(fixtureAim.Yaw)}),"collection hint selection preserves the known native angle across six fixed action hypotheses");
                        search=new(world,profile,start,objective,(point,item)=>FlashSearchSight(source,point,item),
                            new(){MaxCandidates=6,SeedAims=collected,ResultCount=2});
                        phase=1;break;
                    }
                }
                else if(phase is 1 or 2)
                {
                    while(watch.Elapsed.TotalMilliseconds<.5 && !search!.Done)
                    {
                        long before=world.TraceCount,beforeRays=effectRayCount;
                        search.Advance();
                        advanceBounded&=world.TraceCount-before<=4 && effectRayCount-beforeRays<=5 &&
                            (world.TraceCount==before || effectRayCount==beforeRays);
                    }
                    if(search!.Done)
                    {
                        if(phase==1)
                        {
                            var result=search.Result!;
                            Check(result.Matches.Count>0,"known native trajectory can be recovered without a requested detonation target");
                            Check(result.Matches.All(x=>x.CandidateOnly && !x.StrictProtectionVerified && x.GeometryAccepted),"published matches remain prediction-only rather than actual flash claims");
                            Check(search.Start==start && result.Matches.All(x=>x.Candidate.Aim.ForwardSpeed==0 && x.Candidate.Aim.ForwardOffset==0),"search preserves the recorded origin");
                            Check(result.Matches.All(x=>x.Candidate.RobustTrials>1 && x.Candidate.Trajectory.Points.Count>0),"published native candidates include completed perturbation review and drawable trajectory");
                            search.Dispose();
                            var conflict=new FlashObjective([observer,observer with {Id=2,Label="同眼位保护点",Role=FlashRole.Protect}]);
                            search=new(world,profile,start,conflict,(point,item)=>FlashSearchSight(source,point,item),new(){MaxCandidates=1,SeedAims=[fixtureAim]});
                            phase=2;
                        }
                        else
                        {
                            Check(search.Result is {Matches.Count:0} && search.Result.Conclusion.Contains("不代表理论无解"),"one clear eye cannot simultaneously satisfy must-flash and protect constraints");
                            Check(advanceBounded,"each search advancement bounds native collision and eye-ray work independently");
                            search.Dispose();search=null;
                            var candidate=new Candidate(fixtureAim,known!,0);
                            var arm=new Arm(candidate,start,null,0,Server.CurrentTime+10,Physics:profile,EffectObjective:true);
                            Check(arm.Target is null && arm.EffectObjective,"effect recording has no fabricated landing target");
                            var record=new RecordedThrow("diagnostic-only",DateTimeOffset.UtcNow.ToString("O"),map,profile,null,
                                arm,null,Vector3.Zero,Vector3.Zero,Vector3.Zero,known!.Endpoint,known.Time,true,true,0,null,null,[],[],GuidedMatched:true);
                            Check(!record.Success && !record.GuidedLandingSuccess,"even matching synthetic flags cannot label an effect objective as landing success");
                            bool rejected=false;try{SerializeThrow(record);}catch(InvalidOperationException){rejected=true;}
                            Check(rejected,"landing serializer rejects effect-only evidence");
                            int routes=routeLibrary.Count;RememberActual(record);
                            Check(routeLibrary.Count==routes,"effect-only evidence does not populate the landing route library");
                            string menu=PanelText.Menu("按假人找闪",["从当前位置搜索","要闪 / 不闪"],0,flashObjective:true);
                            Check(menu.Contains("F2要求") && !menu.Contains("F2区域") && menu.Contains("WASD移动") && menu.Contains("V飞行") &&
                                Encoding.UTF8.GetByteCount(menu)<=480 && menu.Count(c=>c=='\n')<6,"flash menu displays its own shortcuts within the text budget");
                            Finish();return;
                        }
                    }
                }
                AddTimer(.02f,Step);
            }
            catch(Exception ex){Finish(ex.GetType().Name+": "+ex.Message);}
        }
        flashSearchCheckPending=true;stopFlashSearchCheck=()=>Finish("diagnostic cancelled by lifecycle cleanup");
        Say(null,"FLASHCHECK START: native traces only; existing bot state and player sessions are preserved.");
        AddTimer(.02f,Step);
    });
}
