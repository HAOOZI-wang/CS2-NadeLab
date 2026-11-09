namespace NadeLab.Core;

public sealed partial class LineupSearch
{
    // Raising selectable capacity must not multiply exploration/review work.
    private int ReviewBatchCount=>SearchResultCapacity.ReviewBatch(options.ResultCount);
}
