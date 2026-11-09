using System.Runtime.CompilerServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private CompanionPipeServer? inputCompanion;
    private uint companionController;
    private void RegisterCompanion()
    {
        if(!OperatingSystem.IsWindows()) return;
        inputCompanion=new(CompanionProtocol.PipeName(GameContentRoot));
        RegisterMenuEquipment();
        RegisterListener<Listeners.OnTick>(TickCompanion);
    }
    private bool HasCompanion(CCSPlayerController p)=>inputCompanion?.Peer is {Fresh:true} &&
        companionController==p.EntityHandle.Raw && Practice;
    private string CompanionKeys(CCSPlayerController p,Session s,bool menu,bool root)=>CompanionControls.Text(
        CompanionControls.Resolve(ResolveWorkflow(p,s),menu,root,HasDummyBatch(p),OwnedDummies(p).Length>0));
    private CompanionSnapshot SnapshotCompanion(CCSPlayerController p,CompanionPeer peer)
    {
        var s=Get(p);bool menu=CurrentPanel(p,out var panel);
        string context=$"{grenadeEpoch}:{p.EntityHandle.Raw}:{p.PlayerPawn.Value?.EntityHandle.Raw}:{s.Revision}:{(menu ? RuntimeHelpers.GetHashCode(panel.Menu) : 0)}:{s.Selected}:{ResolveWorkflow(p,s).Step}:{DummyBatchHud(p)}";
        return new(peer.Id,context,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),menu,menu&&panel.Menu.IsRoot,
            CompanionControls.Resolve(ResolveWorkflow(p,s),menu,menu&&panel.Menu.IsRoot,HasDummyBatch(p),OwnedDummies(p).Length>0),p.SteamID,
            "本地输入已连接",peer.Acknowledgements.Value);
    }
    private void TickCompanion()
    {
        var peer=inputCompanion?.Peer;
        if(peer is null || !peer.Fresh || !Practice)
        {
            companionController=0;
            RestoreMenuEquipment();
            if(peer is not null) peer.Snapshot=new(peer.Id,"",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),false,false,[],Status:"训练连接已暂停");
            return;
        }
        // A local instance with multiple humans must specify the selected Steam profile.
        var humans=Utilities.GetPlayers().Where(p=>p.IsValid&&!p.IsBot&&p.PlayerPawn.Value is {IsValid:true,LifeState:0} &&
            (peer.SteamId==0 || p.SteamID==peer.SteamId) && (p.IpAddress?.StartsWith("127.0.0.1",StringComparison.Ordinal)==true || p.IpAddress?.StartsWith("[::1]",StringComparison.Ordinal)==true)).ToArray();
        if(humans.Length!=1)
        {
            companionController=0;peer.Snapshot=new(peer.Id,"",0,false,false,[],Status:"等待唯一的本地训练玩家");
            RestoreMenuEquipment();
            while(peer.Requests.TryDequeue(out _)){}return;
        }
        var player=humans[0];uint previous=companionController;companionController=player.EntityHandle.Raw;
        if(previous!=companionController) {Get(player).NextHudAt=0;Get(player).LastHudSentAt=0;}
        var snapshot=SnapshotCompanion(player,peer);
        peer.Acknowledgements.Complete(queuedPlayers.HasPending(player.Slot));
        for(int count=0;count<8&&peer.Requests.TryDequeue(out var request);count++)
        {
            peer.Acknowledgements.Observe(request.Sequence);
            if(request.Session!=peer.Id || request.Context!=snapshot.Context || !snapshot.Keys.Any(k=>k.Action==request.Action)) continue;
            ExecuteCompanion(player,request.Action,request.Key);
            snapshot=SnapshotCompanion(player,peer);
        }
        peer.Acknowledgements.Complete(queuedPlayers.HasPending(player.Slot));
        SyncMenuEquipment(player);
        peer.Snapshot=snapshot with {Acknowledged=peer.Acknowledgements.Value};
    }
    private void ExecuteCompanion(CCSPlayerController p,string action,int key=0)
    {
        if(!Check(p,out _,allowPlacement:true)) return;
        bool menu=HasPanel(p);
        if(action is not ("up" or "down")) RestoreMenuEquipment(p.Slot);
        switch(action)
        {
            case "menu":TogglePanel(p);break;
            case "up":if(key<0) ReceivePanelWheel(p,-1);else ReceivePanelDirection(p,-1);break;
            case "down":if(key<0) ReceivePanelWheel(p,1);else ReceivePanelDirection(p,1);break;
            case "select":ActivatePanel(p);break;
            case "back":
                if(menu) BackPanel(p);else if(HasDummyBatch(p)) DummyBatchBack(p);else if(Get(p).Placement is not null) FinishPlacement(p,false);
                break;
            case "primary":if(HasDummyBatch(p)) DummyBatchPrimary(p);else PrimaryAction(p);break;
            case "next":NextCandidate(p,null!);break;
            case "flight":ToggleTrainingFlight(p);break;
            case "region":if(Get(p).FlashMode) OpenFlashRequirements(p);else OpenRegionEditor(p);break;
            case "retarget":ClearDummyBatch(p.Slot);Retarget(p);break;
            case "reposition":Reposition(p);break;
            case "grenade":
                ClearDummyBatch(p.Slot);
                if(Get(p).Placement is not null) FinishPlacement(p,false);
                var kinds=Enum.GetValues<GrenadeKind>();SelectGrenade(p,kinds[(Array.IndexOf(kinds,Get(p).Kind)+1)%kinds.Length]);break;
            case "effect":ToggleEffectPreview(p);break;
            case "camera":ToggleGrenadeBodyCamera(p);break;
            case "resetdummies":ResetOwnedDummies(p);break;
        }
    }
}
