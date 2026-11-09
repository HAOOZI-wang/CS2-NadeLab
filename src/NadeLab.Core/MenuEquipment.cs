namespace NadeLab.Core;

// Keep exact entity handles: no weapon is removed/recreated, so ammunition,
// skins and item state survive the temporary menu restriction.
public static class MenuEquipment
{
    public sealed record Recovery(uint[] Handles,uint[] Overflow);
    public const uint Empty=uint.MaxValue;
    public static uint[] Mask(IEnumerable<uint> current,uint active)=>current.Select(h=>h==active ? h : Empty).ToArray();
    public static Recovery Restore(IReadOnlyList<uint> current,IEnumerable<uint> saved,Func<uint,bool> stillOwned)
    {
        var result=current.ToArray();
        var missing=saved.Where(h=>h!=Empty&&stillOwned(h)&&!result.Contains(h)).Distinct().ToArray();
        var holes=Enumerable.Range(0,result.Length).Where(i=>result[i]==Empty).ToArray();
        int count=Math.Min(holes.Length,missing.Length);
        for(int i=0;i<count;i++) result[holes[i]]=missing[i];
        return new(result,missing.Skip(count).ToArray());
    }
}
