using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private sealed record ImpactRelease(int Slot,ImpactOwner Owner,uint Pawn,GrenadeKind Kind,float At);
    private sealed record ImpactProjectile(ImpactRelease Release,uint Handle,float Created);
    private sealed record RecentImpact(ImpactOwner Owner,RecordedImpact Record,bool Historical);
    private readonly List<ImpactRelease> impactReleases=[];
    private readonly Dictionary<uint,ImpactProjectile> impactProjectiles=[];
    private readonly Dictionary<int,Dictionary<GrenadeKind,RecentImpact>> recentImpacts=[];
    private readonly Dictionary<(ulong Steam,GrenadeKind Kind),RecordedImpact> archivedImpacts=[];
    private string? impactArchiveWorld;
    private float impactCaptureBlockedUntil;

    private ImpactOwner ImpactIdentity(CCSPlayerController owner)=>new(grenadeEpoch,owner.EntityHandle.Raw,owner.SteamID);

    private void RegisterRecentImpacts()
    {
        AddPlayerCommand("css_nl_recent","Use your latest actual impact as the current grenade target",(p,a)=>AdoptRecentImpact(p));
        AddCommand("css_nl_recentstatus","Server-console recent-impact archive summary",(caller,command)=>
        {
            if(caller is not null) return;
            bool compatible=impactArchiveWorld==worldVersion?.ToJsonString();
            Say(null,$"RECENTSTATUS map={Server.MapName}; archiveRecords={archivedImpacts.Count}; worldCompatible={compatible}; liveOwners={recentImpacts.Count}; pendingProjectiles={impactProjectiles.Count}.");
            foreach(var group in archivedImpacts.Values.GroupBy(r=>r.Kind))
            {
                var latest=group.MaxBy(r=>r.Utc)!;
                Say(null,$"RECENTSTATUS {group.Key}: latestUtc={latest.Utc:O}; actual=({latest.Position.X:F5},{latest.Position.Y:F5},{latest.Position.Z:F5}); records={group.Count()}.");
            }
        });
        RegisterEventHandler<EventGrenadeThrown>((e,i)=>
        {
            if(!loaded || !Practice || e.Userid is not {IsValid:true} owner || owner.PlayerPawn.Value is not {IsValid:true} pawn) return HookResult.Continue;
            var kind=Enum.GetValues<GrenadeKind>().Where(k=>e.Weapon==k.Weapon() || "weapon_"+e.Weapon==k.Weapon()).Cast<GrenadeKind?>().FirstOrDefault();
            if(kind is null) return HookResult.Continue;
            TrackImpactRelease(owner,pawn,kind.Value);
            return HookResult.Continue;
        });
        RegisterEventHandler<EventFlashbangDetonate>((e,i)=>{CaptureIndexedImpact(e.Userid,e.Entityid,GrenadeKind.Flash,new(e.X,e.Y,e.Z));return HookResult.Continue;},HookMode.Pre);
        RegisterEventHandler<EventHegrenadeDetonate>((e,i)=>{CaptureIndexedImpact(e.Userid,e.Entityid,GrenadeKind.HighExplosive,new(e.X,e.Y,e.Z));return HookResult.Continue;},HookMode.Pre);
        RegisterEventHandler<EventSmokegrenadeDetonate>((e,i)=>{CaptureIndexedImpact(e.Userid,e.Entityid,GrenadeKind.Smoke,new(e.X,e.Y,e.Z));return HookResult.Continue;},HookMode.Pre);
        RegisterEventHandler<EventDecoyStarted>((e,i)=>{CaptureIndexedImpact(e.Userid,e.Entityid,GrenadeKind.Decoy,new(e.X,e.Y,e.Z));return HookResult.Continue;},HookMode.Pre);
        RegisterEventHandler<EventMolotovDetonate>((e,i)=>{CaptureFireImpact(e.Userid,new(e.X,e.Y,e.Z));return HookResult.Continue;},HookMode.Pre);
        ReadImpactArchive();
        RegisterRecentImpactDiagnostic();
    }

    private void TrackImpactRelease(CCSPlayerController owner,CCSPlayerPawn pawn,GrenadeKind kind)
    {
        float now=Server.CurrentTime;
        if(now<impactCaptureBlockedUntil) return;
        impactReleases.RemoveAll(r=>r.Owner.Epoch!=grenadeEpoch || now-r.At>30);
        foreach(var key in impactProjectiles.Where(p=>p.Value.Release.Owner.Epoch!=grenadeEpoch || now-p.Value.Release.At>30).Select(p=>p.Key).ToArray()) impactProjectiles.Remove(key);
        if(impactReleases.Count>=64 || impactProjectiles.Count>=64)
        {impactCaptureBlockedUntil=now+30;impactReleases.Clear();impactProjectiles.Clear();return;}
        var release=new ImpactRelease(owner.Slot,ImpactIdentity(owner),pawn.EntityHandle.Raw,kind,now);
        impactReleases.Add(release);
        PersonalRelease(release);
        Server.NextFrame(()=>
        {
            if(!loaded || !Practice || release.Owner.Epoch!=grenadeEpoch || !impactReleases.Contains(release)) return;
            var current=Utilities.GetPlayerFromSlot(release.Slot);
            if(current is not {IsValid:true} || ImpactIdentity(current)!=release.Owner) return;
            var projectile=ResolveImpactProjectile(release);if(projectile is null) return;
            impactProjectiles[projectile.Index]=new(release,projectile.EntityHandle.Raw,projectile.CreateTime);
        });
    }

    private static CBaseCSGrenadeProjectile? ResolveImpactProjectile(ImpactRelease release)
    {
        var matches=Utilities.FindAllEntitiesByDesignerName<CBaseCSGrenadeProjectile>(release.Kind.Projectile())
            .Where(g=>g.IsValid && g.Thrower.Raw==release.Pawn && g.CreateTime>=release.At-.1f && g.CreateTime<=release.At+.1f).ToArray();
        return matches.Length==1 ? matches[0] : null;
    }

    private void CaptureIndexedImpact(CCSPlayerController? owner,int entity,GrenadeKind kind,Vector3 actual)
    {
        if(!loaded || !Practice || owner is not {IsValid:true} || entity<=0 || !Geometry.Finite(actual) ||
            !impactProjectiles.TryGetValue((uint)entity,out var tracked)) return;
        var projectile=Utilities.GetEntityFromIndex<CBaseCSGrenadeProjectile>(entity);
        if(projectile is not {IsValid:true} || projectile.DesignerName!=kind.Projectile() || projectile.Thrower.Raw!=tracked.Release.Pawn ||
            projectile.CreateTime!=tracked.Created || !RecentImpactRules.Matches(tracked.Release.Owner,ImpactIdentity(owner),tracked.Release.Kind,kind,tracked.Handle,projectile.EntityHandle.Raw) ||
            Server.CurrentTime-tracked.Release.At is <0 or >30) return;
        impactProjectiles.Remove((uint)entity);
        impactReleases.Remove(tracked.Release);
        StoreRecentImpact(owner,kind,actual,tracked.Handle,OutcomeForImpact(kind),tracked.Release);
    }

    private void CaptureFireImpact(CCSPlayerController? owner,Vector3 actual)
    {
        if(!loaded || !Practice || owner is not {IsValid:true} || !Geometry.Finite(actual)) return;
        var identity=ImpactIdentity(owner);
        // This event has no projectile id and the two fire types share a class.
        // Count every throw (even untracked ones); never infer type from inventory,
        // selected menu item, nearby trajectory, team or a lone armed recording.
        var releases=impactReleases.Where(r=>r.Owner==identity && r.Kind.Fire() && Server.CurrentTime-r.At is >=0 and <12).ToArray();
        if(releases.Length!=1) return;
        var possible=impactProjectiles.Where(p=>p.Value.Release==releases[0]).ToArray();
        if(possible.Length==0)
        {
            // A fire bottle may hit a nearby wall before the next-frame binder.
            // Resolve the sole release at the real event, using the same strict
            // thrower full handle and creation window, never event proximity.
            var immediate=ResolveImpactProjectile(releases[0]);if(immediate is null) return;
            impactProjectiles[immediate.Index]=new(releases[0],immediate.EntityHandle.Raw,immediate.CreateTime);
            possible=impactProjectiles.Where(p=>p.Value.Release==releases[0]).ToArray();
        }
        if(possible.Length!=1) return;
        var tracked=possible[0];var projectile=Utilities.GetEntityFromIndex<CBaseCSGrenadeProjectile>((int)tracked.Key);
        if(projectile is not {IsValid:true} || projectile.EntityHandle.Raw!=tracked.Value.Handle || projectile.Thrower.Raw!=releases[0].Pawn) return;
        impactProjectiles.Remove(tracked.Key);
        impactReleases.Remove(releases[0]);
        StoreRecentImpact(owner,releases[0].Kind,actual,tracked.Value.Handle,"burst-ignition-unconfirmed",releases[0]);
    }

    private static string OutcomeForImpact(GrenadeKind kind)=>kind switch
    {GrenadeKind.Smoke=>"smoke-detonated",GrenadeKind.Decoy=>"sound-started",_=>"detonated"};

    private void StoreRecentImpact(CCSPlayerController owner,GrenadeKind kind,Vector3 actual,uint projectile,string outcome,ImpactRelease release)
    {
        var record=new RecordedImpact(Guid.NewGuid().ToString("N"),kind,owner.SteamID,actual,DateTimeOffset.UtcNow);
        PersonalImpact(owner,release,record);
        if(!recentImpacts.TryGetValue(owner.Slot,out var kinds)) recentImpacts[owner.Slot]=kinds=[];
        kinds[kind]=new(ImpactIdentity(owner),record,false);
        if(!owner.IsBot && record.Steam!=0) archivedImpacts[(record.Steam,kind)]=record;
        WriteGrenade(new JsonObject {["ThrowId"]=record.Id,["Kind"]=kind.ToString(),["PlayerSteamId"]=record.Steam.ToString(),
            ["Outcome"]=outcome,["Actual"]=VectorNode(actual),["ProjectileHandle"]=projectile,["OwnerHandle"]=owner.EntityHandle.Raw,
            ["WorldVersion"]=worldVersion?.DeepClone(),["Source"]="engine-event",["EffectVerified"]=false},"recent-impacts.jsonl");
        // A concise notice is independent of the menu/status text. Do not
        // override an existing per-throw result with an unsolicited panel.
        if(!owner.IsBot && sessions.TryGetValue(owner.Slot,out var session) && session.Kind==kind && session.Arm is null)
            owner.PrintToConsole($"[NadeLab] 已保存{kind.Label()}真实生效点；菜单 → 采用最近实投爆点。");
    }

    private RecentImpact? FindRecentImpact(CCSPlayerController owner,GrenadeKind kind)
    {
        var identity=ImpactIdentity(owner);
        if(recentImpacts.TryGetValue(owner.Slot,out var kinds) && kinds.TryGetValue(kind,out var found) && found.Owner==identity) return found;
        if(!owner.IsBot && owner.SteamID!=0 && impactArchiveWorld==worldVersion?.ToJsonString() && archivedImpacts.TryGetValue((owner.SteamID,kind),out var saved))
        {
            if(kinds is null) recentImpacts[owner.Slot]=kinds=[];
            return kinds[kind]=new(identity,saved,true);
        }
        return null;
    }

    private void AdoptRecentImpact(CCSPlayerController? player)
    {
        if(!Check(player,out _)) return;
        var session=Get(player!);var impact=FindRecentImpact(player!,session.Kind);
        if(impact is null)
        {Say(player,$"还没有可确认属于你的{session.Kind.Label()}实投生效点。直接手投一颗，等生效后再选此项；无需先开启记录。火瓶连续投掷归属不明时不保存。");return;}
        AdoptRecentImpactCore(player!,session,impact);
    }

    private void AdoptRecentImpactCore(CCSPlayerController player,Session session,RecentImpact impact)
    {
        if(impact.Owner!=ImpactIdentity(player) || impact.Record.Kind!=session.Kind || !Geometry.Finite(impact.Record.Position)) return;
        ClosePanel(player);Invalidate(player.Slot,session);
        session.Target=RecentImpactRules.TargetFor(session.Kind,impact.Record.Position);session.TargetPreset=session.Target;
        DrawTargetRegion(player,session.Target.Value,8,TargetRegionColor,session.Kind.Timed());
        Say(player,$"已采用{(impact.Historical ? "历史" : "最近")}{session.Kind.Label()}真实生效点（{impact.Record.Utc.ToLocalTime():HH:mm:ss}）。"+
            (session.Start is null ? "到出手位置站稳后按使用键搜索。" : "保留原出手站位，使用键重新搜索；静步+使用换站位。")+"目标设置菜单调整容差；该目标不代表效果已验证。");
    }

    private void ClearRecentImpacts() {impactReleases.Clear();impactProjectiles.Clear();recentImpacts.Clear();impactCaptureBlockedUntil=0;recentImpactCheck=null;}
    private void RemoveRecentImpacts(int slot)
    {
        recentImpacts.Remove(slot);impactReleases.RemoveAll(r=>r.Slot==slot);
        foreach(var key in impactProjectiles.Where(p=>p.Value.Release.Slot==slot).Select(p=>p.Key).ToArray()) impactProjectiles.Remove(key);
    }

    private void ReadImpactArchive()
    {
        archivedImpacts.Clear();impactArchiveWorld=worldVersion?.ToJsonString();
        if(impactArchiveWorld is null) return;
        // Startup-only, bounded tail; runtime adoption performs no disk reads.
        foreach(string file in new[]{"grenade-throws.jsonl","recent-impacts.jsonl"})
        try
        {
            string path=Path.Combine(DataPath,file);if(!File.Exists(path)) continue;
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
            const int tailBytes=8*1024*1024;bool partial=stream.Length>tailBytes;
            if(partial) stream.Seek(-tailBytes,SeekOrigin.End);
            using var reader=new StreamReader(stream,Encoding.UTF8);if(partial) reader.ReadLine();
            while(reader.ReadLine() is { } line)
            {
                var record=RecentImpactRules.ReadArchive(line,Server.MapName,impactArchiveWorld);if(record is null) continue;
                var key=(record.Steam,record.Kind);
                if(!archivedImpacts.TryGetValue(key,out var prior) || record.Utc>prior.Utc) archivedImpacts[key]=record;
            }
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {Logger.LogWarning(ex,"Recent impact history could not be read from {File}",file);}
    }
}
