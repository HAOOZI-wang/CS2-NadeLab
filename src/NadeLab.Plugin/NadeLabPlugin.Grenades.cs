using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private void GiveSelectedGrenade(CCSPlayerController player,GrenadeKind kind)
    {
        if(!Enum.IsDefined(kind) || !player.IsValid || ThrowInProgress(player)) return;
        AllowMenuEquipmentChange(player,kind);
        EnsureSelectedGrenade(player,kind);
        int slot=player.Slot;var session=Get(player);var identity=ActionIdentity(player);int revision=session.Revision;
        Server.NextFrame(()=>
        {
            var current=Utilities.GetPlayerFromSlot(slot);
            if(!loaded || !Practice || current?.IsValid!=true || !identity.Matches(ActionIdentity(current)) ||
                current.PlayerPawn.Value is not {IsValid:true,LifeState:0} || session.Revision!=revision || session.Kind!=kind || session.Placement is not null || ThrowInProgress(current)) return;
            AllowMenuEquipmentChange(current,kind);
            if(HasSelectedGrenade(current,kind)) return;
            if(EnsureSelectedGrenade(current,kind)) current.ExecuteClientCommand(kind.Slot());
            else HudNotice(session,"未能补充所选道具，请检查携带限制后重新准备。",5);
        });
    }

    private sealed record HeldTrainingGrenade(CBasePlayerWeapon Weapon,GrenadeKind Kind,int AmmoType,int Count,bool InUse);

    private static bool ReadTrainingGrenades(CCSPlayerPawn pawn,out HeldTrainingGrenade[] inventory)
    {
        inventory=[];
        if(pawn.WeaponServices is not { } services) return false;
        var result=new List<HeldTrainingGrenade>();
        foreach(var weapon in services.MyWeapons.Select(handle=>handle.Value).Where(weapon=>weapon is {IsValid:true}))
        {
            var kind=Enum.GetValues<GrenadeKind>().Cast<GrenadeKind?>().FirstOrDefault(kind=>kind!.Value.Weapon()==weapon!.DesignerName);
            if(kind is null) continue;
            if(weapon!.VData is not { } data || data.PrimaryAmmoType>=services.Ammo.Length) return false;
            var grenade=weapon.As<CBaseCSGrenade>();
            result.Add(new(weapon,kind.Value,data.PrimaryAmmoType,services.Ammo[data.PrimaryAmmoType],grenade.PinPulled || grenade.ThrowTime>0));
        }
        // Ambiguous/shared ammo ownership cannot be safely reduced by type.
        if(result.GroupBy(item=>item.AmmoType).Any(group=>group.Select(item=>item.Kind).Distinct().Count()>1)) return false;
        inventory=result.ToArray();return true;
    }

    private static bool HasSelectedGrenade(CCSPlayerController player,GrenadeKind kind)=>
        player.PlayerPawn.Value is {IsValid:true,LifeState:0} pawn && ReadTrainingGrenades(pawn,out var inventory) &&
        inventory.Any(item=>item.Kind==kind && item.Count>0);

    private static bool EnsureSelectedGrenade(CCSPlayerController player,GrenadeKind kind)
    {
        if(player.PlayerPawn.Value is not {IsValid:true,LifeState:0} pawn || pawn.WeaponServices is not { } services ||
            ThrowInProgress(player) || !ReadTrainingGrenades(pawn,out var inventory)) return false;
        int totalLimit=ConVar.Find("ammo_grenade_limit_total")?.GetPrimitiveValue<int>() ?? 4;
        int typeLimit=ConVar.Find(kind==GrenadeKind.Flash ? "ammo_grenade_limit_flashbang" : "ammo_grenade_limit_default")?.GetPrimitiveValue<int>() ?? (kind==GrenadeKind.Flash ? 2 : 1);
        var plan=GrenadeInventory.Prepare(kind,inventory.Select(item=>new GrenadeInventoryItem(item.Kind,item.Count,item.InUse)).ToArray(),totalLimit,typeLimit);
        if(!plan.Give) return inventory.Any(item=>item.Kind==kind && item.Count>0);
        foreach(var change in plan.Changes)
        {
            foreach(var item in inventory.Where(item=>item.Kind==change.Kind))
            {
                if(change.Remaining==0)
                {
                    if(!RemoveTrainingGrenade(services,item)) return false;
                }
                SetTrainingGrenadeAmmo(services,item.AmmoType,change.Remaining);
            }
        }
        player.GiveNamedItem(kind.Weapon());
        return HasSelectedGrenade(player,kind);
    }

    private static bool RemoveTrainingGrenade(CPlayer_WeaponServices services,HeldTrainingGrenade item)
    {
        if(!item.Weapon.IsValid) return false;
        var grenade=item.Weapon.As<CBaseCSGrenade>();
        if(grenade.PinPulled || grenade.ThrowTime>0) return false;
        uint raw=item.Weapon.EntityHandle.Raw;
        // CSS 1.0.378 labels this signature CBasePlayerPawn_RemovePlayerItem,
        // but its native receiver is CPlayer_WeaponServices: it reads that
        // component's active handle and inventory vector, detaches the weapon,
        // and schedules entity removal itself. Passing pawn.Handle (as the CSS
        // convenience wrapper does) never detaches the actual inventory.
        VirtualFunctions.RemovePlayerItemVirtual(services.Handle,item.Weapon.Handle);
        if(services.MyWeapons.Any(handle=>handle.Raw==raw)) return false;
        if(services.ActiveWeapon.Raw==raw) SetBodyActiveWeapon(services,uint.MaxValue);
        if(services.LastWeapon.Raw==raw)
        {services.LastWeapon.Raw=uint.MaxValue;MarkBodyComponent(services,"CPlayer_WeaponServices","m_hLastWeapon");}
        return true;
    }

    private static void SetTrainingGrenadeAmmo(CPlayer_WeaponServices services,int ammoType,int count)
    {
        services.Ammo[ammoType]=checked((ushort)count);
        MarkBodyComponent(services,"CPlayer_WeaponServices","m_iAmmo");
    }
    private PhysicsProfile ProfileFor(Session s)
    {
        var profile=Grenades.Bootstrap(s.Kind,physics);
        if(s.Kind.Fire())
        {
            float fuse=ConVar.Find("molotov_throw_detonate_time")?.GetPrimitiveValue<float>() ?? 3.5f;
            if(float.IsFinite(fuse) && fuse is >=.1f and <=10) profile=profile with {FuseSeconds=fuse};
        }
        return profile;
    }

    private void RegisterGrenadeSupport()
    {
        RegisterGrenadeInventoryDiagnostic();
        AddPlayerCommand("css_nl_grenade","Cycle selected grenade",(p,args)=>
        {
            if(!Check(p,out _)) return;
            var kinds=Enum.GetValues<GrenadeKind>();var s=Get(p!);
            SelectGrenade(p!,kinds[(Array.IndexOf(kinds,s.Kind)+1)%kinds.Length]);
        });
        AddCommand("css_nl_probe","Empty-server native grenade calibration",GrenadeProbe);
        RegisterEventHandler<EventFlashbangDetonate>((e,i)=>{ObserveFlashVerificationDetonation((uint)e.Entityid);FinishGrenade((uint)e.Entityid,new(e.X,e.Y,e.Z),GrenadeKind.Flash,"detonated");return HookResult.Continue;});
        RegisterEventHandler<EventHegrenadeDetonate>((e,i)=>{FinishGrenade((uint)e.Entityid,new(e.X,e.Y,e.Z),GrenadeKind.HighExplosive,"detonated");return HookResult.Continue;});
        RegisterEventHandler<EventDecoyStarted>((e,i)=>{FinishGrenade((uint)e.Entityid,new(e.X,e.Y,e.Z),GrenadeKind.Decoy,"sound-started");return HookResult.Continue;});
        RegisterEventHandler<EventMolotovDetonate>((e,i)=>{FinishFireProjectile(e.Userid?.Slot,new(e.X,e.Y,e.Z));return HookResult.Continue;});
        RegisterEventHandler<EventPlayerBlind>((e,i)=>{RecordBlind(e);return HookResult.Continue;});
        RegisterEventHandler<EventPlayerHurt>((e,i)=>{RecordGrenadeDamage(e);return HookResult.Continue;});
        RegisterEventHandler<EventInfernoStartburn>((e,i)=>{RecordInferno((uint)e.Entityid);return HookResult.Continue;});
    }

    private void OpenGrenadeMenu(CCSPlayerController? p)
    {
        if(!Check(p,out _)) return;
        var opened=Get(p!);int revision=opened.Revision,epoch=grenadeEpoch;
        var menu=NewPanel($"道具 · 当前{Get(p!).Kind.Label()}");menu.ExitButton=true;
        foreach(var kind in Enum.GetValues<GrenadeKind>())
            menu.AddMenuOption(kind.Label(),(who,option)=>QueueAction(who,current=>
            {
                if(!MenuIsCurrent(current,opened,revision,epoch)) return;
                SelectGrenade(current!,kind);
            }));
        menu.Open(p!);
    }

    private void SelectGrenade(CCSPlayerController player,GrenadeKind kind)
    {
        if(!Enum.IsDefined(kind) || !Check(player,out _)) return;
        ClearDummyBatch(player.Slot);
        var s=Get(player);ClosePanel(player);
        if(s.Kind==kind) {OpenMenu(player);return;}
        var transfer=GrenadeTargetTransfer.Resolve(s.Target,s.Kind,kind);
        Invalidate(player.Slot,s);s.Kind=kind;s.SearchPhysics=null;
        s.Target=transfer.Target;s.TargetPreset=transfer.Target;
        s.RegionDraft=null;s.Start=null;s.RequestedStart=null;s.ReferenceView=null;
        GiveSelectedGrenade(player,kind);EquipSmoke(player);
        string targetNotice=transfer.Kind switch
        {
            TargetTransferKind.KeptArea=>"保留兼容的单一目标区域，到站位按使用键搜索。",
            TargetTransferKind.PointToArea=>"原精确点已转为同中心的可编辑区域；在目标设置菜单检查范围，到站位按使用键搜索。",
            TargetTransferKind.ComplexTarget=>"旧目标含多个区域、排除区或封窗标记，不能作为新道具的单一区域继承；已清空，请按使用键重新选点。",
            TargetTransferKind.SpaceChanged=>"地面/空中目标类型不兼容，已清空旧目标；请按使用键重新选点。",
            _=>"请按使用键选目标，再到站位按使用键搜索。"
        };
        Say(player,$"已选择{kind.Label()}。{targetNotice}"+
            (kind==GrenadeKind.Smoke ? "" : "当前路线为待校准模型，实际伤害/致盲/火区须实投验证。"));
    }

    private void PickGrenadeTarget(CCSPlayerController player,string? method=null)
    {
        var s=Get(player);var pawn=player.PlayerPawn.Value!;
        Vector3 point;
        if(method=="feet") point=NativeWorld.FromNative(pawn.AbsOrigin!)+(s.Kind.Timed() ? new Vector3(0,0,pawn.ViewOffset.Z) : Vector3.Zero);
        else
        {
            if(!PickTargetPoint(player,!s.Kind.Timed(),out point,out var normal)) return;
            if(s.Kind.Timed()) point+=normal*8;
        }
        var target=new Target(point,48,new Vector3(48,48,s.Kind.Timed() ? 48 : 12),
            s.Kind.Timed() ? "三维爆点区域" : s.Kind.Fire() ? "点火落点区域（不代表烧满）" : "诱饵生效区域",Airspace:s.Kind.Timed());
        Invalidate(player.Slot,s);s.Target=target;s.TargetPreset=target;
        Say(player,$"{s.Kind.Label()}目标已记录；在目标设置菜单调整范围/高度或重新定位。效果与位置命中分开记录。");
    }

    private void OpenGrenadeTargetEditor(CCSPlayerController player)
    {
        if(!Check(player,out _)) return;
        if(Get(player).FlashMode) {OpenFlashMenu(player);return;}
        var s=Get(player);if(s.Target is not { } target) {Say(player,"先按使用键选择目标。");return;}
        int revision=s.Revision,epoch=grenadeEpoch;
        if(target.Objective is not null || target.ExtraRegions is {Count:>0} || target.Exclusions is {Count:>0})
        {Say(player,"该目标包含当前道具编辑器无法完整显示的约束，请先在目标设置菜单重新选择单一目标；原目标未修改。");return;}
        // Compatibility for a retained point from an older plugin/session: show
        // the proposed conversion, and commit it only when an edit is applied.
        var editable=target.IsArea ? target : target with {AreaHalfSize=new Vector3(target.Radius,target.Radius,MathF.Min(target.Radius,s.Kind.Timed() ? 48 : 12))};
        if(!target.IsArea) Say(player,"当前是精确点；以下范围显示为同中心的区域草稿，调整后才应用。");
        var size=editable.AreaHalfSize!.Value;
        var menu=NewPanel($"{s.Kind.Label()} · 水平{size.X*2:F0}×{size.Y*2:F0}u / 高差±{size.Z:F0}u");menu.ExitButton=true;menu.Back=OpenTargetTools;
        menu.AddMenuOption("显示区域边界（8秒）",(who,option)=>QueueAction(who,current=>
        {
            if(!MenuIsCurrent(current,s,revision,epoch)) return;
            ClearPreview(current!.Slot);
            DrawTargetRegion(current!,editable,8,TargetRegionColor,s.Kind.Timed());
            Say(current,"蓝色实线 = 允许落点/爆点范围；效果参考暂时隐藏，8秒后恢复。");
        }));
        void Add(string label,Func<Target,Target> change)=>menu.AddMenuOption(label,(who,option)=>QueueAction(who,current=>
        {
            if(!MenuIsCurrent(current,s,revision,epoch)) return;
            var updated=change(editable);updated.Validate();Invalidate(current!.Slot,s);s.Target=updated;s.TargetPreset=updated;
            Say(current,"已应用目标调整，原站位保留；按使用键重新搜索。");OpenGrenadeTargetEditor(current);
        }));
        Add("水平范围放宽25%",t=>t with {AreaHalfSize=new(MathF.Min(256,t.AreaHalfSize!.Value.X*1.25f),MathF.Min(256,t.AreaHalfSize.Value.Y*1.25f),t.AreaHalfSize.Value.Z)});
        Add("水平范围收紧20%",t=>t with {AreaHalfSize=new(MathF.Max(8,t.AreaHalfSize!.Value.X*.8f),MathF.Max(8,t.AreaHalfSize.Value.Y*.8f),t.AreaHalfSize.Value.Z)});
        if(s.Kind.Timed())
        {
            Add("允许高差增加8u",t=>t with {AreaHalfSize=new(t.AreaHalfSize!.Value.X,t.AreaHalfSize.Value.Y,MathF.Min(64,t.AreaHalfSize.Value.Z+8))});
            Add("允许高差减少8u",t=>t with {AreaHalfSize=new(t.AreaHalfSize!.Value.X,t.AreaHalfSize.Value.Y,MathF.Max(8,t.AreaHalfSize.Value.Z-8))});
            Add("爆点提高16u",t=>t with {Position=t.Position+Vector3.UnitZ*16});
            Add("爆点降低16u",t=>t with {Position=t.Position-Vector3.UnitZ*16});
            menu.AddMenuOption("变为道具，重新定位空中爆点",(who,option)=>QueueAction(who,current=>
            {if(MenuIsCurrent(current,s,revision,epoch)) {ClosePanel(current);TargetByKind(current);}}));
        }
        menu.AddMenuOption("敌方站位 / 闪光朝向 / 伤害假人",(who,option)=>QueueAction(who,current=>{if(MenuIsCurrent(current,s,revision,epoch)) OpenEffectTarget(current);}));
        menu.AddMenuOption("自由实投记录",(who,option)=>QueueAction(who,current=>{if(MenuIsCurrent(current,s,revision,epoch)) RecordManualThrow(current);}));
        menu.Open(player);
    }
}
