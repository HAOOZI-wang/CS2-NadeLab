using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private void RecordManualThrow(CCSPlayerController? player)
    {
        if(!Check(player,out var pawn)) return;
        if(pawn.MoveType==MoveType_t.MOVETYPE_NOCLIP) {Say(player,"先从菜单退出飞行并落地，再记录手投。");return;}
        var session=Get(player!);
        if(session.RegionDraft is not null) {OpenRegionEditor(player);return;}
        if(session.Target is null) {Say(player,"先选择落点，再开启自由实投记录。"); return;}
        var start=session.Start ?? new ThrowStart(NativeWorld.FromNative(pawn.AbsOrigin!),pawn.ViewOffset.Z);
        var placeholder=new Candidate(new(0,0,ThrowMode.Left),new(Vector3.Zero,SimulationStatus.Flying,0,0,[]),0);
        BindPreparation(player!,session);
        session.Arm=new(placeholder,start,session.Target.Value,session.Revision,Server.CurrentTime+90,true,ProfileFor(session));
        session.Pending=null; session.FlightCheck=null; session.PreparationIssue=null; session.CueStage=ReleaseStage.None; session.CueUntil=0;
        session.ReleaseDelay=null; session.LastThrowTime=0; session.LastVerified=null; session.SmokeReview=null;
        GiveSelectedGrenade(player!,session.Kind);
        EquipSmoke(player!);
        HudNotice(session,"自由记录90秒：按你认为可行的动作投一颗所选道具。",15);
        Say(player,"已开启自由实投记录；无需搜索成功，可自行跳投/一步投。保存真实动作、初速度、轨迹和落点，用于验证可达性；不会标成模型已验证。");
    }
}
