using System.Numerics;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private Session? preparationContinuationSession;
    private string? preparationContinuationOutcome;
    private Action? stopPreparationContinuationCheck;
    private void StopPreparationContinuationDiagnostic()=>stopPreparationContinuationCheck?.Invoke();

    // Replays the real preparation checks, without allowing a bot through the
    // human-only command/recording guard or writing diagnostic throw evidence.
    private void PreparationContinuationDiagnostic(CCSPlayerController? caller,CommandInfo command)
    {
        if(caller is not null || !loaded || !Practice || !BenchmarkIdle || stopPreparationContinuationCheck is not null ||
            Utilities.GetPlayers().Any(p=>!p.IsBot))
        {Say(caller,"PREPARECONTINUE refused: empty idle practice server console required.");return;}
        if(ConVar.Find("bot_stop")?.StringValue is not ("1" or "true"))
        {Say(null,"PREPARECONTINUE refused: set bot_stop 1 for the empty-server test; native bot movement must stay enabled so gravity can settle the pawn.");return;}
        int selection=1;
        if(command.ArgCount>3 || command.ArgCount==3 && (!int.TryParse(command.ArgByIndex(2),out selection) || selection<1))
        {Say(null,"PREPARECONTINUE usage: css_nl_preparecheck continuation [candidate number]");return;}
        var owner=Utilities.GetPlayers().FirstOrDefault(p=>p.IsBot && p.IsValid &&
            p.PlayerPawn.Value is {IsValid:true,LifeState:0} && !ThrowInProgress(p));
        if(owner?.PlayerPawn.Value is not {IsValid:true,LifeState:0,WeaponServices:{ } weapons} pawn)
        {Say(null,"PREPARECONTINUE refused: one existing living idle bot is required.");return;}
        sessions.TryGetValue(owner.Slot,out var original);
        if(original?.Continuation is not null || HasPanel(owner) || flashVerification.ContainsKey(owner.Slot))
        {Say(null,"PREPARECONTINUE refused: selected bot has a retained search, panel or flash verification.");return;}

        Session sandbox;
        string recordedUtc;
        try
        {
            string file=Path.Combine(DataPath,"searches.jsonl");
            if(!File.Exists(file)) throw new InvalidOperationException("no recorded searches");
            JsonDocument? selected=null;
            foreach(string line in BoundedLogTail.ReadLines(file).Reverse())
            {
                var document=JsonDocument.Parse(line);var record=document.RootElement;
                if(record.TryGetProperty("Continuations",out var count) && count.GetInt32()>0 &&
                    record.TryGetProperty("Actor",out var actor) && actor.GetString()=="human" && ReplayWorldMatches(record))
                {selected=document;break;}
                document.Dispose();
            }
            using var recordDocument=selected ?? throw new InvalidOperationException("no human continuation matching the current map/build in the bounded log tail");
            var root=recordDocument.RootElement;
            var result=SearchPresentation.ForPlayer(root.GetProperty("Result").Deserialize<SearchResult>(Json)!);
            if(selection>result.Matches.Count) throw new InvalidOperationException($"latest continuation has {result.Matches.Count} selectable candidates");
            var profile=root.GetProperty("Physics").Deserialize<PhysicsProfile>(Json)!;
            recordedUtc=root.GetProperty("Utc").GetString()!;
            sandbox=new Session {SteamId=owner.SteamID,Kind=profile.Kind,SearchPhysics=profile,
                Start=root.GetProperty("Start").Deserialize<ThrowStart>(Json),Target=root.GetProperty("Target").Deserialize<Target>(Json),
                Result=result,Selected=selection,HudEnabled=false,GuideEnabled=false,FlightPreviewEnabled=false};
            sandbox.RequestedStart=sandbox.Start;
        }
        catch(Exception ex) {Say(null,"PREPARECONTINUE refused: "+ex.Message);return;}

        int slot=owner.Slot,epoch=grenadeEpoch;
        uint controller=owner.EntityHandle.Raw,pawnHandle=pawn.EntityHandle.Raw;
        ulong steam=owner.SteamID;
        var oldFeet=NativeWorld.FromNative(pawn.AbsOrigin!);var oldVelocity=NativeWorld.FromNative(pawn.AbsVelocity);
        var oldView=new Aim(pawn.EyeAngles.X,pawn.EyeAngles.Y,ThrowMode.Left);
        var oldMove=pawn.MoveType;var oldActualMove=pawn.ActualMoveType;
        bool oldBot=pawn.BotAllowActive;bool? oldAi=pawn.Bot?.AllowActive;
        uint oldActive=weapons.ActiveWeapon.Raw,oldLast=weapons.LastWeapon.Raw;
        var oldHandles=weapons.MyWeapons.Select(h=>h.Raw).ToHashSet();var oldAmmo=weapons.Ammo.ToArray();
        bool finished=false;
        void Finish(string outcome)
        {
            if(finished) return;finished=true;
            stopPreparationContinuationCheck=null;preparationContinuationSession=null;preparationContinuationOutcome=null;
            var failures=new List<string>();
            void Restore(string name,Action action)
            {try {action();}catch(Exception ex) {failures.Add(name+": "+ex.Message);}}
            try
            {
                if(grenadeEpoch==epoch && Utilities.GetPlayerFromSlot(slot) is {IsValid:true,IsBot:true} current &&
                    current.EntityHandle.Raw==controller && current.SteamID==steam &&
                    current.PlayerPawn.Value is {IsValid:true,LifeState:0,WeaponServices:{ } inventory} currentPawn && currentPawn.EntityHandle.Raw==pawnHandle)
                {
                    Restore("inventory",()=>
                    {
                        if(ReadTrainingGrenades(currentPawn,out var held))
                            foreach(var item in held.Where(item=>!oldHandles.Contains(item.Weapon.EntityHandle.Raw)))
                                if(!RemoveTrainingGrenade(inventory,item)) throw new InvalidOperationException("could not detach diagnostic grenade");
                        oldAmmo.AsSpan().CopyTo(inventory.Ammo);MarkBodyComponent(inventory,"CPlayer_WeaponServices","m_iAmmo");
                        SetBodyActiveWeapon(inventory,oldActive);inventory.LastWeapon.Raw=oldLast;
                        MarkBodyComponent(inventory,"CPlayer_WeaponServices","m_hLastWeapon");
                    });
                    Restore("position/view",()=>{currentPawn.Teleport(position:oldFeet,velocity:oldVelocity);SetDiagnosticView(currentPawn,oldView);});
                    Restore("movement",()=>SetMovement(currentPawn,oldMove,oldActualMove));
                    Restore("bot AI",()=>{currentPawn.BotAllowActive=oldBot;if(oldAi is { } active && currentPawn.Bot is { } ai) ai.AllowActive=active;});
                }
            }
            finally
            {
                Restore("diagnostic HUD",()=>
                {
                    if(grenadeEpoch==epoch && sessions.TryGetValue(slot,out var active) && ReferenceEquals(active,sandbox))
                        ClearPanelDisplay(slot);
                });
                Restore("session",()=>
                {
                    if(grenadeEpoch==epoch && sessions.TryGetValue(slot,out var active) && ReferenceEquals(active,sandbox))
                    {
                        if(original is null) sessions.Remove(slot);
                        else
                        {
                            sessions[slot]=original;
                            original.HudVisible=false;original.LastHudText=null;original.LastHudSentAt=0;original.NextHudAt=0;
                            original.NextFeedbackAt=0;original.FeedbackUrgent=true;
                        }
                    }
                });
                Say(null,$"PREPARECONTINUE {outcome}; cleanup={(failures.Count==0 ? "restored" : string.Join(" | ",failures))}; native readiness only, human input/client correction and real throw remain unverified.");
            }
        }
        stopPreparationContinuationCheck=()=>Finish("CANCELLED by lifecycle reset");
        preparationContinuationSession=sandbox;preparationContinuationOutcome=null;
        sessions[slot]=sandbox;
        try
        {
            // Disabling these switches also prevents the bot's native movement
            // simulation from settling a raised teleport onto the floor.
            // As in PlacementDiagnostic, bot_stop stops decisions while the
            // per-pawn movement path remains active for real grounded flags.
            SetDiagnosticBotActive(pawn,true);
            SetMovement(pawn,MoveType_t.MOVETYPE_WALK,MoveType_t.MOVETYPE_WALK);
            var candidate=sandbox.Result!.Matches[selection-1];var start=sandbox.Start!.Value;
            var destination=candidate.Aim.NeedsRunUp ? FindPreparation(pawn,start,candidate.Aim,sandbox.SearchPhysics!) : start.Feet;
            if(destination is null) throw new InvalidOperationException("current FindRunUp rejected the recorded candidate");
            if(!ReturnToStart(owner,pawn,destination.Value)) throw new InvalidOperationException("current ReturnToStart rejected the recorded candidate");
            if(!TryAlignPreparation(owner,pawn,sandbox,candidate.Aim)) throw new InvalidOperationException("current AlignView rejected the recorded candidate");
            if(!weapons.MyWeapons.Any(h=>h.Value is {IsValid:true} item && item.DesignerName==sandbox.Kind.Weapon()))
                owner.GiveNamedItem(sandbox.Kind.Weapon());
            var grenade=weapons.MyWeapons.Select(h=>h.Value).FirstOrDefault(w=>w is {IsValid:true} && w.DesignerName==sandbox.Kind.Weapon())
                ?? throw new InvalidOperationException("bot could not receive the selected grenade");
            SetBodyActiveWeapon(weapons,grenade.EntityHandle.Raw);
            BindPreparation(owner,sandbox);
            sandbox.Pending=new(selection,sandbox.Revision,NativeWorld.FromNative(pawn.AbsOrigin!),Server.CurrentTime);
            float since=Server.CurrentTime;
            bool settledView=false;
            Say(null,$"PREPARECONTINUE START: recorded={recordedUtc}; slot={slot}; selected={selection}/{sandbox.Result.Matches.Count}; start={start.Feet}; recordedRunUp={candidate.RunUpFeet}; currentRunUp={destination}; teleport={pawn.AbsOrigin}; aim={candidate.Aim}; expected=recording-refused after stable readiness (bot remains disallowed).");
            void Poll()
            {
                if(finished) return;
                try
                {
                    if(!loaded || !Practice || grenadeEpoch!=epoch || Utilities.GetPlayers().Any(p=>!p.IsBot) ||
                        Utilities.GetPlayerFromSlot(slot) is not {IsValid:true,IsBot:true} current || current.EntityHandle.Raw!=controller || current.SteamID!=steam ||
                        current.PlayerPawn.Value is not {IsValid:true,LifeState:0} currentPawn || currentPawn.EntityHandle.Raw!=pawnHandle ||
                        !sessions.TryGetValue(slot,out var session) || !ReferenceEquals(session,sandbox))
                    {Finish("CANCELLED: practice/map/player/session guard changed");return;}
                    if(preparationContinuationOutcome is { } outcome)
                    {Finish(outcome=="recording-refused" ? "PASS: all preparation gates stable; bot recording guard retained" : "FAIL: "+outcome+"; "+sandbox.PreparationIssue);return;}
                    if(!settledView && GroundedUpright(currentPawn))
                    {
                        // bot_stop still permits head scanning. Once real
                        // movement has acquired floor support, freeze the bot
                        // and supply its measured view without another Teleport
                        // (which would invalidate that newly acquired support).
                        // The earlier native AlignView result remains logged;
                        // this fixture does not verify human client correction.
                        SetDiagnosticBotActive(currentPawn,false);
                        var eye=currentPawn.EyeAngles;eye.X=candidate.Aim.Pitch;eye.Y=candidate.Aim.Yaw;eye.Z=0;
                        var view=currentPawn.V_angle;view.X=candidate.Aim.Pitch;view.Y=candidate.Aim.Yaw;view.Z=0;
                        settledView=true;
                        Say(null,$"PREPARECONTINUE BOT VIEW: genuine ground acquired at {currentPawn.AbsOrigin}; flags=0x{currentPawn.Flags:X}; scripted bot view fixed after native alignment; no grounded flag or pose gate was overridden.");
                    }
                    if(sandbox.Pending is null || Server.CurrentTime-since>7)
                    {Finish("FAIL: preparation disappeared or no outcome before deadline; "+sandbox.PreparationIssue);return;}
                    AddTimer(.1f,Poll);
                }
                catch(Exception ex) {Finish("FAIL: "+ex.Message);}
            }
            AddTimer(.1f,Poll);
        }
        catch(Exception ex) {Finish("FAIL: "+ex.Message);}
    }
}
