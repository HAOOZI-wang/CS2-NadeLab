using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private sealed class RecentImpactCheck(ImpactOwner owner,GrenadeKind kind)
    {
        public readonly ImpactOwner Owner=owner;
        public readonly GrenadeKind Kind=kind;
        public Vector3? EventPoint;
    }
    private RecentImpactCheck? recentImpactCheck;

    private void RegisterRecentImpactDiagnostic()
    {
        void Observe(CCSPlayerController? owner,GrenadeKind kind,Vector3 point)
        {if(owner is {IsValid:true} && recentImpactCheck is { } check && check.Kind==kind && check.Owner==ImpactIdentity(owner)) check.EventPoint=point;}
        RegisterEventHandler<EventFlashbangDetonate>((e,i)=>{Observe(e.Userid,GrenadeKind.Flash,new(e.X,e.Y,e.Z));return HookResult.Continue;});
        RegisterEventHandler<EventHegrenadeDetonate>((e,i)=>{Observe(e.Userid,GrenadeKind.HighExplosive,new(e.X,e.Y,e.Z));return HookResult.Continue;});
        AddCommand("css_nl_recentcheck","Empty-server real unarmed Flash/HE impact check",(caller,command)=>
        {
            if(caller is not null || !loaded || !Practice || recentImpactCheck is not null || job is not null || flashJob is not null || flashSearchCheckPending || flashVerificationCheckPending || DummyLayoutLoadActive ||
                Utilities.GetPlayers().Any(p=>!p.IsBot) || flights.Count>0 || grenadeFlights.Count>0)
            {Say(caller,"RECENTCHECK refused: idle empty practice server required.");return;}
            var kind=command.GetArg(1).Equals("he",StringComparison.OrdinalIgnoreCase) ? GrenadeKind.HighExplosive : GrenadeKind.Flash;
            var owner=Utilities.GetPlayers().FirstOrDefault(p=>p is {IsValid:true,IsBot:true} && p.PlayerPawn.Value is {IsValid:true,LifeState:0} && FindDummy(p) is null);
            if(owner is null) {Say(null,"RECENTCHECK refused: one living ordinary bot required.");return;}
            BeginRecentImpactCheck(owner,kind);
        });
    }

    private void BeginRecentImpactCheck(CCSPlayerController owner,GrenadeKind kind)
    {
        var pawn=owner.PlayerPawn.Value!;var identity=ImpactIdentity(owner);uint pawnHandle=pawn.EntityHandle.Raw;
        var check=new RecentImpactCheck(identity,kind);recentImpactCheck=check;
        sessions.TryGetValue(owner.Slot,out var previous);
        if(previous?.Placement is not null) {recentImpactCheck=null;Say(null,"RECENTCHECK refused: owner is in placement.");return;}
        var feet=NativeWorld.FromNative(pawn.AbsOrigin!);var velocity=NativeWorld.FromNative(pawn.AbsVelocity);
        var view=new Vector3(pawn.EyeAngles.X,pawn.EyeAngles.Y,0);
        var move=pawn.MoveType;var actual=pawn.ActualMoveType;bool active=pawn.BotAllowActive,takesDamage=pawn.TakesDamage;
        float nextAttack=NextAttack(pawn);uint activeWeapon=pawn.WeaponServices?.ActiveWeapon.Raw ?? 0;
        string? stop=ConVar.Find("bot_stop")?.StringValue;
        var held=pawn.WeaponServices?.MyWeapons.Select(h=>h.Raw).ToHashSet() ?? [];
        var session=new Session {SteamId=owner.SteamID,Kind=GrenadeKind.Smoke,Start=new(feet),RequestedStart=new(feet),ReferenceView=new(0,17,ThrowMode.Left)};
        sessions[owner.Slot]=session;RemoveRecentImpacts(owner.Slot);
        int passed=0;bool ended=false;
        void Require(bool condition,string detail) {if(!condition) throw new InvalidOperationException(detail);passed++;Say(null,"RECENTCHECK PASS: "+detail);}
        void End(string result)
        {
            if(ended) return;ended=true;if(ReferenceEquals(recentImpactCheck,check)) recentImpactCheck=null;
            if(grenadeEpoch==identity.Epoch)
            {
                if(stop is not null && ConVar.Find("bot_stop") is { } cvar) cvar.StringValue=stop;
                if(sessions.TryGetValue(owner.Slot,out var currentSession) && ReferenceEquals(currentSession,session))
                {
                    ClearPreview(owner.Slot);RemoveRecentImpacts(owner.Slot);
                    if(previous is null) sessions.Remove(owner.Slot);else sessions[owner.Slot]=previous;
                }
                if(owner.IsValid && owner.EntityHandle.Raw==identity.Controller && pawn.IsValid && pawn.EntityHandle.Raw==pawnHandle)
                {
                    pawn.Teleport(position:feet,angles:view,velocity:velocity);SetMovement(pawn,move,actual);
                    pawn.BotAllowActive=active;if(pawn.Bot is { } bot) bot.AllowActive=active;
                    pawn.TakesDamage=takesDamage;SetNextAttack(pawn,nextAttack);
                    foreach(var weapon in pawn.WeaponServices?.MyWeapons.Select(h=>h.Value).Where(w=>w is {IsValid:true} && !held.Contains(w.EntityHandle.Raw)).ToArray() ?? []) weapon!.Remove();
                    if(pawn.WeaponServices is { } ws && held.Contains(activeWeapon)) ws.ActiveWeapon.Raw=activeWeapon;
                }
            }
            Say(null,$"RECENTCHECK DONE {result}; kind={kind}; passed={passed}.");
        }
        bool Current()=>loaded && Practice && grenadeEpoch==identity.Epoch && owner.IsValid && owner.EntityHandle.Raw==identity.Controller &&
            pawn.IsValid && pawn.EntityHandle.Raw==pawnHandle && !Utilities.GetPlayers().Any(p=>!p.IsBot);
        try
        {
            if(ConVar.Find("bot_stop") is { } cvar) cvar.StringValue="1";
            pawn.TakesDamage=false;pawn.BotAllowActive=true;if(pawn.Bot is { } bot) bot.AllowActive=true;
            SetMovement(pawn,MoveType_t.MOVETYPE_WALK,MoveType_t.MOVETYPE_WALK);SetNextAttack(pawn,0);
            owner.GiveNamedItem(kind.Weapon());
            AddTimer(.25f,()=>
            {
                try
                {
                    if(!Current()) {End("CANCELLED");return;}
                    var weapon=pawn.WeaponServices?.MyWeapons.Select(h=>h.Value).FirstOrDefault(w=>w is {IsValid:true} && w.DesignerName==kind.Weapon())?.As<CBaseCSGrenade>();
                    Require(weapon is not null,"native grenade weapon available");pawn.WeaponServices!.ActiveWeapon.Raw=weapon!.EntityHandle.Raw;
                    AddTimer(1,()=>
                    {
                        try
                        {
                            if(!Current() || !weapon.IsValid) {End("CANCELLED");return;}
                            Require(session.Arm is null && session.Kind==GrenadeKind.Smoke,"throw is unarmed and selected menu type differs");
                            weapon.PinPulled=false;weapon.Redraw=false;weapon.ThrowStrength=0;weapon.IsHeldByPlayer=true;weapon.ThrowTime=Server.CurrentTime+.1f;
                            AddTimer(3,()=>
                            {
                                try
                                {
                                    if(!Current()) {End("CANCELLED");return;}
                                    Require(check.EventPoint is not null,"independent native detonation event observed");
                                    var impact=FindRecentImpact(owner,kind);
                                    Require(impact is not null && !impact.Historical && impact.Record.Position==check.EventPoint,"unrecorded real event captured with exact position");
                                    Require(impact!.Owner==identity && FindRecentImpact(owner,GrenadeKind.Decoy) is null,"owner and type isolation retained");
                                    session.Kind=kind;var start=session.Start;var requested=session.RequestedStart;var reference=session.ReferenceView;
                                    AdoptRecentImpactCore(owner,session,impact);
                                    Require(session.Target is {Airspace:true,IsArea:true} target && target.Position==check.EventPoint,"adoption keeps the exact airborne event centre");
                                    Require(session.Start==start && session.RequestedStart==requested && session.ReferenceView==reference,"adoption preserves existing throw origin and reference view");
                                    Require(session.LastVerified is null && session.Result is null && session.RegionDraft is null,"adoption clears old result and does not mark verified");
                                    session.Start=null;session.RequestedStart=null;AdoptRecentImpactCore(owner,session,impact);
                                    Require(session.Start is null && session.RequestedStart is null,"no origin is silently inferred from impact or observer location");
                                    RemoveRecentImpacts(owner.Slot);
                                    Require(FindRecentImpact(owner,kind) is null,"disconnect cleanup removes bot impact without history fallback");
                                    End("PASS");
                                }
                                catch(Exception ex) {End("FAIL: "+ex.Message);}
                            });
                        }
                        catch(Exception ex) {End("FAIL: "+ex.Message);}
                    });
                }
                catch(Exception ex) {End("FAIL: "+ex.Message);}
            });
        }
        catch(Exception ex) {End("FAIL: "+ex.Message);}
    }
}
