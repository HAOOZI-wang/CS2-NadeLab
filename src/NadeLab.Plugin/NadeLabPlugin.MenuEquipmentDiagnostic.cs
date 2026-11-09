using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using NadeLab.Core;

namespace NadeLab.Plugin;
public sealed partial class NadeLabPlugin
{
    private void RegisterMenuEquipmentDiagnostic()=>AddCommand("css_nl_menuequipmentcheck",
        "Empty-server menu inventory recovery checks",(caller,command)=>
    {
        if(caller is not null || !Practice || !BenchmarkIdle || Utilities.GetPlayers().Any(p=>!p.IsBot))
        {Say(null,"WEAPONMENU refused: empty idle practice server required.");return;}
        var p=Utilities.GetPlayers().FirstOrDefault(p=>p.IsBot && p.PlayerPawn.Value is {IsValid:true,LifeState:0});
        if(p?.PlayerPawn.Value is not {WeaponServices:{ } weapons} pawn)
        {Say(null,"WEAPONMENU refused: living fixture bot required.");return;}
        int passed=0,failed=0;
        uint[]? recoveredHandles=null;ushort[]? recoveredAmmo=null;
        void Check(bool ok,string text){if(ok)passed++;else failed++;Say(null,$"WEAPONMENU {(ok ? "PASS" : "FAIL")}: {text}");}
        var originalKind=Get(p).Kind;bool originalPickup=weapons.PreventWeaponPickup;
        uint[] original=weapons.MyWeapons.Select(h=>h.Raw).ToArray();
        try
        {
            RestrictMenuEquipment(p,pawn);
            Check(menuEquipment.ContainsKey(p.Slot) && weapons.MyWeapons.All(h=>h.Raw==MenuEquipment.Empty || h.Raw==weapons.ActiveWeapon.Raw),"mask retains only held weapon selection");
            RestoreMenuEquipment(p.Slot);
            Check(weapons.MyWeapons.Select(h=>h.Raw).SequenceEqual(original) && weapons.PreventWeaponPickup==originalPickup && !menuEquipment.ContainsKey(p.Slot),"ordinary close restores exact handles and pickup/drop");
            RestrictMenuEquipment(p,pawn);Get(p).Kind=GrenadeKind.Flash;
            GiveSelectedGrenade(p,GrenadeKind.Flash);EquipSmoke(p);
            Check(!menuEquipment.ContainsKey(p.Slot) && HasSelectedGrenade(p,GrenadeKind.Flash),"supply restores complete inventory before testing capacity");
            Check(menuEquipWindows.TryGetValue(p.Slot,out var window) && window.Expected==GrenadeKind.Flash.Weapon(),"async equip retains its expected weapon until actual completion");
            ClosePanel(p);
            Check(!menuEquipWindows.ContainsKey(p.Slot),"closing a menu discards stale equip expectations");
            // Synthetic external compaction: surviving items still belong to the
            // pawn, but no slots remain. Exercise real engine slot allocation.
            weapons.PreventWeaponPickup=true;
            RestrictMenuEquipment(p,pawn);
            var saved=menuEquipment[p.Slot].Handles.Where(h=>h!=MenuEquipment.Empty).ToArray();
            var savedAmmo=weapons.Ammo.ToArray();
            weapons.MyWeapons.RemoveAll();RestoreMenuEquipment(p.Slot);
            Say(null,$"WEAPONMENU recovery state: slots=[{string.Join(',',weapons.MyWeapons.Select(h=>h.Raw))}]; saved=[{string.Join(',',saved.Select(raw=>$"{raw}/{new CHandle<CBasePlayerWeapon>(raw).Value?.DesignerName}/{new CHandle<CBasePlayerWeapon>(raw).Value?.OwnerEntity.Raw}"))}]; pawn={pawn.EntityHandle.Raw}; pickup={weapons.PreventWeaponPickup}; pending={menuEquipment.ContainsKey(p.Slot)}");
            Check(!menuEquipment.ContainsKey(p.Slot) && weapons.PreventWeaponPickup,"zero-slot recovery terminates its lease and restores original pickup flag");
            Check(saved.All(raw=>new CHandle<CBasePlayerWeapon>(raw).Value is {IsValid:true} w && w.OwnerEntity.Raw==pawn.EntityHandle.Raw && weapons.MyWeapons.Any(h=>h.Raw==raw)),"all original entities including knife survive zero-slot recovery in inventory");
            Check(weapons.Ammo.SequenceEqual(savedAmmo),"slot allocation preserves every original ammo count");
            recoveredHandles=saved;recoveredAmmo=savedAmmo;
        }
        catch(Exception ex){failed++;Say(null,"WEAPONMENU exception: "+ex.Message);}
        finally
        {
            RestoreMenuEquipment(p.Slot);menuEquipWindows.Remove(p.Slot);
            var session=Get(p);session.Kind=originalKind;session.Revision++;
            weapons.PreventWeaponPickup=originalPickup;
            MarkBodyComponent(weapons,"CPlayer_WeaponServices","m_bPreventWeaponPickup");
        }
        Server.NextFrame(()=>Server.NextFrame(()=>
        {
            Check(pawn.IsValid && recoveredHandles is not null && recoveredAmmo is not null &&
                recoveredHandles.All(raw=>new CHandle<CBasePlayerWeapon>(raw).Value is {IsValid:true} w &&
                    w.OwnerEntity.Raw==pawn.EntityHandle.Raw && weapons.MyWeapons.Any(h=>h.Raw==raw)) &&
                weapons.Ammo.SequenceEqual(recoveredAmmo),"original inventory and ammo survive delayed temporary-entity removal");
            Say(null,$"WEAPONMENU DONE {(failed==0 ? "PASS" : "FAIL")}: {passed} passed; {failed} failed; disposable fixture bot only.");
        }));
    });
}
