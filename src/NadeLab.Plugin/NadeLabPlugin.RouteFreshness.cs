using System.Diagnostics;
using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private sealed record PreparationFlightCheck(PlayerActionIdentity Identity,int Revision,int Preparation,int Selected,ThrowStart Start,
        PhysicsProfile Physics,Target? Target,Candidate Candidate,RouteFreshnessCheck Check,float Since,bool ArmAfter);
    private bool CurrentFlightCheck(CCSPlayerController player,Session s,PreparationFlightCheck check)=>
        check.Identity.Matches(ActionIdentity(player)) && check.Revision==s.Revision && check.Preparation==s.PreparationRevision && check.Selected==s.Selected &&
        ReferenceEquals(s.Result?.Matches.ElementAtOrDefault(s.Selected-1),check.Candidate) && s.Start==check.Start &&
        ReferenceEquals(s.SearchPhysics,check.Physics) &&
        (check.Target is { } target ? s.Target is { } current && current.SameDefinition(target) : s.Target is null);
    private void BeginFlightCheck(CCSPlayerController player,Session s,Candidate candidate,bool armAfter)
    {
        BindPreparation(player,s);
        s.SearchPhysics ??= ProfileFor(s);
        s.FlightCheck=new(ActionIdentity(player),s.Revision,s.PreparationRevision,s.Selected,s.Start!.Value,s.SearchPhysics,s.Target?.Snapshot(),candidate,
            new(new NativeWorld(player.PlayerPawn.Value),s.SearchPhysics,s.Start.Value,candidate,s.Target),Server.CurrentTime,armAfter);
        HudNotice(s,"正在复核当前路线；完成前请勿投掷。",1);
    }
    private void TickPreparedFlights()
    {
        long started=0;
        foreach(var (slot,s) in sessions)
        {
            if(s.FlightCheck is not { } check || check.Check.Done) continue;
            var player=Utilities.GetPlayerFromSlot(slot);
            if(player is not {IsValid:true} || player.PlayerPawn.Value is not {IsValid:true,LifeState:0} || !CurrentFlightCheck(player,s,check))
            {s.FlightCheck=null;continue;}
            if(Server.CurrentTime-check.Since>8)
            {BlockPreparation(player,s,"路线复核超时，请重新就位。");continue;}
            int steps=0;
            if(started==0) started=Stopwatch.GetTimestamp();
            while(Stopwatch.GetElapsedTime(started).TotalMilliseconds<.35 && steps++<16 && check.Check.Advance()) {}
            if(!check.Check.Done) continue;
            if(!check.Check.Accepted)
            {BlockPreparation(player,s,"当前碰撞路线已变化；请选择其他路线或重新搜索。");continue;}
            if(s.Pending is { } pending) s.Pending=pending with {Since=Server.CurrentTime};
            if(check.ArmAfter) ArmVerification(player,s.Selected.ToString(CultureInfo.InvariantCulture));
        }
    }
}
