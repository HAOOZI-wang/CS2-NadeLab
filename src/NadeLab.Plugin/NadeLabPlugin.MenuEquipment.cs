using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using NadeLab.Core;

namespace NadeLab.Plugin;
public sealed partial class NadeLabPlugin
{
    private sealed record MenuEquipmentLease(uint Pawn,uint[] Handles,uint Last,bool PreventPickup,bool Restricted=true,float RetryAt=0);
    private sealed record MenuEquipWindow(uint Pawn,string? Expected,float Earliest,float Deadline);
    private readonly Dictionary<int,MenuEquipmentLease> menuEquipment=[];
    private readonly HashSet<int> restoringMenuEquipment=[];
    private readonly Dictionary<int,MenuEquipWindow> menuEquipWindows=[];
    private void RegisterMenuEquipment()
    {
        AddCommandListener("drop",(p,c)=>p is not null && menuEquipment.TryGetValue(p.Slot,out var lease) && lease.Restricted ? HookResult.Handled : HookResult.Continue,HookMode.Pre);
        RegisterEventHandler<EventPlayerDeath>((ev,info)=>{if(ev.Userid is { } p) RestoreMenuEquipment(p.Slot);return HookResult.Continue;},HookMode.Pre);
        RegisterEventHandler<EventItemPickup>((ev,info)=>{if(ev.Userid is { } p) AllowMenuEquipmentChange(p);return HookResult.Continue;},HookMode.Pre);
        RegisterMenuEquipmentDiagnostic();
    }
    private void AllowMenuEquipmentChange(CCSPlayerController p,GrenadeKind? kind=null)
    {
        RestoreMenuEquipment(p.Slot);
        var expected=kind?.Weapon();
        if(expected is null && menuEquipWindows.TryGetValue(p.Slot,out var previous)) expected=previous.Expected;
        menuEquipWindows[p.Slot]=new(p.PlayerPawn.Raw,expected,Server.CurrentTime+.15f,Server.CurrentTime+2);
    }
    private void SyncMenuEquipment(CCSPlayerController p)
    {
        bool menuContext=HasPanel(p) || queuedPlayers.HasPending(p.Slot);
        if(!menuContext) menuEquipWindows.Remove(p.Slot);
        bool restrict=HasCompanion(p) && inputCompanion?.Peer?.InputActive==true && Get(p).Placement is null && menuContext;
        if(!restrict) {RestoreMenuEquipment(p.Slot);return;}
        if(p.PlayerPawn.Value is not {IsValid:true,LifeState:0,WeaponServices:{ } weapons} pawn) return;
        if(menuEquipWindows.TryGetValue(p.Slot,out var window))
        {
            if(window.Pawn!=pawn.EntityHandle.Raw) menuEquipWindows.Remove(p.Slot);
            else if(Server.CurrentTime<window.Earliest) return;
            else if(window.Expected is not null && weapons.ActiveWeapon.Value?.DesignerName!=window.Expected)
            {
                if(Server.CurrentTime<window.Deadline) return;
                menuEquipWindows.Remove(p.Slot);ClosePanel(p);
                HudNotice(Get(p),"道具尚未切出，已收起菜单；请重新就位或按 F3 切换。",5);return;
            }
            else menuEquipWindows.Remove(p.Slot);
        }
        RestrictMenuEquipment(p,pawn);
    }
    private void RestrictMenuEquipment(CCSPlayerController p,CCSPlayerPawn pawn)
    {
        if(pawn.WeaponServices is not { } weapons) return;
        if(menuEquipment.TryGetValue(p.Slot,out var previous) && previous.Pawn!=pawn.EntityHandle.Raw) RestoreMenuEquipment(p.Slot);
        if(menuEquipment.TryGetValue(p.Slot,out previous) && !previous.Restricted) RestoreMenuEquipment(p.Slot);
        if(menuEquipment.ContainsKey(p.Slot)) return;
        var original=weapons.MyWeapons.Select(h=>h.Raw).ToArray();
        if(original.Length==0 || weapons.ActiveWeapon.Value is not {IsValid:true}) return;
        menuEquipment[p.Slot]=new(pawn.EntityHandle.Raw,original,weapons.LastWeapon.Raw,weapons.PreventWeaponPickup);
        weapons.PreventWeaponPickup=true;
        var masked=MenuEquipment.Mask(original,weapons.ActiveWeapon.Raw);
        for(int i=0;i<masked.Length;i++) SetMenuEquipmentHandle(weapons,i,masked[i]);
        weapons.LastWeapon.Raw=weapons.ActiveWeapon.Raw;
        MarkBodyComponent(weapons,"CPlayer_WeaponServices","m_hLastWeapon");
        MarkBodyComponent(weapons,"CPlayer_WeaponServices","m_bPreventWeaponPickup");
    }
    private void RestoreMenuEquipment(int? slot=null)
    {
        foreach(int key in menuEquipment.Keys.Where(k=>slot is null || k==slot).ToArray())
        {
            if(!restoringMenuEquipment.Add(key)) continue;
            try
            {
            var saved=menuEquipment[key];
            if(!saved.Restricted && loaded && Server.CurrentTime<saved.RetryAt) continue;
            if(new CHandle<CCSPlayerPawn>(saved.Pawn).Value is not {IsValid:true,WeaponServices:{ } weapons} pawn)
            {menuEquipment.Remove(key);continue;}
            bool Owned(uint handle)=>new CHandle<CBasePlayerWeapon>(handle).Value is {IsValid:true} w && w.OwnerEntity.Raw==saved.Pawn;
            // Always release pickup/drop independently of slot recovery. Retain
            // ownership of any failed recovery, but never retain a control lock.
            if(saved.Restricted)
            {
                weapons.PreventWeaponPickup=saved.PreventPickup;
                MarkBodyComponent(weapons,"CPlayer_WeaponServices","m_bPreventWeaponPickup");
            }
            menuEquipment[key]=saved with {Restricted=false};
            var restored=MenuEquipment.Restore(weapons.MyWeapons.Select(h=>h.Raw).ToArray(),saved.Handles,Owned);
            for(int i=0;i<restored.Handles.Length;i++) SetMenuEquipmentHandle(weapons,i,restored.Handles[i]);
            if(Owned(saved.Last) && restored.Handles.Contains(saved.Last)) weapons.LastWeapon.Raw=saved.Last;
            else weapons.LastWeapon.Raw=weapons.ActiveWeapon.Raw;
            MarkBodyComponent(weapons,"CPlayer_WeaponServices","m_hLastWeapon");
            // Use the engine to allocate missing slots; never resize its vector
            // through guessed memory offsets. A temporary absent grenade reserves a slot
            // and is replaced and removed within this frame. Original weapons,
            // their ammo, skins and ownership are preserved, including knives
            // which the engine refuses to drop.
            var failed=new List<uint>();
            foreach(uint raw in restored.Overflow)
                if(Owned(raw) && !RestoreMenuEquipmentSlot(pawn,raw)) failed.Add(raw);
            if(failed.Count==0) menuEquipment.Remove(key);
            else
            {
                menuEquipment[key]=saved with {Restricted=false,RetryAt=Server.CurrentTime+.5f};
                if(!saved.Restricted) continue;
                Logger.LogWarning("Menu inventory recovery pending for slot {Slot}; {Count} original handles retained without input restrictions",key,failed.Count);
                if(Utilities.GetPlayerFromSlot(key) is {IsValid:true,IsBot:false} p && p.PlayerPawn.Raw==saved.Pawn)
                    HudNotice(Get(p),"背包恢复尚未完成；操作限制已解除，正在重试原装备恢复。",6);
            }
            }
            finally {restoringMenuEquipment.Remove(key);}
        }
    }
    private bool RestoreMenuEquipmentSlot(CCSPlayerPawn pawn,uint raw)
    {
        if(pawn.ItemServices is not { } items || pawn.WeaponServices is not { } weapons) return false;
        uint active=weapons.ActiveWeapon.Raw,last=weapons.LastWeapon.Raw;
        var ammo=weapons.Ammo.ToArray();
        bool preventPickup=weapons.PreventWeaponPickup;
        var names=weapons.MyWeapons.Select(h=>h.Value).Where(w=>w is {IsValid:true}).Select(w=>w!.DesignerName).ToHashSet();
        var placeholder=Enum.GetValues<GrenadeKind>().FirstOrDefault(kind=>!names.Contains(kind.Weapon()) &&
            (!kind.Fire() || (!names.Contains(GrenadeKind.Molotov.Weapon()) && !names.Contains(GrenadeKind.Incendiary.Weapon()))));
        if(names.Contains(placeholder.Weapon())) return false;
        CBasePlayerWeapon? temporary=null;
        try
        {
            weapons.PreventWeaponPickup=false;
            // A hidden grenade still contributes to the engine's carry cap.
            // Reserve an otherwise absent type with temporary ammo counts, then
            // restore the complete original ammo array before leaving this frame.
            weapons.Ammo.Clear();
            temporary=items.As<CCSPlayer_ItemServices>().GiveNamedItem<CBasePlayerWeapon>(placeholder.Weapon());
            if(temporary is not {IsValid:true})
            {Logger.LogWarning("Menu slot allocation returned no weapon; pickup={Pickup}; slots={Count}",preventPickup,weapons.MyWeapons.Count);return false;}
            uint allocated=temporary.EntityHandle.Raw;
            int index=Enumerable.Range(0,weapons.MyWeapons.Count).FirstOrDefault(i=>weapons.MyWeapons[i].Raw==allocated,-1);
            if(index<0)
            {Logger.LogWarning("Menu slot allocation was not attached; owner={Owner}; pawn={Pawn}; slots={Count}",temporary.OwnerEntity.Raw,pawn.EntityHandle.Raw,weapons.MyWeapons.Count);return false;}
            SetMenuEquipmentHandle(weapons,index,raw);
            if(weapons.ActiveWeapon.Raw==allocated) SetBodyActiveWeapon(weapons,active);
            if(weapons.LastWeapon.Raw==allocated) weapons.LastWeapon.Raw=last;
            MarkBodyComponent(weapons,"CPlayer_WeaponServices","m_hLastWeapon");
            return true;
        }
        finally
        {
            if(temporary is {IsValid:true}) temporary.Remove();
            weapons.PreventWeaponPickup=preventPickup;
            MarkBodyComponent(weapons,"CPlayer_WeaponServices","m_bPreventWeaponPickup");
            ammo.AsSpan().CopyTo(weapons.Ammo);
            MarkBodyComponent(weapons,"CPlayer_WeaponServices","m_iAmmo");
        }
    }
    private static void SetMenuEquipmentHandle(CPlayer_WeaponServices weapons,int index,uint raw)
    {
        if(weapons.MyWeapons[index].Raw==raw) return;
        weapons.MyWeapons[index].Raw=raw;
        // This network vector requires the changed element index. A field-only
        // notification can leave the client using its previous selectable list
        // even though the server has restored all original owned handles.
        NativeAPI.SchemaNetworkStateChanged(weapons.__m_pChainEntity.Handle,
            (uint)Schema.GetSchemaOffset("CPlayer_WeaponServices","m_hMyWeapons"),(uint)index,uint.MaxValue);
    }
}
