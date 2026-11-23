namespace Miningcore.Persistence.Model.Projections;

/// <summary>One round trip covering what used to be two separate aux-block queries per pool
/// (GetPoolAuxBlockCountAsync, GetLastPoolAuxBlockTimeAsync), used by /api/pools and /api/pools/{id}.</summary>
public class PoolAuxBlockSummary
{
    public int TotalAuxBlocks { get; set; }
    public DateTime? LastAuxBlockTime { get; set; }
}
