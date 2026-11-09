using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    // These names are shared with ui/panorama. Only values change at runtime;
    // never intern dynamic panel/class names into the engine's string tables.
    private const string PanelLayoutResource="panorama/layout/custom_game/nadelab_panel.vxml_c";
    private const string PanelWindowId="NadeLabWindow",PanelLabelId="NadeLabText";
    private CHandle<CCSCustomHudLayout>? panelLayout;
    private int panelLayoutEpoch=-1;
    private readonly Dictionary<int,(uint Controller,string Text,string Keys)> panelDisplayText=[];
    private enum PanelChannel { Main, Notice, Dummies, Cue }
    private readonly Dictionary<int,(uint Controller,string Text,string Keys)> panelNoticeText=[];
    private readonly Dictionary<int,(uint Controller,string Text,string Keys)> panelDummyText=[];
    private readonly Dictionary<int,(uint Controller,string Text,string Keys)> panelCueText=[];
    private readonly Dictionary<int,float> panelDummyNextWrite=[];
    private Dictionary<int,(uint Controller,string Text,string Keys)> PanelCache(PanelChannel channel)=>channel switch
    {PanelChannel.Notice=>panelNoticeText,PanelChannel.Dummies=>panelDummyText,PanelChannel.Cue=>panelCueText,_=>panelDisplayText};
    private readonly Dictionary<int,(string Text,float At)> panelNoticeFallback=[];
    private string? panelDisplayFailure;
    private int panelDisplayWrites;
    private int panelDisplayCleanupFailures;
    private readonly HashSet<int> panelFallbackSlots=[];
    private bool panelFallbackLogged;
    private bool PanelResourceInstalled=>new[]{Server.GameDirectory,Path.Combine(Server.GameDirectory,"csgo"),Path.GetFullPath(Path.Combine(ModuleDirectory,"../../../.."))}
        .Any(root=>File.Exists(Path.Combine(root,PanelLayoutResource)) && File.Exists(Path.Combine(root,PanelBridgeResource)));

    private void RegisterPanelDisplay()
    {
        RegisterPanelBridge();
        RegisterListener<Listeners.OnServerPrecacheResources>(manifest=>
        {
            manifest.AddResource(PanelLayoutResource[..^2]);
            manifest.AddResource("panorama/styles/custom_game/nadelab_panel.vcss");
            manifest.AddResource(PanelBridgeResource[..^2]);
        });
        RegisterListener<Listeners.OnClientPutInServer>(slot=>
        {
            // Clear inherited per-slot state before the first normal HUD write.
            ClearPanelDisplay(slot);
        });
        RegisterEventHandler<EventPlayerSpawn>((ev,info)=>
        {
            if(ev.Userid is {IsValid:true} p)
            {
                ClearPanelDisplay(p.Slot);
                if(sessions.TryGetValue(p.Slot,out var s))
                {s.HudVisible=false;s.LastHudText=null;s.NextHudAt=0;s.LastHudSentAt=0;}
            }
            return HookResult.Continue;
        });
    }

    private CCSCustomHudLayout? ExistingPanelLayout()=>panelLayoutEpoch==grenadeEpoch ? panelLayout?.Value : null;

    private CCSCustomHudLayout EnsurePanelLayout(CCSPlayerController player)
    {
        if(panelDisplayFailure is { } failure) throw new InvalidOperationException(failure);
        if(ExistingPanelLayout() is {IsValid:true} current) return current;
        // Do not resolve/create an entity during plugin Load or cold OnMapStart:
        // the entity list may not have been initialized by the engine yet.
        if(!loaded || !Practice || !player.IsValid || player.PlayerPawn.Value is not {IsValid:true})
            throw new InvalidOperationException("NadeLab custom panel needs an active practice player.");
        CCSCustomHudLayout? created=null;
        try
        {
            created=Utilities.CreateEntityByName<CCSCustomHudLayout>("custom_hud_layout")
                ?? throw new InvalidOperationException("custom_hud_layout could not be created.");
            created.StrLayout=PanelLayoutResource;
            created.Entity!.Name="nadelab_panel_layout";
            created.Observable=false;
            created.DispatchSpawn();
            if(!created.IsValid) throw new InvalidOperationException("custom_hud_layout was invalid after spawn.");
            panelLayout=new(created.EntityHandle.Raw);panelLayoutEpoch=grenadeEpoch;
            panelDisplayText.Clear();panelNoticeText.Clear();panelDummyText.Clear();panelCueText.Clear();
            Logger.LogInformation("NadeLab custom panel entity {Entity} uses {Resource}; native state does not confirm client assets/pixels.",created.Index,PanelLayoutResource);
            return created;
        }
        catch(Exception ex)
        {
            TryPanelCleanup("failed creation",()=>{if(created?.IsValid==true) created.Remove();});
            panelDisplayFailure="NadeLab custom panel unavailable: "+ex.Message;
            Logger.LogError(ex,"Could not create NadeLab custom panel. Caller must use the plain center-text fallback.");
            throw;
        }
    }

    private void SendPanelText(CCSPlayerController player,string text,string keys="")
    {
        if(!player.IsValid) return;
        text=PanelText.Bounded(text);
        keys=PanelText.Shortcuts(keys);
        if(string.IsNullOrWhiteSpace(text)) {ClearPanelMain(player.Slot);return;}
        try
        {
            if(!PanelResourceInstalled) throw new InvalidOperationException("Compiled panel resources have not been installed on this server.");
            SendCustomPanelText(player,text,keys:keys);
            if(panelFallbackSlots.Remove(player.Slot)) TryPanelCleanup("fallback text",()=>player.PrintToCenter(" "));
        }
        catch(Exception ex)
        {
            if(!panelFallbackLogged) {Logger.LogWarning(ex,"Using bounded plain-text HUD until custom panel resources are ready");panelFallbackLogged=true;}
            panelFallbackSlots.Add(player.Slot);
            // A failure of the fallback itself must not abort TickCore before
            // search progression, or unwind an unrelated placement operation.
            TryPanelCleanup("fallback delivery",()=>player.PrintToCenter(PanelFallbackText(text,keys)));
        }
    }
    private static string PanelFallbackText(string text,string keys)=>
        string.IsNullOrEmpty(keys) ? PanelText.Bounded(text) : PanelText.WithKeys(text,keys);
    private void SendCustomPanelText(CCSPlayerController player,string text,PanelChannel channel=PanelChannel.Main,string keys="")
    {
        text=channel==PanelChannel.Dummies ? PanelText.Bounded(text,900,9) :
            channel==PanelChannel.Notice ? PanelText.Bounded(text,360,3) : channel==PanelChannel.Cue ? PanelText.Bounded(text,300,2) : PanelText.Bounded(text);
        keys=channel==PanelChannel.Main ? PanelText.Shortcuts(keys) : "";
        var layout=EnsurePanelLayout(player);
        uint owner=player.EntityHandle.Raw;
        var cache=PanelCache(channel);
        if(cache.TryGetValue(player.Slot,out var sent) && sent.Controller==owner && sent.Text==text && sent.Keys==keys) return;
        // The layout is display-only. No cursor capture, HTML events, HintText,
        // timers, or per-tick refreshes are involved. Values are per owner slot.
        WritePanelThroughEngine(layout,player,text,true,channel,keys);
        cache[player.Slot]=(owner,text,keys);panelDisplayWrites++;
    }

    private void SendNoticeText(CCSPlayerController player,string text)
    {
        if(!player.IsValid) return;
        text=PanelText.Bounded(text,360,3);
        if(string.IsNullOrWhiteSpace(text)) {ClearPanelChannel(player.Slot,PanelChannel.Notice);return;}
        try
        {
            if(!PanelResourceInstalled) throw new InvalidOperationException("Panel resources missing.");
            SendCustomPanelText(player,text,PanelChannel.Notice);
        }
        catch(Exception ex)
        {
            if(!panelFallbackLogged) {Logger.LogWarning(ex,"Using chat for separate notifications until panel resources are ready");panelFallbackLogged=true;}
            NoticeFallback(player,text);
        }
    }
    private void NoticeFallback(CCSPlayerController player,string text)
    {
        if(player.IsBot || string.IsNullOrWhiteSpace(text)) return;
        if(panelNoticeFallback.TryGetValue(player.Slot,out var last) && (last.Text==text || Server.CurrentTime-last.At<1)) return;
        panelNoticeFallback[player.Slot]=(text,Server.CurrentTime);
        TryPanelCleanup("notification fallback",()=>player.PrintToChat("[NadeLab] "+PanelText.Line(text,90)));
    }
    private void SendDummyPanelText(CCSPlayerController player,string? text)
    {
        if(!player.IsValid) return;
        text=PanelText.Bounded(text,900,9);
        if(string.IsNullOrWhiteSpace(text)) {ClearPanelChannel(player.Slot,PanelChannel.Dummies);panelDummyNextWrite.Remove(player.Slot);return;}
        if(panelDummyNextWrite.GetValueOrDefault(player.Slot)>Server.CurrentTime) return;
        panelDummyNextWrite[player.Slot]=Server.CurrentTime+.25f;
        try
        {
            if(!PanelResourceInstalled) throw new InvalidOperationException("Panel resources missing.");
            SendCustomPanelText(player,text,PanelChannel.Dummies);
        }
        catch(Exception ex)
        {
            if(!panelFallbackLogged) {Logger.LogWarning(ex,"Dummy status panel unavailable; client UI update required");panelFallbackLogged=true;}
            // Do not send a changing multi-target roster to chat or overwrite controls.
            DummyPanelFallback(player);
        }
    }
    private void DummyPanelFallback(CCSPlayerController player)=>
        NoticeFallback(player,"假人状态栏不可用，请更新配套UI并重开CS2。菜单仍可查看与管理假人。");
    private void ClearPanelMain(int slot)=>ClearPanelChannel(slot,PanelChannel.Main);
    private void ClearPanelDisplay(int slot)
    {
        var channels=Enum.GetValues<PanelChannel>();
        var visible=channels.ToDictionary(c=>c,c=>panelLatestDelivery.TryGetValue((slot,c),out var latest) &&
            (latest.Delivery.Show || panelDeliveries.ContainsKey(latest.Sequence)));
        ForgetPanelDeliveries(slot);
        foreach(var channel in channels) ClearPanelChannel(slot,channel,visible[channel]);
        panelNoticeFallback.Remove(slot);panelDummyNextWrite.Remove(slot);
    }
    private void ClearPanelChannel(int slot,PanelChannel channel,bool force=false)
    {
        bool hadText=PanelCache(channel).Remove(slot);
        bool fallback=channel==PanelChannel.Main && panelFallbackSlots.Remove(slot);
        bool couldBeVisible=panelLatestDelivery.TryGetValue((slot,channel),out var latest) && latest.Delivery.Show;
        if(!hadText && !fallback && !couldBeVisible && !force) return;
        TryPanelCleanup($"slot {slot}",()=>
        {
            if(Utilities.GetPlayerFromSlot(slot) is not {IsValid:true} player) return;
            if(fallback) TryPanelCleanup("fallback text",()=>player.PrintToCenter(" "));
            if(ExistingPanelLayout() is not {IsValid:true} layout) return;
            TryPanelCleanup("native display reset",()=>WritePanelThroughEngine(layout,player,"",false,channel));
        });
    }

    private void ClearPanelDisplays()
    {
        // Save before forgetting; resolving the serial-bearing handle protects
        // against touching another entity that reused the same index.
        try
        {
            // Removing the layout clears every owner. Do not queue updates to
            // the script which will be removed in this same frame.
            foreach(int slot in panelFallbackSlots.ToArray())
                TryPanelCleanup("fallback text",()=>{if(Utilities.GetPlayerFromSlot(slot) is {IsValid:true,IsBot:false} p) p.PrintToCenter(" ");});
            TryPanelCleanup("entity",()=>{if(ExistingPanelLayout() is {IsValid:true} layout) layout.Remove();});
            ClearPanelBridge();
        }
        finally
        {
            panelLayout=null;panelLayoutEpoch=-1;panelDisplayText.Clear();panelNoticeText.Clear();panelDummyText.Clear();panelCueText.Clear();panelNoticeFallback.Clear();panelDummyNextWrite.Clear();
            panelDisplayFailure=null;panelDisplayWrites=0;
            panelFallbackSlots.Clear();panelFallbackLogged=false;
        }
    }

    private void TryPanelCleanup(string part,Action action)
    {
        try {action();}
        catch(Exception ex)
        {
            panelDisplayCleanupFailures++;
            Logger.LogWarning(ex,"NadeLab panel {Part} failed; continuing gameplay/search and other cleanup",part);
        }
    }

    private string PanelDisplayDiagnostics=>
        $"custom-hud entity={ExistingPanelLayout()?.Index.ToString() ?? "none"}; owners={panelDisplayText.Count}; writes={panelDisplayWrites}; cleanupFailures={panelDisplayCleanupFailures}; resource={PanelLayoutResource}; failure={panelDisplayFailure ?? "none"}; client pixels unverified";
}
