using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private void TickReleaseCues()
    {
        float now=Server.CurrentTime;
        foreach(var (slot,s) in sessions)
        {
            if(s.Arm is null) {ClearPanelChannel(slot,PanelChannel.Cue);continue;}
            var player=Utilities.GetPlayerFromSlot(slot);
            if(player is not {IsValid:true,IsBot:false} || player.SteamID!=s.SteamId || !s.HudEnabled || HasPanel(player) ||
                s.Pending is not null || s.Arm is not {ObservationOnly:false} arm || now>=arm.Expires || arm.Revision!=s.Revision ||
                !arm.Candidate.Aim.NeedsRunUp || player.PlayerPawn.Value is not {IsValid:true,LifeState:0} pawn ||
                !GroundedUpright(pawn) || pawn.MoveType==MoveType_t.MOVETYPE_NOCLIP || pawn.WeaponServices?.ActiveWeapon.Value is not {IsValid:true} weapon ||
                weapon.DesignerName!=s.Kind.Weapon() || weapon.As<CBaseCSGrenade>().ThrowTime>0)
            {ClearPanelChannel(slot,PanelChannel.Cue);s.NextReleaseCueAt=0;continue;}
            if(now<s.NextReleaseCueAt) continue;
            s.NextReleaseCueAt=now+.1f;
            var aim=arm.Candidate.Aim;
            bool aligned=GroundedUpright(pawn) && MathF.Abs(pawn.EyeAngles.X-aim.Pitch)<=ExecutionEnvelope.Angle &&
                MathF.Abs(Geometry.NormalizeYaw(pawn.EyeAngles.Y-aim.Yaw))<=ExecutionEnvelope.Angle;
            var cue=ReleaseCue.Build(arm.Start,aim,arm.Physics ?? physics,NativeWorld.FromNative(pawn.AbsOrigin!),NativeWorld.FromNative(pawn.AbsVelocity),aligned,arm.Candidate.ReleaseWindow);
            try
            {
                if(PanelResourceInstalled) SendCustomPanelText(player,cue.Text,PanelChannel.Cue);
            }
            catch(Exception)
            {
                // Existing slow notice guidance remains the fallback. Never
                // flood chat or replace menu controls with a live progress bar.
                ClearPanelChannel(slot,PanelChannel.Cue);
            }
        }
    }
}
