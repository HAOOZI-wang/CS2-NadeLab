using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Menu;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private sealed class PlacementDraft(uint pawn,int epoch,Vector3 feet,float eyeHeight,Aim view,MoveType_t move,MoveType_t actualMove,float nextAttack,ThrowStart? stableStart,string? weapon)
    {
        public readonly uint Pawn=pawn;
        public readonly int Epoch=epoch;
        public readonly Vector3 ReturnFeet=feet;
        public readonly float ReturnEyeHeight=eyeHeight;
        public readonly Aim ReturnView=view;
        public readonly MoveType_t ReturnMove=move,ReturnActualMove=actualMove;
        public readonly float NextAttack=nextAttack;
        public readonly ThrowStart? StableStart=stableStart;
        public readonly string? Weapon=weapon;
        public PlacementResult Point=new(false,feet,"正在定位");
        public bool Area=true;
        public string? ReturnIssue;
    }

    private void RegisterPlacement()
    {
        AddPlayerCommand("css_nl_place","Select ground target or enter grenade body placement",(p,a)=>{if(p is not null) TargetByKind(p);});
        AddPlayerCommand("css_nl_effect","Toggle reference effect preview",(p,a)=>ToggleEffectPreview(p));
        RegisterPlacementDiagnostic();
    }

    private void TargetByKind(CCSPlayerController player)
    {
        if(!Check(player,out var pawn,allowPlacement:true)) return;
        ClearDummyBatch(player.Slot);
        var s=Get(player);ClosePanel(player);
        if(s.FlashMode) ExitFlashMode(player);
        if(s.Placement is not null) {FinishPlacement(player,false);return;}
        if(s.RegionDraft is not null) {Say(player,"先在目标设置菜单应用或取消范围草稿，再重新选点。");OpenRegionEditor(player);return;}
        if(s.Kind.Timed()) {BeginPlacement(player,pawn,s);return;}
        if(!PickTargetPoint(player,true,out var point,out var normal)) return;
        var placement=EffectPlacement.ResolveGroundHit(new NativeWorld(pawn),s.Kind,point,normal);
        if(!placement.Valid) {Say(player,"该地面不可用："+placement.Reason);return;}
        var target=s.Kind==GrenadeKind.Smoke ? SmokeTargets.Around(placement.Position) :
            new Target(placement.Position,48,new Vector3(48,48,12),s.Kind.Fire() ? "点火落点区域" : "诱饵生效区域");
        Invalidate(player.Slot,s);s.Target=target;s.TargetPreset=target;
        // Changing a target keeps an explicitly recorded throwing origin. A
        // new origin is always requested with Shift+E, never silently inferred.
        SetEffectPreview(player,s.Kind,placement.Position,true,s.EffectEnabled,true);
        int slot=player.Slot;
        var scene=effectPreviewScenes.GetValueOrDefault(slot);int epoch=grenadeEpoch;
        AddTimer(2,()=>
        {
            if(epoch==grenadeEpoch && !s.EffectEnabled && s.Placement is null &&
                effectPreviewScenes.TryGetValue(slot,out var current) && ReferenceEquals(current,scene)) ClearEffectPreview(slot);
        });
        Say(player,$"{s.Kind.Label()}地面目标已确定。"+(s.Start is null ? "到出手位置站稳后按使用键搜索。" : "使用键从原站位搜索；静步+使用换站位。")+"目标设置菜单调整范围。");
    }

    private static void SetMovement(CCSPlayerPawn pawn,MoveType_t move,MoveType_t actual)
    {
        pawn.MoveType=move;pawn.ActualMoveType=actual;
        Utilities.SetStateChanged(pawn,"CBaseEntity","m_MoveType");
    }
    private static float NextAttack(CCSPlayerPawn pawn)=>pawn.WeaponServices?.As<CCSPlayer_WeaponServices>().NextAttack ?? 0;
    private static void SetNextAttack(CCSPlayerPawn pawn,float time)
    {if(pawn.WeaponServices is { } services) services.As<CCSPlayer_WeaponServices>().NextAttack=time;}

    private void TogglePlacement(CCSPlayerController? player)
    {
        if(!Check(player,out var pawn,allowHeldGrenade:true,allowPlacement:true)) return;
        var s=Get(player!);
        if(s.Placement is not null) {FinishPlacement(player!,false);return;}
        BeginPlacement(player!,pawn,s);
    }

    private void BeginPlacement(CCSPlayerController player,CCSPlayerPawn pawn,Session s)
    {
        if(!s.Kind.Timed()) {Say(player,"烟、火和诱饵请准星瞄准地面，按使用键选点。");return;}
        if(ThrowInProgress(player!)) {Say(player,"先完成蓄力/投掷，再从菜单重选目标进入定位。");return;}
        if(s.RegionDraft is not null) {Say(player,"先在目标设置菜单应用或取消区域草稿，再进入定位。");return;}
        if(job?.Slot==player!.Slot) {Say(player,"请等搜索完成，或从菜单取消后再定位。");return;}
        if(pawn.MoveType is not (MoveType_t.MOVETYPE_WALK or MoveType_t.MOVETYPE_NOCLIP)) {Say(player,"当前移动状态无法进入定位，请先回到普通步行或飞行。");return;}
        var feet=NativeWorld.FromNative(pawn.AbsOrigin!);
        if(!PlacementEyeClear(pawn,feet,pawn.ViewOffset.Z)) {Say(player,"当前位置在墙体内，请先飞到空处再从菜单重选目标。");return;}
        ThrowStart? stable=null;
        if(Standing(pawn) && TryStandingPosition(pawn,feet,out var supported) && supported.Z-feet.Z<=3)
            stable=new(feet,pawn.ViewOffset.Z,ThrowOrigin.Preparation);
        if(pawn.MoveType==MoveType_t.MOVETYPE_WALK && stable is null) {Say(player,"请站立停稳后从菜单重选目标；也可以先从菜单开启飞行，再进入定位。");return;}
        var draft=new PlacementDraft(pawn.EntityHandle.Raw,grenadeEpoch,feet,pawn.ViewOffset.Z,new(pawn.EyeAngles.X,pawn.EyeAngles.Y,ThrowMode.Left),pawn.MoveType,pawn.ActualMoveType,NextAttack(pawn),stable,pawn.WeaponServices?.ActiveWeapon.Value?.DesignerName);
        if(!BeginGrenadeBody(player,pawn,s.Kind,out var reason)) {Say(player,"无法进入道具形态："+reason);return;}
        s.Placement=draft;
        s.Arm=null;s.Pending=null;s.FlightCheck=null;s.Snapshot=null;s.CueStage=ReleaseStage.None;s.CueUntil=0;s.PreparationIssue=null;s.PreparationRevision++;
        s.HudNotice=null;s.NextEffectAt=0;s.LastEffectCenter=null;
        ClosePanel(player);ClearPreview(player.Slot);
        SetNextAttack(pawn,Server.CurrentTime+1);
        SetMovement(pawn,MoveType_t.MOVETYPE_NOCLIP,MoveType_t.MOVETYPE_NOCLIP);
        pawn.Teleport(position:feet+Vector3.UnitZ*draft.ReturnEyeHeight,velocity:Vector3.Zero);
        TickGrenadeBodies();
        Say(player,$"已变为{s.Kind.Label()}：当前位置就是爆点。移动定位，使用键确认返回，换弹键取消；定位菜单切换视角、开关预览与假人预测。");
    }

    private static bool PlacementEyeClear(CCSPlayerPawn pawn,Vector3 feet,float eyeHeight)
    {
        var eye=feet+Vector3.UnitZ*eyeHeight;
        return Geometry.Finite(eye) && !new NativeWorld(pawn).Sweep(eye,eye,2).Hit;
    }

    private PlacementResult PlacementPoint(CCSPlayerPawn pawn,GrenadeKind kind)
    {
        if(kind.Timed()) return EffectPlacement.Resolve(new NativeWorld(pawn),kind,NativeWorld.FromNative(pawn.AbsOrigin!));
        var eye=NativeWorld.FromNative(pawn.AbsOrigin!)+Vector3.UnitZ*pawn.ViewOffset.Z;
        var desired=eye+Geometry.Direction(pawn.EyeAngles.X,pawn.EyeAngles.Y)*48;
        var world=new NativeWorld(pawn);
        var forward=world.Sweep(eye,desired,2);
        if(forward.StartSolid) return new(false,eye,"视角在实体内，请移出墙体");
        // A forward hull sweep prevents the marker from jumping across a thin wall.
        if(forward.Hit) desired=forward.Position+forward.Normal*.125f;
        return EffectPlacement.Resolve(world,kind,desired);
    }

    private bool SamePlacementPawn(CCSPlayerController p,PlacementDraft draft,out CCSPlayerPawn pawn)
    {
        pawn=null!;
        if(draft.Epoch!=grenadeEpoch || p.PlayerPawn.Value is not {IsValid:true,LifeState:0} current || current.EntityHandle.Raw!=draft.Pawn) return false;
        pawn=current;return true;
    }

    private bool ReturnFromPlacement(CCSPlayerController p,PlacementDraft draft,out string? issue)
    {
        issue=null;
        if(!SamePlacementPawn(p,draft,out var pawn)) {issue="角色或地图已变化，定位已取消";return false;}
        var destination=draft.ReturnFeet;
        if(draft.ReturnMove==MoveType_t.MOVETYPE_WALK && !TryStandingPosition(pawn,destination,out destination))
        {issue="原位置已被占用，无法安全返回；请移开障碍后再按使用键/换弹键";return false;}
        if(draft.ReturnMove==MoveType_t.MOVETYPE_NOCLIP && !PlacementEyeClear(pawn,destination,draft.ReturnEyeHeight))
        {issue="原飞行位置位于实体内，暂时无法返回";return false;}
        RestoreGrenadeBody(p.Slot);
        pawn.Teleport(position:destination,velocity:Vector3.Zero);
        SetMovement(pawn,draft.ReturnMove,draft.ReturnActualMove);
        AlignView(pawn,draft.ReturnView);
        RestorePlacementWeapon(p,pawn,draft);
        return true;
    }

    private static void RestorePlacementWeapon(CCSPlayerController p,CCSPlayerPawn pawn,PlacementDraft draft)
    {
        SetNextAttack(pawn,MathF.Max(draft.NextAttack,Server.CurrentTime+.2f));
        if(draft.Weapon is { } weapon && weapon.StartsWith("weapon_",StringComparison.Ordinal))
        {
            var grenade=Enum.GetValues<GrenadeKind>().Where(k=>k.Weapon()==weapon).Cast<GrenadeKind?>().FirstOrDefault();
            p.ExecuteClientCommand(grenade is { } kind ? kind.Slot() : weapon.Contains("knife",StringComparison.Ordinal) ? "slot3" : "use "+weapon);
        }
    }

    private void FinishPlacement(CCSPlayerController player,bool commit)
    {
        if(!Check(player,out var pawn,allowHeldGrenade:true,allowPlacement:true)) return;
        var s=Get(player);if(s.Placement is not { } draft) return;
        CompletePlacement(player,pawn,s,commit);
    }

    private void CompletePlacement(CCSPlayerController player,CCSPlayerPawn pawn,Session s,bool commit)
    {
        if(s.Placement is not { } draft) return;
        if(!SamePlacementPawn(player,draft,out _)) {AbandonPlacement(player.Slot,s);return;}
        if(commit)
        {
            draft.Point=PlacementPoint(pawn,s.Kind);
            if(!draft.Point.Valid) {Say(player,"无法确认："+draft.Point.Reason);return;}
            if(!EffectMarkerReady(player.Slot,s.Kind,draft.Point))
            {s.NextEffectAt=0;s.LastEffectCenter=null;Say(player,"定位十字正在更新，请停稳后再按使用键确认。");return;}
        }
        if(!ReturnFromPlacement(player,draft,out var issue))
        {
            if(commit || !PlacementEyeClear(pawn,NativeWorld.FromNative(pawn.AbsOrigin!),pawn.ViewOffset.Z))
            {draft.ReturnIssue=issue;Say(player,issue+"；换弹键可在当前空处取消退出。");return;}
            // A door/prop can occupy the saved position. Always provide a cancel
            // exit, preserving the old target and keeping flight at a free eye.
            var bodyCenter=NativeWorld.FromNative(pawn.AbsOrigin!);
            RestoreGrenadeBody(player.Slot);
            SetMovement(pawn,MoveType_t.MOVETYPE_NOCLIP,MoveType_t.MOVETYPE_NOCLIP);
            pawn.Teleport(position:bodyCenter-new Vector3(pawn.ViewOffset.X,pawn.ViewOffset.Y,pawn.ViewOffset.Z),velocity:Vector3.Zero);RestorePlacementWeapon(player,pawn,draft);
            s.Placement=null;RemoveDummyPredictions(player.Slot);ClearEffectPreview(player.Slot);s.LastEffectCenter=null;s.NextEffectAt=0;
            Say(player,"原位置被占用：已在当前位置取消，保留飞行与原目标。从菜单退出飞行返回步行。");return;
        }
        s.Placement=null;RemoveDummyPredictions(player.Slot);ClearEffectPreview(player.Slot);s.NextEffectAt=0;s.LastEffectCenter=null;
        if(commit)
        {
            Invalidate(player.Slot,s);
            s.Target=new(draft.Point.Position,48,new Vector3(48,48,s.Kind.Timed() ? 48 : 12),s.Kind.Timed() ? "三维爆点区域" : "地面生效区域",Airspace:s.Kind.Timed());
            s.TargetPreset=s.Target;
            if(draft.StableStart is { } start) {s.Start=start;s.RequestedStart=start;s.ReferenceView=draft.ReturnView;}
            Say(player,"目标已确认，已返回原位置。"+(draft.StableStart is not null ? "使用进入定位前的站位，按使用键搜索。" :
                s.Start is null ? "尚无出手站位；从菜单退出飞行，落地站稳后按使用键记录并搜索。" : "保留原搜索站位；从菜单退出飞行，落地后按使用键搜索，静步+使用改用当前位置。"));
        }
        else {Say(player,"已取消定位并返回；原目标、站位与方案保留。使用键可重新准备手投。");RefreshSelectedGuides(player);}
    }

    private void AbandonPlacement(int slot,Session s)
    {
        RestoreGrenadeBody(slot);
        var p=Utilities.GetPlayerFromSlot(slot);
        if(s.Placement is { } draft && p?.IsValid==true && p.SteamID==s.SteamId && SamePlacementPawn(p,draft,out var pawn))
        {SetNextAttack(pawn,MathF.Max(draft.NextAttack,Server.CurrentTime+.2f));}
        s.Placement=null;RemoveDummyPredictions(slot);ClearEffectPreview(slot);s.LastEffectCenter=null;
    }
    private void RestorePlacements()
    {
        foreach(var (slot,s) in sessions)
        {
            var p=Utilities.GetPlayerFromSlot(slot);
            if(s.Placement is { } draft && p?.IsValid==true && p.SteamID==s.SteamId && SamePlacementPawn(p,draft,out var pawn) &&
                !ReturnFromPlacement(p,draft,out _))
            {
                var eye=NativeWorld.FromNative(pawn.AbsOrigin!)+new Vector3(pawn.ViewOffset.X,pawn.ViewOffset.Y,pawn.ViewOffset.Z);
                RestoreGrenadeBody(slot);
                SetMovement(pawn,MoveType_t.MOVETYPE_NOCLIP,MoveType_t.MOVETYPE_NOCLIP);
                pawn.Teleport(position:eye-new Vector3(pawn.ViewOffset.X,pawn.ViewOffset.Y,pawn.ViewOffset.Z),velocity:Vector3.Zero);RestorePlacementWeapon(p,pawn,draft);
            }
            AbandonPlacement(slot,s);
        }
    }

    private void ToggleEffectPreview(CCSPlayerController? p)
    {
        if(!Check(p,out _,allowPlacement:true)) return;
        ToggleEffectPreviewCore(p!);
    }

    private void ToggleEffectPreviewCore(CCSPlayerController p)
    {
        var s=Get(p);
        if(s.FlashMode) {ShowFlashDetails(p);return;}
        if(s.Kind.Timed())
        {
            if(s.Placement is not { } timedDraft) {Say(p,"先从菜单重选目标变为道具，再在定位菜单开关假人效果预测。");return;}
            timedDraft.Area=!timedDraft.Area;s.LastEffectCenter=null;s.NextEffectAt=0;RemoveDummyPredictions(p!.Slot);
            Say(p,timedDraft.Area ? "已开启预览：爆点轻微闪动，假人栏显示实时粗估；不产生实际伤害或白屏。" : "预览已关闭；基本定位十字保留。");return;
        }
        if(!EffectReference.HasAreaPreview(s.Kind))
        {
            s.EffectEnabled=false;s.NextEffectAt=0;s.LastEffectCenter=null;
            if(s.Placement is { } unsupportedDraft) unsupportedDraft.Area=false;
            else ClearEffectPreview(p!.Slot);
            Say(p,EffectReference.Label(s.Kind));return;
        }
        bool enabled;
        if(s.Placement is { } draft) enabled=draft.Area=!draft.Area;
        else enabled=s.EffectEnabled=!s.EffectEnabled;
        s.NextEffectAt=0;s.LastEffectCenter=null;
        if(!enabled && s.Placement is null) ClearEffectPreview(p!.Slot);
        Say(p,enabled ? EffectReference.Label(s.Kind)+"；提示框显示当前参考位置。" : "效果范围已关闭。"+(s.Placement is null ? "" : "定位十字保留，使用键确认 / 换弹键取消。"));
    }

    private string? EffectPreviewCaption(CCSPlayerController player,Session s)
    {
        if(s.Kind.Timed() && s.Placement is { } timed)
            return timed.Area ? "爆点脉冲仅作预览；假人栏显示效果粗估（低置信、未校准）。" : "效果预览已关闭；可开启预览，切换视角观察。";
        if(s.RegionDraft is not null || ShowingTargetRange(player.Slot))
            return "蓝线：允许落点/爆点；红线：排除区。效果参考暂时隐藏。";
        if(!effectPreviewScenes.TryGetValue(player.Slot,out var scene) || scene.Controller!=player.EntityHandle.Raw || scene.SteamId!=s.SteamId || !scene.Area) return null;
        if(!scene.Valid) return "当前位置不可用，请移出墙体或瞄准可用地面。";
        if(scene.FireCells.Length>0) return "绿色十字：最新实测火格（仅采样点，不代表完整火区）。";
        string source=s.Placement is not null ? "当前道具位置" :
            s.Result?.Matches.ElementAtOrDefault(s.Selected-1) is { } candidate && Vector3.Distance(candidate.Trajectory.Endpoint,scene.Center)<2 ? "当前投法生效点" :
            s.Target is { } target && Vector3.Distance(target.Position,scene.Center)<2 ? "目标中心" : "准星位置";
        string legend=s.Kind switch
        {
            GrenadeKind.Smoke=>"灰虚线：烟体参考",
            GrenadeKind.Molotov or GrenadeKind.Incendiary=>"橙短线：扩散参考",
            _=>"效果参考"
        };
        return $"{source} · {legend}；实际效果需实投确认。";
    }

    private void TickPlacementAndEffects()
    {
        float now=Server.CurrentTime;
        foreach(var (slot,s) in sessions)
        {
            if(s.RegionDraft is not null)
            {ClearEffectPreview(slot);s.LastEffectCenter=null;s.NextEffectAt=0;continue;}
            if((s.Kind.Timed() || !EffectReference.HasAreaPreview(s.Kind)) && s.Placement is null)
            {
                // A just-picked decoy retains its two-second point marker;
                // there is deliberately no decoy effect-range toggle.
                if(effectPreviewScenes.TryGetValue(slot,out var markerScene) && !markerScene.Area && now-markerScene.Refreshed<2) continue;
                ClearEffectPreview(slot);s.LastEffectCenter=null;continue;
            }
            if(s.Placement is null && !s.EffectEnabled) continue;
            var p=Utilities.GetPlayerFromSlot(slot);
            if(p?.IsValid!=true || p.SteamID!=s.SteamId || p.PlayerPawn.Value is not {IsValid:true,LifeState:0} pawn)
            {AbandonPlacement(slot,s);continue;}
            if(s.Placement is { } active)
            {
                if(!SamePlacementPawn(p,active,out _)) {AbandonPlacement(slot,s);continue;}
                SetNextAttack(pawn,now+1);
            }
            else if(s.Pending is not null || ThrowInProgress(p)) {ClearEffectPreview(slot);s.LastEffectCenter=null;continue;}
            if(now<s.NextEffectAt) continue;
            s.NextEffectAt=now+(job is null ? .333f : .75f);
            Vector3 center;bool valid,marker,area;IReadOnlyList<Vector3>? actual=null;
            if(s.Placement is { } draft)
            {draft.Point=PlacementPoint(pawn,s.Kind);center=draft.Point.Position;valid=draft.Point.Valid;marker=true;area=draft.Area;}
            else
            {
                marker=false;area=true;valid=true;
                if(s.Kind.Fire() && s.FirePreviewCells is {Length:>0} && now-s.FirePreviewAt<1.2f && s.FirePreviewRevision==s.Revision && s.FirePreviewSequence==s.ThrowSequence)
                    actual=s.FirePreviewCells;
                // Measurements and estimates are exclusive scenes. Real fire
                // samples must keep their own origin when another route is selected.
                if(actual is not null) center=s.FirePreviewCenter;
                else if(s.Result?.Matches.ElementAtOrDefault(s.Selected-1) is { } candidate) center=candidate.Trajectory.Endpoint;
                else if(s.Target is { } target) center=target.Position;
                else
                {
                    var eye=NativeWorld.FromNative(pawn.AbsOrigin!)+Vector3.UnitZ*pawn.ViewOffset.Z;
                    var world=new NativeWorld(pawn);var hit=world.Sweep(eye,eye+Geometry.Direction(pawn.EyeAngles.X,pawn.EyeAngles.Y)*2048,2);
                    if(!hit.Hit || hit.StartSolid) {ClearEffectPreview(slot);continue;}
                    var point=EffectPlacement.Resolve(world,s.Kind,hit.Position+hit.Normal*.125f);
                    center=point.Position;valid=point.Valid;marker=true;
                }
            }
            // Refresh static scenes periodically for doors/props; moving scenes
            // only need new geometry after a meaningful position change.
            if(s.LastEffectCenter is { } previous && Vector3.Distance(previous,center)<2 && valid==s.LastEffectValid && area==s.LastEffectArea && actual is null && now<s.LastEffectBuildAt+1) continue;
            s.LastEffectCenter=center;s.LastEffectValid=valid;s.LastEffectArea=area;s.LastEffectBuildAt=now;
            // Timed grenades use the existing centre marker for a soft pulse
            // plus per-dummy estimates. No opaque light field or damage rings.
            SetEffectPreview(p,s.Kind,center,valid,area && !s.Kind.Timed(),marker,actual,pulse:s.Kind.Timed() && area);
        }
    }

    private void RememberFirePreview(GrenadeEffect? effect,Vector3[] cells)
    {
        if(effect is null || !sessions.TryGetValue(effect.Slot,out var s) || s.SteamId!=effect.Steam || s.Revision!=effect.Revision || s.ThrowSequence!=effect.Sequence) return;
        s.FirePreviewCells=cells;s.FirePreviewCenter=effect.Position;s.FirePreviewAt=Server.CurrentTime;
        s.FirePreviewRevision=effect.Revision;s.FirePreviewSequence=effect.Sequence;
    }
}
