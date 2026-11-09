using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private const string FlightPreviewConVar="sv_grenade_trajectory_prac_pipreview";
    private sealed class FlightPreviewClient(uint controller,ulong steam)
    {
        public readonly uint Controller=controller;
        public readonly ulong Steam=steam;
        public bool? Sent;
        public int Writes;
    }
    private readonly Dictionary<int,FlightPreviewClient> flightPreviewClients=[];
    private void RegisterFlightPreview()
    {
        // Group A: leave native PiP enabled. Do not re-arm on throws or close on
        // old detonations. The engine owns camera selection; latest-only is NOT guaranteed.
        AddTimer(.25f,TickFlightPreview,TimerFlags.REPEAT);
        AddCommand("css_nl_pipstatus","Read-only native continuous preview state",(p,c)=>{
            if(p is not null) return;
            Say(null,$"PIP: mode=native-continuous-A; practice={Practice}; clients={flightPreviewClients.Count}; serverDefault={FlightPreviewDefault()}; latestGuaranteed=false.");
            foreach(var (slot,client) in flightPreviewClients)
                Say(null,$"PIP: slot={slot}; enabled={client.Sent}; writes={client.Writes}; overlapPause=false.");
        });
    }
    private static bool FlightPreviewOwner(CCSPlayerController? owner,FlightPreviewClient client)=>
        owner is {IsValid:true,IsBot:false} && owner.EntityHandle.Raw==client.Controller && owner.SteamID==client.Steam;
    private static bool FlightPreviewDefault()=>ConVar.Find(FlightPreviewConVar)?.GetPrimitiveValue<bool>()==true;
    private void SendFlightPreview(CCSPlayerController owner,FlightPreviewClient client,bool enabled)
    {
        if(client.Sent==enabled) return;
        try {owner.ReplicateConVar(FlightPreviewConVar,enabled ? "1" : "0");client.Sent=enabled;client.Writes++;}
        catch(Exception ex) {Logger.LogWarning(ex,"Could not update native grenade preview for slot {Slot}",owner.Slot);}
    }
    private void SyncFlightPreview(CCSPlayerController owner)
    {
        if(!flightPreviewClients.TryGetValue(owner.Slot,out var client) || !FlightPreviewOwner(owner,client))
            flightPreviewClients[owner.Slot]=client=new(owner.EntityHandle.Raw,owner.SteamID);
        SendFlightPreview(owner,client,loaded && Practice && Get(owner).FlightPreviewEnabled && owner.PlayerPawn.Value is {IsValid:true,LifeState:0});
    }
    private void TickFlightPreview()
    {
        if(!loaded || !Practice) {ClearFlightPreviews();return;}
        foreach(var owner in Utilities.GetPlayers().Where(p=>p.IsValid && !p.IsBot)) SyncFlightPreview(owner);
        foreach(var (slot,client) in flightPreviewClients.ToArray())
            if(!FlightPreviewOwner(Utilities.GetPlayerFromSlot(slot),client)) flightPreviewClients.Remove(slot);
    }
    private void ClearFlightPreviews()
    {
        foreach(var (slot,client) in flightPreviewClients)
            if(Utilities.GetPlayerFromSlot(slot) is {} owner && FlightPreviewOwner(owner,client))
                SendFlightPreview(owner,client,FlightPreviewDefault());
        flightPreviewClients.Clear();
    }
}
