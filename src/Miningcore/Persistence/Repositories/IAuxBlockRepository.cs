using System.Data;
using Miningcore.Persistence.Model;

namespace Miningcore.Persistence.Repositories;

public interface IAuxBlockRepository
{
    Task<bool> InsertAsync(IDbConnection con, IDbTransaction tx, AuxBlock block);
    Task<AuxBlock[]> PageAuxBlocksAsync(IDbConnection con, BlockStatus[] status, int page, int pageSize, CancellationToken ct);
    Task<AuxBlock[]> PagePoolAuxBlocksAsync(IDbConnection con, string poolId, BlockStatus[] status, int page, int pageSize, CancellationToken ct);
    Task<AuxBlock[]> PageMinerAuxBlocksAsync(IDbConnection con, string poolId, string address, BlockStatus[] status, int page, int pageSize, CancellationToken ct);
    Task<AuxBlock[]> GetPendingAsync(IDbConnection con, string poolId, string chainId, int limit, CancellationToken ct);
    Task<AuxBlock[]> GetBlocksWithoutHeightAsync(IDbConnection con, string poolId, string chainId, int limit, CancellationToken ct, bool pendingOnly = false);
    Task<AuxBlock[]> GetRecentlyConfirmedAsync(IDbConnection con, string poolId, string chainId, TimeSpan window, int limit, CancellationToken ct);
    Task UpdateAsync(IDbConnection con, IDbTransaction tx, AuxBlock block);
    Task<int> GetPoolAuxBlockCountAsync(IDbConnection con, string poolId, CancellationToken ct);
    Task<Model.Projections.AuxChainSummary[]> GetChainSummariesAsync(IDbConnection con, string poolId, DateTime since, CancellationToken ct);
    Task<int> GetPoolAuxBlockCountSinceAsync(IDbConnection con, string poolId, DateTime since, CancellationToken ct);
    Task<DateTime?> GetLastPoolAuxBlockTimeAsync(IDbConnection con, string poolId, CancellationToken ct);
    Task<Model.Projections.PoolAuxBlockSummary> GetPoolAuxBlockSummaryAsync(IDbConnection con, string poolId, CancellationToken ct);
}
