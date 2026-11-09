using System.Text.Encodings.Web;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private const string PanelBridgeResource="maps/scripts/nadelab_panel_bridge.vjs_c";
    private CHandle<CCSPointScriptEntity>? panelScript;
    private CHandle<CLogicCase>? panelInbox;
    private int panelBridgeEpoch=-1,panelBridgeSequence=1,panelBridgeGeneration;
    private const int PanelDeliveryAttemptLimit=3;
    private sealed record PanelDelivery(int Slot,uint Controller,uint Pawn,ulong Steam,uint Layout,int Epoch,
        float Deadline,PanelChannel Channel,string Text,string Keys,bool Show,int Attempts=0);
    private readonly Dictionary<int,PanelDelivery> panelDeliveries=[];
    // Keep the latest intent after acknowledgement. A send can throw after the
    // native call accepted it, so the text cache alone cannot tell us whether a
    // channel still needs hiding. There are only four intents per player slot.
    private readonly Dictionary<(int Slot,PanelChannel Channel),(int Sequence,PanelDelivery Delivery)> panelLatestDelivery=[];
    private int panelAcknowledged,panelDeliveryFailures;
    private bool panelDeliveryTimer;

    private void RegisterPanelBridge()
    {
        AddCommand("css_nl_panel_ack","Internal native panel delivery acknowledgement",(caller,command)=>
        {
            if(caller is not null || command.ArgCount!=2 || !int.TryParse(command.ArgByIndex(1),out int sequence) ||
                !panelDeliveries.Remove(sequence,out var delivery) || delivery.Epoch!=grenadeEpoch) return;
            panelAcknowledged++;
        });
    }

    // Pass the transient string through a native logic_case output. Dynamic
    // text is never used as an entity name, pooled key or script source.
    private void WritePanelThroughEngine(CCSCustomHudLayout layout,CCSPlayerController player,string text,bool show,PanelChannel channel=PanelChannel.Main,string keys="")
    {
        var key=(player.Slot,channel);
        if(panelLatestDelivery.TryGetValue(key,out var old)) panelDeliveries.Remove(old.Sequence);
        if(panelDeliveries.Count>=256) throw new InvalidOperationException("HUD delivery queue is full.");
        int seq=panelBridgeSequence=panelBridgeSequence>=1000000000 ? 2 : panelBridgeSequence+1;
        var delivery=new PanelDelivery(player.Slot,player.EntityHandle.Raw,player.PlayerPawn.Raw,player.SteamID,
            layout.EntityHandle.Raw,grenadeEpoch,Server.CurrentTime+1,channel,show ? text : "",show && channel==PanelChannel.Main ? keys : "",show);
        panelLatestDelivery[key]=(seq,delivery);
        SendPanelDelivery(seq,delivery);
    }

    private bool CurrentPanelDelivery(int seq,PanelDelivery delivery,out CCSPlayerController player,out CCSCustomHudLayout layout)
    {
        player=null!;layout=null!;
        if(!loaded || delivery.Epoch!=grenadeEpoch ||
            !panelLatestDelivery.TryGetValue((delivery.Slot,delivery.Channel),out var latest) || latest.Sequence!=seq ||
            latest.Delivery.Controller!=delivery.Controller || latest.Delivery.Pawn!=delivery.Pawn ||
            latest.Delivery.Steam!=delivery.Steam || latest.Delivery.Layout!=delivery.Layout || latest.Delivery.Epoch!=delivery.Epoch ||
            latest.Delivery.Show!=delivery.Show || latest.Delivery.Text!=delivery.Text || latest.Delivery.Keys!=delivery.Keys ||
            Utilities.GetPlayerFromSlot(delivery.Slot) is not {IsValid:true} current || current.EntityHandle.Raw!=delivery.Controller ||
            current.PlayerPawn.Raw!=delivery.Pawn || current.SteamID!=delivery.Steam ||
            ExistingPanelLayout() is not {IsValid:true} currentLayout || currentLayout.EntityHandle.Raw!=delivery.Layout) return false;
        player=current;layout=currentLayout;return true;
    }

    private void SendPanelDelivery(int seq,PanelDelivery delivery)
    {
        if(!CurrentPanelDelivery(seq,delivery,out var player,out var layout)) {panelDeliveries.Remove(seq);return;}
        delivery=delivery with {Attempts=delivery.Attempts+1,Deadline=Server.CurrentTime+1};
        panelLatestDelivery[(delivery.Slot,delivery.Channel)]=(seq,delivery);
        // Track before bridge creation/AcceptInput, including synchronous
        // exceptions. In particular, a failed hide must remain retryable after
        // ClearPanelChannel has removed its visible-text cache.
        panelDeliveries[seq]=delivery;
        using var stream=new MemoryStream();
        using(var writer=new Utf8JsonWriter(stream,new JsonWriterOptions {Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping}))
        {writer.WriteStartObject();writer.WriteNumber("seq",seq);writer.WriteString("channel",delivery.Channel.ToString().ToLowerInvariant());writer.WriteString("text",delivery.Text);writer.WriteString("keys",delivery.Keys);writer.WriteBoolean("show",delivery.Show);writer.WriteEndObject();}
        string payload=System.Text.Encoding.UTF8.GetString(stream.ToArray());
        try
        {
            var inbox=EnsurePanelBridge();
            panelScript!.Value!.AcceptInput("RunScriptInput",caller:layout,value:"layout");
            inbox.AcceptInput("InValue",activator:player,caller:layout,value:payload);
        }
        finally {SchedulePanelDeliveryCheck();}
    }

    private void SchedulePanelDeliveryCheck()
    {
        if(panelDeliveryTimer || panelDeliveries.Count==0) return;
        panelDeliveryTimer=true;int epoch=grenadeEpoch,generation=panelBridgeGeneration;
        AddTimer(.25f,()=>
        {
            if(epoch!=grenadeEpoch || generation!=panelBridgeGeneration) return;
            panelDeliveryTimer=false;
            foreach(var (seq,delivery) in panelDeliveries.ToArray())
            {
                if(Server.CurrentTime<delivery.Deadline) continue;
                panelDeliveries.Remove(seq);
                // Never replay an older hide over a newer visible menu/notice,
                // or an old occupant's operation over a reused slot/pawn.
                if(!CurrentPanelDelivery(seq,delivery,out var player,out _)) continue;
                panelDeliveryFailures++;
                if(delivery.Attempts<PanelDeliveryAttemptLimit)
                {
                    try {SendPanelDelivery(seq,delivery);}
                    catch(Exception ex) {Logger.LogWarning(ex,"Native panel update {Sequence} retry {Attempt} failed",seq,delivery.Attempts+1);}
                    continue;
                }
                var cache=PanelCache(delivery.Channel);
                if(delivery.Show && cache.TryGetValue(delivery.Slot,out var cached) &&
                    cached.Controller==delivery.Controller && cached.Text==delivery.Text && cached.Keys==delivery.Keys)
                {
                    cache.Remove(delivery.Slot);
                    if(!player.IsBot)
                    {
                        if(delivery.Channel==PanelChannel.Notice) NoticeFallback(player,cached.Text);
                        else if(delivery.Channel==PanelChannel.Dummies) DummyPanelFallback(player);
                        else if(delivery.Channel==PanelChannel.Main)
                        {
                            panelFallbackSlots.Add(delivery.Slot);
                            TryPanelCleanup("unacknowledged text fallback",()=>player.PrintToCenter(PanelFallbackText(cached.Text,cached.Keys)));
                        }
                    }
                }
                Logger.LogWarning("Native panel update {Sequence} ({Channel}, show={Show}) was not acknowledged after {Attempts} attempts; automatic retries stopped",
                    seq,delivery.Channel.ToString().ToLowerInvariant(),delivery.Show,delivery.Attempts);
            }
            SchedulePanelDeliveryCheck();
        });
    }

    private CLogicCase EnsurePanelBridge()
    {
        if(panelBridgeEpoch==grenadeEpoch && panelScript?.Value is {IsValid:true} && panelInbox?.Value is {IsValid:true} existing) return existing;
        ClearPanelBridge(preserveDeliveries:true);
        CLogicCase? inbox=null;CCSPointScriptEntity? script=null;
        try
        {
            inbox=Utilities.CreateEntityByName<CLogicCase>("logic_case") ?? throw new InvalidOperationException("Could not create HUD message inbox.");
            inbox.DispatchSpawn();inbox.Health=0;
            script=Utilities.CreateEntityByName<CCSPointScriptEntity>("point_script") ?? throw new InvalidOperationException("Could not create native HUD script.");
            using(var keys=new CEntityKeyValues())
            {keys.SetString("cs_script",PanelBridgeResource[..^2]);script.DispatchSpawn(keys);}
            script.AcceptInput("RunScriptInput",caller:inbox,value:"attach");
            if(!inbox.IsValid || inbox.Health!=1) throw new InvalidOperationException("Official HUD script did not attach to its inbox.");
            panelInbox=new(inbox.EntityHandle.Raw);panelScript=new(script.EntityHandle.Raw);panelBridgeEpoch=grenadeEpoch;
            return inbox;
        }
        catch
        {
            TryPanelCleanup("bridge creation script",()=>{if(script?.IsValid==true) script.Remove();});
            TryPanelCleanup("bridge creation inbox",()=>{if(inbox?.IsValid==true) inbox.Remove();});
            throw;
        }
    }
    private void ForgetPanelDeliveries(int slot)
    {
        foreach(var seq in panelDeliveries.Where(kv=>kv.Value.Slot==slot).Select(kv=>kv.Key).ToArray()) panelDeliveries.Remove(seq);
        foreach(var channel in Enum.GetValues<PanelChannel>()) panelLatestDelivery.Remove((slot,channel));
    }
    private void ClearPanelBridge(bool preserveDeliveries=false)
    {
        if(panelBridgeEpoch==grenadeEpoch)
        {
            TryPanelCleanup("bridge script",()=>{if(panelScript?.Value is {IsValid:true} script) script.Remove();});
            TryPanelCleanup("bridge inbox",()=>{if(panelInbox?.Value is {IsValid:true} inbox) inbox.Remove();});
        }
        panelScript=null;panelInbox=null;panelBridgeEpoch=-1;panelBridgeGeneration++;
        // Recreating just the script/inbox must preserve pending hides for the
        // surviving layout. A global UI reset also forgets every channel intent.
        // Sequence ids survive both paths, so old acknowledgements cannot match
        // a new operation on the same map.
        if(!preserveDeliveries) {panelDeliveries.Clear();panelLatestDelivery.Clear();}
        panelDeliveryTimer=false;
    }
}
