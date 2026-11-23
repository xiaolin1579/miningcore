namespace Miningcore.Persistence.Model.Projections;

/// <summary>One round trip covering what used to be four separate block-count/reward/time queries
/// per pool (GetPoolBlockCountAsync, GetTotalConfirmedBlocksAsync, GetTotalPendingBlocksAsync,
/// GetLastConfirmedBlockRewardAsync, GetLastPoolBlockTimeAsync), used by /api/pools and /api/pools/{id}.</summary>
public class PoolBlockSummary
{
    public uint TotalBlocks { get; set; }
    public uint TotalConfirmedBlocks { get; set; }
    public uint TotalPendingBlocks { get; set; }
    public DateTime? LastBlockTime { get; set; }
    public decimal? LastConfirmedReward { get; set; }
}
