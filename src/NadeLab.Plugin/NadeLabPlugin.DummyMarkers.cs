using System.Drawing;
using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using NadeLab.Core;
using CsTrace=CounterStrikeSharp.API.Modules.Utils.Trace;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private EffectDummy[] OwnedDummies(CCSPlayerController owner)=>effectDummies.Values
        .Where(d=>d.Owner==owner.EntityHandle.Raw && d.OwnerSteam==owner.SteamID && d.Live && !d.Removing).OrderBy(d=>d.LabelIndex).ToArray();

    private void RegisterDummyManagement()
    {
        AddPlayerCommand("css_nl_dummy_select","Select your crosshair target, or A through H",(p,args)=>
        {
            if(args.Length==0) {SelectDummyByAim(p);return;}
            if(!Check(p,out _,allowPlacement:true)) return;
            string token=args[0].ToUpperInvariant();int index=token.Length==1 ? token[0]-'A' : -1;
            var target=OwnedDummies(p!).FirstOrDefault(d=>d.LabelIndex==index);
            if(target is null) {Say(p,"该字母没有你的假人；准星瞄准目标，或从假人列表选择。");return;}
            SelectDummy(p!,target);
        });
        AddPlayerCommand("css_nl_dummy_reset","Reset only your selected dummy",(p,args)=>ResetSelectedDummy(p));
        AddPlayerCommand("css_nl_dummies_reset","Reset all of your dummies",(p,args)=>{if(p is not null) ResetOwnedDummies(p);});
        AddPlayerCommand("css_nl_dummy_remove","Remove only your selected dummy",(p,args)=>RemoveSelectedDummy(p));
        RegisterListener<Listeners.CheckTransmit>(infos=>
        {
            foreach(var (info,viewer) in infos)
                foreach(var target in effectDummies.Values)
                {
                    bool owner=viewer is {IsValid:true} && viewer.EntityHandle.Raw==target.Owner && viewer.SteamID==target.OwnerSteam;
                    if(DummyMarker(target) is { } marker && !owner) info.TransmitEntities.Remove(marker);
                    // Observe only: a native opacity check cannot prove that the
                    // owner's outgoing snapshot even contains the player body.
                    if(owner && target.Live && !target.Removing && target.Bot.PlayerPawn.Value is {IsValid:true} pawn)
                    {
                        if(target.OwnerTransmitPawn!=pawn.EntityHandle.Raw)
                        {target.OwnerTransmitPawn=pawn.EntityHandle.Raw;target.OwnerTransmitSamples=0;target.OwnerTransmitIncluded=0;}
                        target.OwnerTransmitSamples++;
                        if(info.TransmitEntities.Contains(pawn)) target.OwnerTransmitIncluded++;
                        target.OwnerTransmitSampleTime=Server.CurrentTime;
                    }
                }
        });
    }
    private bool DummyOwnedBy(CCSPlayerController owner,EffectDummy target)=>owner.IsValid && target.Live && !target.Removing &&
        target.Owner==owner.EntityHandle.Raw && target.OwnerSteam==owner.SteamID && effectDummies.TryGetValue(target.Handle,out var current) && ReferenceEquals(current,target);
    private void SelectDummy(CCSPlayerController owner,EffectDummy target)
    {
        if(!DummyOwnedBy(owner,target)) {Say(owner,"这个假人已不存在，请重新选择。");return;}
        selectedDummies[owner.EntityHandle.Raw]=target.Handle;
        foreach(var d in OwnedDummies(owner)) UpdateDummyMarker(d);
        Say(owner,$"已选择{DummyLabel(target)}；现场方括号与右侧箭头对应，可单独复位或删除。");
    }
    private void SelectDummyByAim(CCSPlayerController? owner)
    {
        if(!Check(owner,out var pawn,allowPlacement:true)) return;
        var eye=NativeWorld.FromNative(pawn.AbsOrigin!)+new Vector3(pawn.ViewOffset.X,pawn.ViewOffset.Y,pawn.ViewOffset.Z);
        float pitch=pawn.EyeAngles.X,yaw=pawn.EyeAngles.Y;
        if(grenadeBodies.TryGetValue(owner!.Slot,out var body) && SameGrenadeBody(owner,pawn,body) && BodyCamera(body) is {IsValid:true,AbsOrigin:not null,AbsRotation:not null} camera)
        {eye=NativeWorld.FromNative(camera.AbsOrigin);pitch=camera.AbsRotation.X;yaw=camera.AbsRotation.Y;}
        var end=eye+Geometry.Direction(pitch,yaw)*4096;
        var hit=CsTrace.TraceEndShape(NativeWorld.ToNative(eye),NativeWorld.ToNative(end),pawn,new TraceOptions {InteractsAs=Contents.Player,InteractsWith=Masks.PlayerSolid|Contents.Hitbox|Contents.PhysicsProp});
        var entity=hit.HitEntity();
        var target=entity.IsValid ? OwnedDummies(owner!).FirstOrDefault(d=>d.Bot.PlayerPawn.Raw==entity.EntityHandle.Raw) : null;
        if(target is null) {Say(owner,"准星对准自己的假人身体再选；死亡目标也可从A-H列表选择。");return;}
        SelectDummy(owner!,target);
    }
    private EffectDummy? DummyActionTarget(CCSPlayerController owner,uint? expected=null)
    {
        var selected=SelectedDummy(owner);
        return selected is not null && DummyOwnedBy(owner,selected) && (expected is null || selected.Handle==expected) ? selected : null;
    }
    private void ResetSelectedDummy(CCSPlayerController? owner,uint? expected=null)
    {
        if(!Check(owner,out _,allowPlacement:true)) return;
        var selected=DummyActionTarget(owner!,expected);
        if(selected is null) {Say(owner,"所选假人已变化；重新用准星或A-H列表选择后再操作。");return;}
        ResetDummy(selected);Say(owner,$"{DummyLabel(selected)}已请求复位；保持原站位，清空该目标的实测记录。");
    }
    private void RemoveSelectedDummy(CCSPlayerController? owner,uint? expected=null)
    {
        if(!Check(owner,out _,allowPlacement:true)) return;
        var selected=DummyActionTarget(owner!,expected);
        if(selected is null) {Say(owner,"所选假人已变化；重新用准星或A-H列表选择后再操作。");return;}
        RemoveDummy(selected);Say(owner,$"正在删除{DummyLabel(selected)}，其余目标保留。");
    }
    private void ConfirmDummyRemoval(CCSPlayerController owner,uint? expected)
    {
        if(!Check(owner,out _,allowPlacement:true)) return;
        var chosen=expected is null ? null : DummyActionTarget(owner,expected);
        if(expected is not null && chosen is null) {Say(owner,"所选假人已变化，请重新选择。");return;}
        var targets=expected is null ? OwnedDummies(owner) : new[]{chosen!};
        var menu=NewPanel(expected is null ? $"删除我的{targets.Length}个假人？" : $"删除{DummyLabel(chosen!)}？");
        menu.AllowPlacement=true;menu.RememberSelection=false;menu.Back=OpenEffectTarget;
        menu.AddMenuOption("取消，保留假人",(who,option)=>QueueAction(who,OpenEffectTarget));
        menu.AddMenuOption("确认删除",(who,option)=>QueueAction(who,current=>
        {
            if(current is null || !Check(current,out _,allowPlacement:true)) return;
            if(expected is not null && DummyActionTarget(current,expected) is null) {Say(current,"所选假人已变化，请重新选择。");return;}
            foreach(var target in targets) if(DummyOwnedBy(current,target)) RemoveDummy(target);
            Say(current,"已请求删除所确认的假人，其他假人保留。");OpenEffectTarget(current);
        }));
        menu.Open(owner);
    }
    private void OpenDummySelection(CCSPlayerController? owner)
    {
        if(!Check(owner,out _,allowPlacement:true)) return;
        var menu=NewPanel("选择假人 · 字母对应现场");menu.Back=who=>OpenEffectTarget(who);menu.AllowPlacement=true;
        foreach(var target in OwnedDummies(owner!))
        {
            uint handle=target.Handle;uint ownerHandle=owner!.EntityHandle.Raw;
            menu.AddMenuOption(DummyStatus(target),(who,option)=>QueueAction(who,current=>
            {
                if(!Check(current,out _,allowPlacement:true) || current!.EntityHandle.Raw!=ownerHandle || !effectDummies.TryGetValue(handle,out var same) || !DummyOwnedBy(current,same)) return;
                SelectDummy(current,same);OpenEffectTarget(current);
            }));
        }
        menu.AddMenuOption("准星选择我的假人",(who,option)=>QueueAction(who,SelectDummyByAim));
        menu.AddMenuOption("返回假人管理",(who,option)=>QueueAction(who,OpenEffectTarget));
        menu.Open(owner!);
    }
    private static CPointWorldText? DummyMarker(EffectDummy target)=>target.Marker is {IsValid:true} marker && marker.EntityHandle.Raw==target.MarkerHandle ? marker : null;
    private void UpdateDummyMarker(EffectDummy target)
    {
        if(!target.Live || target.Removing || !target.Ready) return;
        try
        {
            var marker=DummyMarker(target);
            if(marker is null)
            {
                if(Server.CurrentTime<target.NextMarkerRepair) return;
                target.NextMarkerRepair=Server.CurrentTime+3;
                marker=Utilities.CreateEntityByName<CPointWorldText>("point_worldtext");
                if(marker is null) return;
                target.Marker=marker;target.MarkerHandle=marker.EntityHandle.Raw;
                using(var keys=new CEntityKeyValues())
                {
                    keys.SetString("message",((char)('A'+target.LabelIndex)).ToString());
                    keys.SetString("font_name","Arial Black");keys.SetString("color",$"{DummyColors[target.LabelIndex].R} {DummyColors[target.LabelIndex].G} {DummyColors[target.LabelIndex].B} 255");
                    keys.SetInt("enabled",1);keys.SetInt("fullbright",1);keys.SetInt("font_size",36);
                    keys.SetFloat("world_units_per_pixel",.3f);keys.SetInt("justify_horizontal",1);keys.SetInt("justify_vertical",1);keys.SetInt("reorient_mode",1);
                    marker.DispatchSpawn(keys);
                }
                marker=DummyMarker(target);if(marker is null) return;
                target.MarkerText=null;target.MarkerPosition=null;
            }
            bool selected=selectedDummies.GetValueOrDefault(target.Owner)==target.Handle;
            string text=selected ? $"[{(char)('A'+target.LabelIndex)}]" : ((char)('A'+target.LabelIndex)).ToString();
            if(text!=target.MarkerText)
            {marker.AcceptInput("SetMessage",value:text);target.MarkerText=text;}
            var position=target.PositionedFeet+Vector3.UnitZ*88;
            if(position!=target.MarkerPosition)
            {marker.Teleport(position:position,angles:Vector3.Zero,velocity:Vector3.Zero);target.MarkerPosition=position;}
        }
        catch(Exception ex)
        {RemoveDummyMarker(target);Logger.LogWarning(ex,"Could not render dummy {Label} world marker",DummyLabel(target));}
    }
    private static void RemoveDummyMarker(EffectDummy target)
    {
        if(DummyMarker(target) is { } marker) marker.Remove();
        target.Marker=null;target.MarkerHandle=0;target.MarkerText=null;target.MarkerPosition=null;
    }
    private string? DummyRosterText(CCSPlayerController owner)
    {
        var targets=OwnedDummies(owner);if(targets.Length==0) return null;
        var selected=SelectedDummy(owner);
        var rows=new List<DummyRosterItem>();
        foreach(var d in targets)
        {
            var pawn=d.Bot.PlayerPawn.Value;
            int hp=pawn is {IsValid:true} ? Math.Clamp(pawn.Health,0,999) : 0;
            int armor=pawn is {IsValid:true} ? Math.Clamp(pawn.ArmorValue,0,999) : 0;
            bool preparing=!d.Ready || d.ResetRequested || pawn is not {IsValid:true};
            bool dead=!preparing && pawn!.LifeState!=0;
            float nativeUntil=pawn is {IsValid:true} && float.IsFinite(pawn.BlindUntilTime) ? pawn.BlindUntilTime : 0;
            float remaining=!dead && !preparing ? Math.Clamp(Math.Max(d.BlindUntil,nativeUntil)-Server.CurrentTime,0,99.9f) : 0;
            string? prediction=DummyPredictionText(owner,d.Bot,compact:true);
            rows.Add(new(d.LabelIndex,ReferenceEquals(selected,d),hp,armor,d.LastDamage,remaining,preparing,dead,
                prediction,FlashRosterContext(owner,d,prediction)));
        }
        return DummyRoster.Format(rows);
    }
}
