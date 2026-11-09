using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private void RegisterPredictionDiagnostic()=>AddCommand("css_nl_predictioncheck","Empty-server read-only effect-estimate checks",(caller,command)=>
    {
        if(caller is not null) return;
        var owner=effectDummies.Values.Where(d=>d.Live && d.Ready && !d.Removing).Select(DummyOwner)
            .FirstOrDefault(p=>p is {IsValid:true,IsBot:true} && p.PlayerPawn.Value is {IsValid:true,LifeState:0});
        if(owner is null) {Say(null,"PREDICTIONCHECK refused: a living test bot with an owned ready dummy is required.");return;}
        RunPredictionDiagnostic(owner);
    });

    // Can also be invoked synchronously from the dummy diagnostic while its
    // isolated owner and targets exist. No grenade or gameplay effect is created.
    private bool RunPredictionDiagnostic(CCSPlayerController owner)
    {
        if(!loaded || !Practice || Utilities.GetPlayers().Any(p=>!p.IsBot) || !owner.IsValid || !owner.IsBot ||
            owner.PlayerPawn.Value is not {IsValid:true,LifeState:0} source || job is not null || flashJob is not null || flashSearchCheckPending || flashVerificationCheckPending || DummyLayoutLoadActive ||
            grenadeFlights.Count>0 || flights.Count>0 || source.AbsOrigin is null) return false;
        var dummies=effectDummies.Values.Where(d=>d.Owner==owner.EntityHandle.Raw && d.Ready && d.Live && !d.Removing &&
            d.Bot.PlayerPawn.Value is {IsValid:true,LifeState:0}).Take(MaxPlayerDummies).ToArray();
        if(dummies.Length==0) return false;
        sessions.TryGetValue(owner.Slot,out var previous);
        if(previous?.Placement is not null) return false;
        var oldOrigin=NativeWorld.FromNative(source.AbsOrigin);var velocity=NativeWorld.FromNative(source.AbsVelocity);
        var move=source.MoveType;var actualMove=source.ActualMoveType;
        var nativeBefore=dummies.Select(d=>(Pawn:d.Bot.PlayerPawn.Value!,Health:d.Bot.PlayerPawn.Value!.Health,
            Armor:d.Bot.PlayerPawn.Value!.ArmorValue,Blind:d.Bot.PlayerPawn.Value!.FlashDuration)).ToArray();
        int passed=0;
        void Require(bool ok,string detail) {if(!ok) throw new InvalidOperationException(detail);passed++;Say(null,"PREDICTIONCHECK PASS: "+detail);}
        try
        {
            var center=new[]{64f,96f,128f}.Select(z=>oldOrigin+Vector3.UnitZ*z)
                .Select(point=>EffectPlacement.Resolve(new NativeWorld(source),GrenadeKind.Flash,point)).FirstOrDefault(point=>point.Valid);
            if(!center.Valid) throw new InvalidOperationException("no nearby clear test grenade centre");
            SetMovement(source,MoveType_t.MOVETYPE_NONE,MoveType_t.MOVETYPE_NONE);source.Teleport(position:center.Position,velocity:Vector3.Zero);
            var draft=new PlacementDraft(source.EntityHandle.Raw,grenadeEpoch,oldOrigin,source.ViewOffset.Z,new(source.EyeAngles.X,source.EyeAngles.Y,ThrowMode.Left),
                move,actualMove,NextAttack(source),null,null) {Point=center,Area=true};
            var session=new Session {SteamId=owner.SteamID,Kind=GrenadeKind.Flash,Placement=draft};sessions[owner.Slot]=session;
            foreach(var kind in new[]{GrenadeKind.Flash,GrenadeKind.HighExplosive})
            {
                session.Kind=kind;RemoveDummyPredictions(owner.Slot);
                foreach(var dummy in dummies)
                {
                    string? text=DummyPredictionText(owner,dummy.Bot);
                    Require(text is not null && text.Contains(kind==GrenadeKind.Flash ? "预测致盲" : "预测掉血") && text.Contains("粗估") && text.Length<=32,
                        $"{kind} target {dummy.Number}: bounded estimate text, actual grenade origin used");
                }
                Require(dummyPredictions[owner.Slot].Samples.Count==dummies.Length,"every owned target has its own prediction sample");
                long rays=effectRayCount;
                foreach(var dummy in dummies) DummyPredictionText(owner,dummy.Bot);
                Require(effectRayCount==rays,"repeated reads in one sample interval issue no effect rays");
                Require(dummyPredictions[owner.Slot].Samples.Values.All(s=>s.Center==NativeWorld.FromNative(source.AbsOrigin!)),"source is pawn AbsOrigin, not camera/view offset");
            }
            draft.Area=false;
            Require(dummies.All(d=>DummyPredictionText(owner,d.Bot) is null) && !dummyPredictions.ContainsKey(owner.Slot),"F4 off clears prediction cache/output");
            draft.Area=true;session.Placement=null;
            Require(dummies.All(d=>DummyPredictionText(owner,d.Bot) is null),"leaving placement removes prediction output");
            Require(nativeBefore.All(s=>s.Pawn.IsValid && s.Pawn.Health==s.Health && s.Pawn.ArmorValue==s.Armor && s.Pawn.FlashDuration==s.Blind),
                "prediction changed neither real health/armor nor native flash duration");
            Say(null,$"PREDICTIONCHECK DONE PASS: {passed} passed; no effects/projectiles created; client pixels unverified.");return true;
        }
        catch(Exception ex) {Say(null,$"PREDICTIONCHECK DONE FAIL: passed={passed}; {ex.Message}");return false;}
        finally
        {
            RemoveDummyPredictions(owner.Slot);
            if(previous is null) sessions.Remove(owner.Slot);else sessions[owner.Slot]=previous;
            if(source.IsValid) {SetMovement(source,move,actualMove);source.Teleport(position:oldOrigin,velocity:velocity);}
        }
    }
}
