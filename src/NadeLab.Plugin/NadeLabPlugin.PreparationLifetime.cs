using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    // Call before creating an Arm/Pending record, including direct command paths.
    private void BindPreparation(CCSPlayerController player,Session session)=>SyncPreparationLifetime(player,session);

    private void SyncPreparationLifetime(CCSPlayerController? player,Session session)
    {
        var pawn=player?.IsValid==true && player.SteamID==session.SteamId ? player.PlayerPawn.Value : null;
        bool valid=pawn?.IsValid==true,alive=valid && pawn!.LifeState==0;
        uint? identity=valid ? pawn!.EntityHandle.Raw : null;
        bool changed=session.PreparationLifetime.Observe(grenadeEpoch,identity,alive);
        bool expired=PreparationLifetime.Expired(session.Arm?.Expires,Server.CurrentTime);
        bool transient=session.Arm is not null || session.Pending is not null || session.Snapshot is not null ||
            session.CueStage!=ReleaseStage.None || session.CueUntil!=0 || session.PreparationIssue is not null;
        if(!changed && !expired && (alive || !transient)) return;
        session.Arm=null;session.Pending=null;session.FlightCheck=null;session.Snapshot=null;session.PreparationIssue=null;
        if(changed && player is {IsValid:true} && (job?.Slot==player.Slot || session.Continuation is not null || session.Result is not null))
        {if(job?.Slot==player.Slot) CancelSearch();session.Continuation?.Search.Dispose();session.Continuation=null;session.Result=null;session.Selected=0;}
        session.CueStage=ReleaseStage.None;session.CueUntil=0;session.ReleaseDelay=null;session.LastThrowTime=0;
        session.PreparationRevision++;
        session.HudNotice=null;session.NextHudAt=0;session.LastHudText=null;
    }

    private PlayerActionIdentity ActionIdentity(CCSPlayerController player)
    {
        sessions.TryGetValue(player.Slot,out var session);
        if(session?.SteamId!=player.SteamID) session=null;
        var pawn=player.PlayerPawn.Value;
        return new(grenadeEpoch,player.EntityHandle.Raw,pawn?.IsValid==true ? pawn.EntityHandle.Raw : null,
            player.SteamID,session,session?.Placement);
    }
}
