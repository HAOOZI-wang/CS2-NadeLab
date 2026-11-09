using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private void RegisterGrenadeInventoryDiagnostic()=>AddCommand("css_nl_inventorycheck",
        "Empty-server grenade capacity and replacement checks",(caller,command)=>
    {
        if(caller is not null || !loaded || !Practice || Utilities.GetPlayers().Any(player=>!player.IsBot) ||
            job is not null || flashJob is not null || flashSearchCheckPending || flashVerificationCheckPending || DummyLayoutLoadActive ||
            placementCheck is not null || panelCheckPending || effectStressCheck is not null || flights.Count>0 || grenadeFlights.Count>0 ||
            sessions.Values.Any(session=>session.Placement is not null || session.Pending is not null || session.Arm is not null))
        {Say(caller,"INVENTORYCHECK refused: idle practice server console with zero humans required.");return;}
        var owner=Utilities.GetPlayers().FirstOrDefault(player=>player.IsValid && player.IsBot &&
            player.PlayerPawn.Value is {IsValid:true,LifeState:0} && !ThrowInProgress(player));
        if(owner?.PlayerPawn.Value is not {IsValid:true,LifeState:0} pawn || pawn.WeaponServices is not { } services ||
            !ReadTrainingGrenades(pawn,out var original) || original.Any(item=>item.InUse))
        {Say(null,"INVENTORYCHECK refused: one living idle bot with readable inventory required.");return;}
        int totalLimit=ConVar.Find("ammo_grenade_limit_total")?.GetPrimitiveValue<int>() ?? 4;
        int flashLimit=ConVar.Find("ammo_grenade_limit_flashbang")?.GetPrimitiveValue<int>() ?? 2;
        int defaultLimit=ConVar.Find("ammo_grenade_limit_default")?.GetPrimitiveValue<int>() ?? 1;
        if(totalLimit is <3 or >5 || flashLimit<2 || defaultLimit<1)
        {Say(null,"INVENTORYCHECK refused: fixture needs total cap 3..5, flash cap >=2 and default cap >=1; no cvars changed.");return;}

        var originalAmmo=services.Ammo.ToArray();uint originalActive=services.ActiveWeapon.Raw,originalLast=services.LastWeapon.Raw;
        var originalHandles=original.Select(item=>(Raw:item.Weapon.EntityHandle.Raw,item.Kind)).ToArray();
        var ordinary=services.MyWeapons.Select(handle=>handle.Value).Where(weapon=>weapon is {IsValid:true} &&
            original.All(item=>item.Weapon.EntityHandle.Raw!=weapon.EntityHandle.Raw))
            .Select(weapon=>(Weapon:weapon!,Raw:weapon!.EntityHandle.Raw,Clip1:weapon.Clip1,Clip2:weapon.Clip2)).ToArray();
        var grenadeAmmoTypes=original.Select(item=>item.AmmoType).ToHashSet();
        bool botActive=pawn.BotAllowActive;bool? aiActive=pawn.Bot?.AllowActive;
        int passed=0,failed=0;
        void Check(bool ok,string text)
        {if(ok) passed++;else failed++;Say(null,$"INVENTORYCHECK {(ok ? "PASS" : "FAIL")}: {text}");}
        HeldTrainingGrenade[] Read()
        {
            if(!ReadTrainingGrenades(pawn,out var inventory)) throw new InvalidOperationException("unreadable grenade inventory");
            foreach(var item in inventory) grenadeAmmoTypes.Add(item.AmmoType);
            return inventory;
        }
        void ClearGrenades()
        {
            foreach(var item in Read())
            {
                if(item.InUse) throw new InvalidOperationException("refusing to remove a pulled grenade");
                uint raw=item.Weapon.EntityHandle.Raw;
                Say(null,$"INVENTORYCHECK detach before {item.Kind}: {NativeState(item,raw)}");
                bool detached=RemoveTrainingGrenade(services,item);
                Say(null,$"INVENTORYCHECK detach after {item.Kind}: {NativeState(item,raw)}; detached={detached}");
                if(!detached) throw new InvalidOperationException($"native removal did not detach {item.Kind}: {NativeState(item,raw)}");
                SetTrainingGrenadeAmmo(services,item.AmmoType,0);
            }
        }
        string NativeState(HeldTrainingGrenade item,uint raw)=>
            $"raw={raw}; valid={item.Weapon.IsValid}; owner={(item.Weapon.IsValid ? item.Weapon.OwnerEntity.Raw : uint.MaxValue)}; "+
            $"pawn={pawn.EntityHandle.Raw}; active={services.ActiveWeapon.Raw}; last={services.LastWeapon.Raw}; "+
            $"ammo[{item.AmmoType}]={services.Ammo[item.AmmoType]}; inventory=[{string.Join(",",services.MyWeapons.Select(handle=>handle.Raw))}]";
        void Give(GrenadeKind kind,int count=1)
        {
            for(int i=0;i<count;i++) owner.GiveNamedItem(kind.Weapon());
            var inventory=Read();
            if(inventory.FirstOrDefault(item=>item.Kind==kind)?.Count!=count)
                throw new InvalidOperationException($"fixture grant failed: {kind} x{count}; "+Describe(inventory));
        }
        static string Describe(IEnumerable<HeldTrainingGrenade> inventory)=>string.Join(", ",inventory.Select(item=>$"{item.Kind}={item.Count}@{item.AmmoType}"));
        try
        {
            Say(null,$"INVENTORYCHECK schema: MyWeapons={Schema.GetSchemaOffset("CPlayer_WeaponServices","m_hMyWeapons")}; "+
                $"ActiveWeapon={Schema.GetSchemaOffset("CPlayer_WeaponServices","m_hActiveWeapon")}; Ammo={Schema.GetSchemaOffset("CPlayer_WeaponServices","m_iAmmo")}");
            pawn.BotAllowActive=false;if(pawn.Bot is { } bot) bot.AllowActive=false;
            ClearGrenades();Give(GrenadeKind.Flash,2);Give(GrenadeKind.Smoke);
            if(totalLimit>=4) Give(GrenadeKind.HighExplosive);
            if(totalLimit>=5) Give(GrenadeKind.Molotov);
            var before=Read();uint flash=before.Single(item=>item.Kind==GrenadeKind.Flash).Weapon.EntityHandle.Raw;
            Check(before.Sum(item=>item.Count)==totalLimit,$"full fixture uses ammo counts: {Describe(before)}");
            bool supplied=EnsureSelectedGrenade(owner,GrenadeKind.Decoy);
            var after=Read();
            Check(supplied && after.Sum(item=>item.Count)==totalLimit &&
                after.Any(item=>item.Kind==GrenadeKind.Decoy && item.Count==1),"absent selected grenade fits at the native total cap");
            Check(after.Any(item=>item.Kind==GrenadeKind.Flash && item.Count==1 && item.Weapon.EntityHandle.Raw==flash),
                "one spare flash was consumed; its weapon and remaining flash survived");
            Check(before.Where(item=>item.Kind!=GrenadeKind.Flash).All(item=>after.Any(current=>current.Weapon.EntityHandle.Raw==item.Weapon.EntityHandle.Raw && current.Count==item.Count)),
                "unselected single-count grenade entities survived");
            uint decoy=after.Single(item=>item.Kind==GrenadeKind.Decoy).Weapon.EntityHandle.Raw;
            Check(EnsureSelectedGrenade(owner,GrenadeKind.Decoy) && Read().Any(item=>item.Kind==GrenadeKind.Decoy && item.Count==1 && item.Weapon.EntityHandle.Raw==decoy),
                "repeated supply retains the selected entity without duplicating ammo");

            var pinned=after.First(item=>item.Kind==GrenadeKind.Flash).Weapon.As<CBaseCSGrenade>();
            pinned.PinPulled=true;
            try
            {
                Check(!EnsureSelectedGrenade(owner,GrenadeKind.Incendiary) &&
                    Read().Select(item=>(item.Weapon.EntityHandle.Raw,item.Count)).SequenceEqual(after.Select(item=>(item.Weapon.EntityHandle.Raw,item.Count))),
                    "pulled grenade blocks supply and preserves the whole inventory");
            }
            finally {if(pinned.IsValid) pinned.PinPulled=false;}

            ClearGrenades();Give(GrenadeKind.Molotov);Give(GrenadeKind.Flash,2);
            for(int i=0;i<4;i++)
            {
                var wanted=i%2==0 ? GrenadeKind.Incendiary : GrenadeKind.Molotov;
                Check(EnsureSelectedGrenade(owner,wanted) && Read().Count(item=>item.Kind.Fire())==1 &&
                    Read().Any(item=>item.Kind==wanted && item.Count==1),$"native fire exchange {i+1} clears counterpart references/ammo");
            }
            Check(ordinary.All(item=>item.Weapon.IsValid && item.Weapon.EntityHandle.Raw==item.Raw &&
                services.MyWeapons.Any(handle=>handle.Raw==item.Raw) && item.Weapon.Clip1==item.Clip1 && item.Weapon.Clip2==item.Clip2) &&
                Enumerable.Range(0,originalAmmo.Length).Where(index=>!grenadeAmmoTypes.Contains(index)).All(index=>services.Ammo[index]==originalAmmo[index]),
                "guns, knife, clips and non-grenade reserve ammo preserved");
        }
        catch(Exception ex) {failed++;Say(null,"INVENTORYCHECK exception: "+ex.Message);}
        finally
        {
            try
            {
                ClearGrenades();
                foreach(var group in original.GroupBy(item=>item.Kind))
                {
                    var item=group.First();owner.GiveNamedItem(item.Kind.Weapon());
                    SetTrainingGrenadeAmmo(services,item.AmmoType,item.Count);
                }
                originalAmmo.AsSpan().CopyTo(services.Ammo);MarkBodyComponent(services,"CPlayer_WeaponServices","m_iAmmo");
                var restored=Read();
                uint RestoreHandle(uint raw)=>originalHandles.Any(item=>item.Raw==raw)
                    ? restored.FirstOrDefault(item=>item.Kind==originalHandles.First(previous=>previous.Raw==raw).Kind)?.Weapon.EntityHandle.Raw ?? uint.MaxValue : raw;
                SetBodyActiveWeapon(services,RestoreHandle(originalActive));services.LastWeapon.Raw=RestoreHandle(originalLast);
                MarkBodyComponent(services,"CPlayer_WeaponServices","m_hLastWeapon");
                Check(original.All(item=>restored.Any(current=>current.Kind==item.Kind && current.Count==item.Count)) &&
                    restored.Length==original.Length,"original bot grenade types/counts restored");
            }
            catch(Exception ex) {failed++;Say(null,"INVENTORYCHECK restore exception: "+ex.Message);}
            pawn.BotAllowActive=botActive;if(aiActive is { } active && pawn.Bot is { } bot) bot.AllowActive=active;
        }
        Say(null,$"INVENTORYCHECK DONE {(failed==0 ? "PASS" : "FAIL")}: {passed} passed; {failed} failed; client switching/HUD still require client validation.");
    });
}
