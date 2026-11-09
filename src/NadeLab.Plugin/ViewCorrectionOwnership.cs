namespace NadeLab.Plugin;

internal readonly record struct ViewCorrectionSnapshot(uint Index,int Type,float Pitch,float Yaw,float Roll);

internal static class ViewCorrectionOwnership
{
    internal static int Select(ViewCorrectionSnapshot[] before,ViewCorrectionSnapshot[] after,float yaw,bool confirmedBot)
    {
        if(confirmedBot && before.SequenceEqual(after)) return -1;
        // Teleport may coalesce all pending corrections into one new absolute
        // command. An unchanged count is not an unchanged command. Otherwise
        // require a pure append and preserve every earlier entry verbatim.
        bool appended=after.Length==before.Length+1 && before.SequenceEqual(after.Take(before.Length));
        if(after.Length!=1 && !appended)
            throw new InvalidOperationException("View alignment produced an ambiguous correction queue.");
        var owned=after[^1];
        if(owned.Type!=1 || before.Any(c=>c.Index==owned.Index) ||
            !(MathF.Abs(owned.Pitch)<=.01f && MathF.Abs(MathF.IEEERemainder(owned.Yaw-yaw,360))<=.01f && MathF.Abs(owned.Roll)<=.01f))
            throw new InvalidOperationException("View alignment could not identify this teleport's absolute correction.");
        return after.Length-1;
    }
}
