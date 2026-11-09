namespace NadeLab.Core;

/// <summary>Result storage is independent of the amount of physics reviewed in one batch.</summary>
public static class SearchResultCapacity
{
    public const int Default=24;
    public const int Maximum=32;
    public static int ReviewBatch(int resultCount,int batchLimit=5)=>Math.Min(resultCount,batchLimit);
}
