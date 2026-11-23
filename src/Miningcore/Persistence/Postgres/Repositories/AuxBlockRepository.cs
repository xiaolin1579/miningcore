using System.Data;
using Miningcore.Mapping;
using Dapper;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;

namespace Miningcore.Persistence.Postgres.Repositories;

public class AuxBlockRepository : IAuxBlockRepository
{
    public AuxBlockRepository(MiningcoreMapper mapper)
    {
        this.mapper = mapper;
    }

    private readonly MiningcoreMapper mapper;

    public async Task<bool> InsertAsync(IDbConnection con, IDbTransaction tx, AuxBlock block)
    {
        var mapped = mapper.ToEntity(block);

        const string query = @"
            INSERT INTO auxblocks(poolid, chainid, chainname, blockheight, auxblockhash, parentblockhash,
                status, confirmationprogress, reward, miner, worker, source, submittedvia, difficulty, networkdifficulty, created)
            VALUES(@poolid, @chainid, @chainname, @blockheight, @auxblockhash, @parentblockhash,
                @status, @confirmationprogress, @reward, @miner, @worker, @source, @submittedvia, @difficulty, @networkdifficulty, @created)
            ON CONFLICT (poolid, chainid, auxblockhash) DO NOTHING";

        var rows = await con.ExecuteAsync(query, mapped, tx);
        return rows > 0;
    }

    public async Task<AuxBlock[]> PageAuxBlocksAsync(IDbConnection con, BlockStatus[] status, int page, int pageSize, CancellationToken ct)
    {
        const string query = @"SELECT * FROM auxblocks WHERE status = ANY(@status)
            ORDER BY created DESC OFFSET @offset FETCH NEXT @pageSize ROWS ONLY";

        return (await con.QueryAsync<Entities.AuxBlock>(new CommandDefinition(query, new
        {
            status = status.Select(x => x.ToString().ToLower()).ToArray(),
            offset = page * pageSize,
            pageSize
        }, cancellationToken: ct)))
            .Select(mapper.ToModel)
            .ToArray();
    }

    public async Task<AuxBlock[]> PagePoolAuxBlocksAsync(IDbConnection con, string poolId, BlockStatus[] status, int page, int pageSize, CancellationToken ct)
    {
        const string query = @"SELECT * FROM auxblocks WHERE poolid = @poolId AND status = ANY(@status)
            ORDER BY created DESC OFFSET @offset FETCH NEXT @pageSize ROWS ONLY";

        return (await con.QueryAsync<Entities.AuxBlock>(new CommandDefinition(query, new
        {
            poolId,
            status = status.Select(x => x.ToString().ToLower()).ToArray(),
            offset = page * pageSize,
            pageSize
        }, cancellationToken: ct)))
            .Select(mapper.ToModel)
            .ToArray();
    }

    public async Task<AuxBlock[]> PageMinerAuxBlocksAsync(IDbConnection con, string poolId, string address, BlockStatus[] status, int page, int pageSize, CancellationToken ct)
    {
        const string query = @"SELECT * FROM auxblocks WHERE poolid = @poolId AND miner = @address AND status = ANY(@status)
            ORDER BY created DESC OFFSET @offset FETCH NEXT @pageSize ROWS ONLY";

        return (await con.QueryAsync<Entities.AuxBlock>(new CommandDefinition(query, new
        {
            poolId,
            address,
            status = status.Select(x => x.ToString().ToLower()).ToArray(),
            offset = page * pageSize,
            pageSize
        }, cancellationToken: ct)))
            .Select(mapper.ToModel)
            .ToArray();
    }

    public async Task<AuxBlock[]> GetPendingAsync(IDbConnection con, string poolId, string chainId, int limit, CancellationToken ct)
    {
        const string query = @"SELECT * FROM auxblocks WHERE poolid = @poolId AND chainid = @chainId
            AND status = 'pending' ORDER BY created ASC LIMIT @limit";

        return (await con.QueryAsync<Entities.AuxBlock>(new CommandDefinition(query, new
        {
            poolId, chainId, limit
        }, cancellationToken: ct)))
            .Select(mapper.ToModel)
            .ToArray();
    }

    public async Task<AuxBlock[]> GetRecentlyConfirmedAsync(IDbConnection con, string poolId, string chainId, TimeSpan window, int limit, CancellationToken ct)
    {
        const string query = @"SELECT * FROM auxblocks WHERE poolid = @poolId AND chainid = @chainId
            AND status = 'confirmed' AND blockheight IS NOT NULL AND created >= @since
            ORDER BY created DESC LIMIT @limit";

        return (await con.QueryAsync<Entities.AuxBlock>(new CommandDefinition(query, new
        {
            poolId, chainId,
            since = DateTime.UtcNow - window,
            limit
        }, cancellationToken: ct)))
            .Select(mapper.ToModel)
            .ToArray();
    }

    public async Task<AuxBlock[]> GetBlocksWithoutHeightAsync(IDbConnection con, string poolId, string chainId, int limit, CancellationToken ct, bool pendingOnly = false)
    {
        // pendingOnly matters for rows that can stay height-less forever (an RSK block that never reached the
        // main chain is orphaned without a height): without it they fill the LIMIT window, are reprocessed and
        // rewritten every tick, and starve newer rows
        var query = pendingOnly
            ? @"SELECT * FROM auxblocks WHERE poolid = @poolId AND chainid = @chainId
                AND blockheight IS NULL AND status = 'pending' ORDER BY created ASC LIMIT @limit"
            : @"SELECT * FROM auxblocks WHERE poolid = @poolId AND chainid = @chainId
                AND blockheight IS NULL ORDER BY created ASC LIMIT @limit";

        return (await con.QueryAsync<Entities.AuxBlock>(new CommandDefinition(query, new
        {
            poolId, chainId, limit
        }, cancellationToken: ct)))
            .Select(mapper.ToModel)
            .ToArray();
    }

    public async Task UpdateAsync(IDbConnection con, IDbTransaction tx, AuxBlock block)
    {
        var mapped = mapper.ToEntity(block);

        // auxblockhash is included because RSK rows are inserted with the merged-mining hash and
        // upgraded to the real block hash once the confirmer has resolved them on-chain.
        const string query = @"UPDATE auxblocks SET status = @status, confirmationprogress = @confirmationprogress,
            blockheight = @blockheight, auxblockhash = @auxblockhash WHERE id = @id";

        await con.ExecuteAsync(query, mapped, tx);
    }

    public Task<int> GetPoolAuxBlockCountAsync(IDbConnection con, string poolId, CancellationToken ct)
    {
        const string query = @"SELECT COUNT(*) FROM auxblocks WHERE poolid = @poolId";
        return con.ExecuteScalarAsync<int>(new CommandDefinition(query, new { poolId }, cancellationToken: ct));
    }

    public async Task<Model.Projections.AuxChainSummary[]> GetChainSummariesAsync(IDbConnection con, string poolId, DateTime since, CancellationToken ct)
    {
        const string query = @"SELECT c.chainid, c.total, c.today, c.lastcreated, r.reward AS lastreward
            FROM (SELECT chainid, COUNT(*) AS total, COUNT(*) FILTER (WHERE created >= @since) AS today, MAX(created) AS lastcreated
                  FROM auxblocks WHERE poolid = @poolId GROUP BY chainid) c
            LEFT JOIN LATERAL (SELECT reward FROM auxblocks WHERE poolid = @poolId AND chainid = c.chainid AND reward IS NOT NULL AND status <> 'orphaned'
                  ORDER BY created DESC LIMIT 1) r ON true";

        return (await con.QueryAsync<Model.Projections.AuxChainSummary>(new CommandDefinition(query, new { poolId, since }, cancellationToken: ct))).ToArray();
    }

    public Task<int> GetPoolAuxBlockCountSinceAsync(IDbConnection con, string poolId, DateTime since, CancellationToken ct)
    {
        const string query = @"SELECT COUNT(*) FROM auxblocks WHERE poolid = @poolId AND created >= @since";
        return con.ExecuteScalarAsync<int>(new CommandDefinition(query, new { poolId, since }, cancellationToken: ct));
    }

    public Task<DateTime?> GetLastPoolAuxBlockTimeAsync(IDbConnection con, string poolId, CancellationToken ct)
    {
        const string query = @"SELECT MAX(created) FROM auxblocks WHERE poolid = @poolId";
        return con.ExecuteScalarAsync<DateTime?>(new CommandDefinition(query, new { poolId }, cancellationToken: ct));
    }

    public async Task<Model.Projections.PoolAuxBlockSummary> GetPoolAuxBlockSummaryAsync(IDbConnection con, string poolId, CancellationToken ct)
    {
        // Replaces GetPoolAuxBlockCountAsync + GetLastPoolAuxBlockTimeAsync with one round trip.
        const string query = @"SELECT COUNT(*) AS totalauxblocks, MAX(created) AS lastauxblocktime FROM auxblocks WHERE poolid = @poolId";

        return await con.QuerySingleAsync<Model.Projections.PoolAuxBlockSummary>(new CommandDefinition(query, new { poolId }, cancellationToken: ct));
    }
}
