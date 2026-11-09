using System.Diagnostics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;

namespace NadeLab.Plugin;

public sealed partial class NadeLabPlugin
{
    private readonly PendingPlayerActions queuedPlayers=new();
    private void QueueAction(CCSPlayerController? player,Action<CCSPlayerController?> action)
    {
        if(player is null) {action(null); return;}
        if(!player.IsValid || !queuedPlayers.TryEnqueue(player.Slot,out var token)) return;
        int slot=player.Slot;
        var identity=ActionIdentity(player);
        // Native work and first-use JIT run after the network command returns.
        Server.NextFrame(()=>
        {
            // A stale callback after Reset must not consume a new world's slot.
            if(!queuedPlayers.Take(slot,token)) return;
            var current=Utilities.GetPlayerFromSlot(slot);
            if(!loaded || current?.IsValid!=true || !identity.Matches(ActionIdentity(current))) return;
            var watch=Stopwatch.StartNew();
            try {RestoreMenuEquipment(slot);action(current);SyncMenuEquipment(current);}
            catch(Exception ex) {Logger.LogError(ex,"NadeLab queued action failed"); Say(current,"操作失败，详情已记录；可重新打开 !nl。");}
            finally
            {
                double elapsed=watch.Elapsed.TotalMilliseconds;
                AddTimingSample(actionWork,elapsed);
                peakActionMs=Math.Max(peakActionMs,elapsed);
                if(elapsed>25) Logger.LogWarning("NadeLab action {Action} took {Ms:F1}ms outside network command processing",action.Method.Name,elapsed);
            }
        });
    }
    private void AddPlayerCommand(string name,string description,Action<CCSPlayerController?,string[]> action)
    {
        AddCommand(name,description,(player,command)=>
        {
            var args=Enumerable.Range(1,Math.Max(0,command.ArgCount-1)).Select(command.ArgByIndex).ToArray();
            QueueAction(player,current=>action(current,args));
        });
    }
}
