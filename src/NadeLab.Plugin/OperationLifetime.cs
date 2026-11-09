namespace NadeLab.Plugin;

// Managed identity checks shared by the game-thread adapters and lightweight tests.
internal sealed class PreparationLifetime
{
    private bool initialized,alive;
    private int epoch;
    private uint? pawn;

    public bool Observe(int currentEpoch,uint? currentPawn,bool currentAlive)
    {
        bool changed=initialized && (epoch!=currentEpoch || pawn!=currentPawn || alive!=currentAlive);
        initialized=true;epoch=currentEpoch;pawn=currentPawn;alive=currentAlive;
        return changed;
    }

    public static bool Expired(float? expires,float now)=>expires is { } end && now>=end;
}

internal readonly record struct PlayerActionIdentity(int Epoch,uint Controller,uint? Pawn,ulong Steam,object? Session,object? Placement)
{
    public bool Matches(PlayerActionIdentity current)=>Epoch==current.Epoch && Controller==current.Controller &&
        Pawn==current.Pawn && Steam==current.Steam && (Session is null || ReferenceEquals(Session,current.Session)) &&
        ReferenceEquals(Placement,current.Placement);
}

internal sealed class PendingPlayerActions
{
    private readonly Dictionary<int,object> pending=[];
    public bool HasPending(int slot)=>pending.ContainsKey(slot);
    public bool TryEnqueue(int slot,out object token)
    {
        token=new object();
        return pending.TryAdd(slot,token);
    }
    public bool Take(int slot,object token)
    {
        if(!pending.TryGetValue(slot,out var current) || !ReferenceEquals(current,token)) return false;
        pending.Remove(slot);return true;
    }
    public void Clear()=>pending.Clear();
}
