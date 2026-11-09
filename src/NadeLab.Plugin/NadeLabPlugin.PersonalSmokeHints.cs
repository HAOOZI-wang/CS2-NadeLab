using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using NadeLab.Core;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    // Called while preparing the request on the game thread. Only the returned
    // immutable aim values enter the solver; no native player wrapper is queued.
    private Aim[] SearchSeedAims(CCSPlayerController? player,ThrowStart start,Target target,Aim? view,PhysicsProfile profile,SearchOptions options)
    {
        var prior=PriorAims(start,target,view,profile);
        if(profile.Kind!=GrenadeKind.Smoke || player is not {IsValid:true,IsBot:false} || player.SteamID==0) return prior;
        string map=Server.MapName;string? world=worldVersion?.ToJsonString();
        if(WorldIdentity.Key(world,map) is null) return prior;
        var entries=ReadPersonalLibrary(player);
        if(entries is null) return prior;
        return PersonalSmokeHints.Combine(prior,PersonalSmokeHints.Select(entries,profile,start,map,world,options));
    }
}
