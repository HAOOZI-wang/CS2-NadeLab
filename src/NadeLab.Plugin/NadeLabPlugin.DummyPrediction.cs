using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using NadeLab.Core;
using TraceOptions=CounterStrikeSharp.API.Modules.Utils.TraceOptions;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private sealed class DummyPredictionOwner
    {
        public required uint Controller,Pawn;
        public required ulong Steam;
        public required int Epoch,Revision;
        public readonly Dictionary<uint,DummyPredictionSample> Samples=[];
    }
    private sealed record DummyPredictionSample(uint Pawn,GrenadeKind Kind,Vector3 Center,Vector3 Eye,Aim View,
        int Health,int Armor,float At,EffectEstimate Estimate);
    private readonly Dictionary<int,DummyPredictionOwner> dummyPredictions=[];
    private void ClearDummyPredictions()=>dummyPredictions.Clear();
    private void RemoveDummyPredictions(int slot)=>dummyPredictions.Remove(slot);
    private static readonly TraceOptions DummyFlashSight=new()
    {
        InteractsWith=Contents.Solid|Contents.Window|Contents.BlockLos|Contents.BlockLight|Contents.CsgoOpaque|Contents.PhysicsProp,
        InteractsExclude=Contents.Player|Contents.Npc|Contents.Hitbox|Contents.PlayerClip|Contents.CsgoGrenadeClip
    };
    private static readonly TraceOptions DummyBlastSight=new()
    {
        InteractsWith=Contents.Solid|Contents.Window|Contents.PassBullets|Contents.PhysicsProp,
        InteractsExclude=Contents.Player|Contents.Npc|Contents.Hitbox|Contents.PlayerClip|Contents.CsgoGrenadeClip
    };

    private string? DummyPredictionText(CCSPlayerController owner,CCSPlayerController bot,bool compact=false)
    {
        if(!loaded || !Practice || !owner.IsValid || !sessions.TryGetValue(owner.Slot,out var session) || session.SteamId!=owner.SteamID ||
            !session.Kind.Timed() || session.Placement is not {Area:true} draft || !SamePlacementPawn(owner,draft,out var source))
        {if(owner.IsValid) dummyPredictions.Remove(owner.Slot);return null;}
        if(FindDummy(bot) is not {Ready:true} dummy || dummy.Owner!=owner.EntityHandle.Raw ||
            bot.PlayerPawn.Value is not {IsValid:true,LifeState:0} target || target.EntityHandle.Raw!=dummy.Pawn) return null;
        uint ownerHandle=owner.EntityHandle.Raw,pawnHandle=source.EntityHandle.Raw;
        if(!dummyPredictions.TryGetValue(owner.Slot,out var cache) || cache.Controller!=ownerHandle || cache.Pawn!=pawnHandle ||
            cache.Steam!=owner.SteamID || cache.Epoch!=grenadeEpoch || cache.Revision!=session.Revision)
            dummyPredictions[owner.Slot]=cache=new() {Controller=ownerHandle,Pawn=pawnHandle,Steam=owner.SteamID,Epoch=grenadeEpoch,Revision=session.Revision};
        // The ownership/full-handle guard prevents slot reuse or another
        // player's dummy from consuming this player's target allowance.
        foreach(uint key in cache.Samples.Keys.Where(key=>!effectDummies.TryGetValue(key,out var item) || !item.Live || item.Owner!=ownerHandle || item.Removing).ToArray())
            cache.Samples.Remove(key);
        if(!cache.Samples.ContainsKey(dummy.Handle) && cache.Samples.Count>=MaxPlayerDummies) return null;
        Vector3 center=NativeWorld.FromNative(source.AbsOrigin!),feet=NativeWorld.FromNative(target.AbsOrigin!);
        var eye=feet+new Vector3(target.ViewOffset.X,target.ViewOffset.Y,target.ViewOffset.Z);
        var view=new Aim(target.EyeAngles.X,target.EyeAngles.Y,ThrowMode.Left);
        float now=Server.CurrentTime;
        if(cache.Samples.TryGetValue(dummy.Handle,out var prior) && now-prior.At<.25f)
        {
            // Four-Hz sample-and-hold during normal movement. Discontinuous
            // changes must not show a prediction for a replaced pawn, another
            // grenade or a distant old room while waiting for the next sample.
            if(prior.Pawn!=target.EntityHandle.Raw || prior.Kind!=session.Kind || prior.Health!=target.Health || prior.Armor!=target.ArmorValue ||
                Vector3.Distance(prior.Center,center)>64 || Vector3.Distance(prior.Eye,eye)>8 || SceneAdvisor.ViewTurn(prior.View,view)>25)
                return compact ? "预更新中" : "预测更新中（未校准）";
            return compact ? DummyRoster.Prediction(prior.Estimate) : prior.Estimate.Text;
        }
        bool valid=Geometry.Finite(center) && Geometry.Finite(eye);
        var point=valid ? EffectPlacement.Resolve(new NativeWorld(source),session.Kind,center) : new PlacementResult(false,center,"invalid");
        EffectEstimate estimate;
        if(!point.Valid) estimate=new(false,session.Kind,0,0,0,0,new(0,1,false));
        else if(session.Kind==GrenadeKind.Flash)
        {
            var visibility=PredictionSight(source,center,eye,DummyFlashSight);
            estimate=EffectPrediction.Flash(center,eye,view,new(visibility.Clear ? 1 : 0,1,visibility.Valid));
        }
        else
        {
            int visible=0;bool allValid=true;
            float height=Math.Clamp(target.ViewOffset.Z,28,72);
            // Three exposure samples per dummy, no per-pixel/radial light grid.
            foreach(float z in new[]{height*.3f,height*.6f,height})
            {
                var ray=PredictionSight(source,center,feet+Vector3.UnitZ*z,DummyBlastSight);
                if(ray.Clear) visible++;allValid&=ray.Valid;
            }
            estimate=EffectPrediction.HighExplosive(center,feet+Vector3.UnitZ*(height*.55f),Math.Max(0,target.ArmorValue),Math.Max(0,target.Health),new(visible,3,allValid));
        }
        cache.Samples[dummy.Handle]=new(target.EntityHandle.Raw,session.Kind,center,eye,view,target.Health,target.ArmorValue,now,estimate);
        return compact ? DummyRoster.Prediction(estimate) : estimate.Text;
    }

    private (bool Valid,bool Clear) PredictionSight(CCSPlayerPawn source,Vector3 center,Vector3 target,TraceOptions options)
    {
        var ray=EffectRay(source,center,target,options);
        bool valid=float.IsFinite(ray.Fraction) && ray.Fraction is >=0 and <=1 && !ray.IsAllSolid;
        return (valid,valid && EffectDrawRules.RayClear(ray.IsAllSolid,ray.DidHit(),ray.Fraction));
    }
}
