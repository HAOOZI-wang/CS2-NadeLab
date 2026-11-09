using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private sealed record PanelOption(string Text,Action<CCSPlayerController,PanelOption> Callback,bool Disabled,string? Hint=null,string? Key=null,Func<bool>? Unavailable=null)
    {public bool IsDisabled=>Disabled || Unavailable?.Invoke()==true;}
    private sealed class PanelMenu(NadeLabPlugin plugin,string title)
    {
        public string Title=title;
        public string Identity=title;
        public int? RestoreCount;
        public bool RememberSelection=true;
        public bool ExitButton=true,IsRoot,AllowPlacement,InputCheck;
        public Action<CCSPlayerController>? Back;
        public Func<string?>? Feedback;
        public string[]? TextPages;
        public readonly List<PanelOption> Options=[];
        public void AddMenuOption(string text,Action<CCSPlayerController,PanelOption> callback,bool disabled=false,string? hint=null,string? key=null,Func<bool>? unavailable=null)
            =>Options.Add(new(text,callback,disabled,hint,key,unavailable));
        public void Open(CCSPlayerController player)=>plugin.OpenPanel(player,this);
    }
    private sealed class PanelState(PanelMenu menu,PlayerActionIdentity identity,int revision)
    {
        public readonly PanelMenu Menu=menu;
        public readonly PlayerActionIdentity Identity=identity;
        public readonly int Revision=revision;
        public int Selected;
        public InputProbeSource ObservedInputs;
    }
    private readonly Dictionary<int,PanelState> panels=[];
    private sealed record PanelMemory(uint Controller,ulong Steam,int Epoch,MenuNavigation Navigation);
    private readonly Dictionary<int,PanelMemory> panelMemory=[];
    private MenuNavigation Navigation(CCSPlayerController p)
    {
        if(!panelMemory.TryGetValue(p.Slot,out var saved) || saved.Controller!=p.EntityHandle.Raw || saved.Steam!=p.SteamID || saved.Epoch!=grenadeEpoch)
            panelMemory[p.Slot]=saved=new(p.EntityHandle.Raw,p.SteamID,grenadeEpoch,new());
        return saved.Navigation;
    }
    private static string[] PanelItems(PanelMenu menu)=>menu.TextPages is { } pages
        ? Enumerable.Range(0,pages.Length).Select(i=>i.ToString()).ToArray()
        : menu.Options.Select(o=>o.Key ?? o.Text).ToArray();
    private void RememberPanel(CCSPlayerController p)
    {
        if(panels.TryGetValue(p.Slot,out var state) && state.Menu.RememberSelection &&
            state.Identity.Controller==p.EntityHandle.Raw && state.Identity.Steam==p.SteamID && state.Identity.Epoch==grenadeEpoch)
            Navigation(p).Remember(state.Menu.Identity,PanelItems(state.Menu),state.Selected);
    }
    private int? informationPlayer;
    private List<string>? informationCapture;
    private PanelMenu NewPanel(string title,[System.Runtime.CompilerServices.CallerMemberName]string identity="")=>new(this,title){Identity=identity};
    private void ShowDetails(CCSPlayerController p,string title,Action details,Action<CCSPlayerController>? back=null)
    {
        var priorPlayer=informationPlayer;var priorCapture=informationCapture;
        var messages=new List<string>();informationPlayer=p.Slot;informationCapture=messages;
        try {details();}
        finally {informationPlayer=priorPlayer;informationCapture=priorCapture;}
        var menu=NewPanel(title);menu.Identity+="/"+title;menu.Back=back;menu.AllowPlacement=Get(p).Placement is not null;menu.TextPages=PanelText.Pages(messages);menu.Open(p);
    }
    private void RegisterPanelControls()
    {
        RegisterPanelInput();
        RegisterMapControls();
        RegisterListener<Listeners.OnClientDisconnect>(slot=>panelMemory.Remove(slot));
        RegisterListener<Listeners.OnMapEnd>(()=>panelMemory.Clear());
        AddCommand("css_nl_panel_up","Previous panel item",(p,c)=>ReceivePanelDirection(p,-1));
        AddCommand("css_nl_panel_down","Next panel item",(p,c)=>ReceivePanelDirection(p,1));
        AddPlayerCommand("css_nl_panel_select","Select current panel item",(p,a)=>ActivatePanel(p));
        AddPlayerCommand("css_nl_panel_back","Back from panel",(p,a)=>BackPanel(p));
    }
    private void OpenPanel(CCSPlayerController player,PanelMenu menu)
    {
        if(!Check(player,out _,allowPlacement:menu.AllowPlacement)) return;
        OpenPanelCore(player,menu);
    }
    // Called by the gated zero-human diagnostic as well as the player adapter.
    private void OpenPanelCore(CCSPlayerController player,PanelMenu menu)
    {
        var s=Get(player);
        RememberPanel(player);
        panels[player.Slot]=new(menu,ActionIdentity(player),s.Revision)
            {Selected=menu.RememberSelection ? Navigation(player).Restore(menu.Identity,PanelItems(menu),menu.RestoreCount) : 0};
        s.NextHudAt=0;s.LastHudSentAt=0;s.LastHudText=null;s.FeedbackUrgent=true;
        if(!player.IsBot) SyncMenuEquipment(player);
    }
    private void ClosePanel(CCSPlayerController? player)
    {
        if(player?.IsValid!=true) return;
        RememberPanel(player);
        panels.Remove(player.Slot);
        RestoreMenuEquipment(player.Slot);
        menuEquipWindows.Remove(player.Slot);
        
        ClearPanelMain(player.Slot);
        if(sessions.TryGetValue(player.Slot,out var s)) {s.NextHudAt=0;s.LastHudText=null;s.LastHudSentAt=0;s.HudVisible=false;s.FeedbackUrgent=true;}
    }
    private bool CurrentPanel(CCSPlayerController? player,out PanelState state)
    {
        state=null!;
        if(player?.IsValid!=true || !panels.TryGetValue(player.Slot,out var current)) return false;
        if(!Practice || player.PlayerPawn.Value is not {IsValid:true,LifeState:0} || ThrowInProgress(player) ||
            !current.Identity.Matches(ActionIdentity(player)) || !sessions.TryGetValue(player.Slot,out var s) || s.Revision!=current.Revision)
        {ClosePanel(player);return false;}
        state=current;return true;
    }
    private bool HasPanel(CCSPlayerController? p)=>CurrentPanel(p,out _);
    private void TogglePanel(CCSPlayerController? p)
    {if(p?.IsValid!=true) return;if(panels.ContainsKey(p.Slot)) ClosePanel(p);else OpenMenu(p);}
    private void MovePanel(CCSPlayerController? p,int delta)
    {
        if(!CurrentPanel(p,out var panel)) return;
        panel.Selected=PanelText.Move(panel.Selected,panel.Menu.TextPages?.Length ?? panel.Menu.Options.Count,delta);
        Get(p!).NextHudAt=0;Get(p!).LastHudSentAt=0;Get(p!).FeedbackUrgent=true;
    }
    private void ActivatePanel(CCSPlayerController? p)
    {
        if(!CurrentPanel(p,out var panel) || panel.Menu.Options.Count==0) return;
        var option=panel.Menu.Options[panel.Selected];
        if(option.IsDisabled) {HudNotice(Get(p!),option.Hint ?? "此项尚不可用，请先完成所需步骤。",3);return;}
        ClosePanel(p);
        // Business callbacks retain their own revision/result guards. The panel
        // is consumed once; a held key cannot execute the old selection twice.
        option.Callback(p!,option);
    }
    private void BackPanel(CCSPlayerController? p)
    {
        if(!CurrentPanel(p,out var panel)) return;
        ClosePanel(p);
        if(!panel.Menu.IsRoot) (panel.Menu.Back ?? OpenMenu)(p!);
    }
    private bool RenderPanel(CCSPlayerController p,Session s,float now)
    {
        if(!CurrentPanel(p,out var panel)) return false;
        string text=panel.Menu.TextPages is { } pages ?
            $"{PanelText.Line(panel.Menu.Title,22)}  {panel.Selected+1}/{pages.Length}\n{pages[panel.Selected]}" :
            PanelText.MenuBody(panel.Menu.Title,panel.Menu.Options.Select(o=>(o.IsDisabled ? "[暂不可用] " : "")+o.Text).ToArray(),panel.Selected,panel.Menu.IsRoot);
        string keys=WorkflowKeys.Menu(ResolveWorkflow(p,s),panel.Menu.TextPages is not null,panel.Menu.IsRoot);
        SendBottomHud(p,s,text,now,keys:keys);
        return true;
    }
    private string? MenuFeedback(CCSPlayerController p)
    {
        if(!CurrentPanel(p,out var panel)) return null;
        var hint=panel.Menu.Options.ElementAtOrDefault(panel.Selected)?.Hint;
        return !string.IsNullOrWhiteSpace(hint) ? hint : panel.Menu.Feedback?.Invoke();
    }
    private void SendBottomHud(CCSPlayerController p,Session s,string text,float now,bool urgent=false,string keys="",string? nativeKeys=null)
    {
        if(HasCompanion(p))
        {
            bool menu=CurrentPanel(p,out var panel);
            keys=CompanionKeys(p,s,menu,menu && panel.Menu.IsRoot);
        }
        else if(!s.LegacyControls)
        {
            bool menu=CurrentPanel(p,out var panel);
            (text,keys)=NativeControlText.Panel(text,ResolveWorkflow(p,s),menu,menu && panel.Menu.IsRoot);
            if(!menu && nativeKeys is not null) keys=nativeKeys;
        }
        text=PanelText.Bounded(text);keys=PanelText.Shortcuts(keys);
        if(!urgent && (now-s.LastHudSentAt<1 || (text==s.LastHudText && keys==s.LastHudKeys && now-s.LastHudSentAt<1.5f))) return;
        SendPanelText(p,text,keys);s.HudVisible=true;s.LastHudText=text;s.LastHudKeys=keys;s.LastHudSentAt=now;
    }
}
