using System.Data;
using Miningcore.Mapping;
using Dapper;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;

namespace Miningcore.Persistence.Postgres.Repositories;

public class BlockRepository : IBlockRepository
{
    public BlockRepository(MiningcoreMapper mapper)
    {
        this.mapper = mapper;
    }

    private readonly MiningcoreMapper mapper;

    public async Task InsertAsync(IDbConnection con, IDbTransaction tx, Block block)
    {
        var mapped = mapper.ToEntity(block);

        const string query =
            @"INSERT INTO blocks(poolid, blockheight, networkdifficulty, difficulty, status, type, transactionconfirmationdata,
                miner, worker, reward, effort, minereffort, confirmationprogress, source, hash, created)
            VALUES(@poolid, @blockheight, @networkdifficulty, @difficulty, @status, @type, @transactionconfirmationdata,
                @miner, @worker, @reward, @effort, @minereffort, @confirmationprogress, @source, @hash, @created)";

        await con.ExecuteAsync(query, mapped, tx);
    }

    public async Task DeleteBlockAsync(IDbConnection con, IDbTransaction tx, Block block)
    {
        const string query = "DELETE FROM blocks WHERE id = @id";
        await con.ExecuteAsync(query, block, tx);
    }

    public async Task UpdateBlockAsync(IDbConnection con, IDbTransaction tx, Block block)
    {
        var mapped = mapper.ToEntity(block);

        const string query = @"UPDATE blocks SET blockheight = @blockheight, status = @status, type = @type,
            reward = @reward, effort = @effort, minereffort = @minereffort, confirmationprogress = @confirmationprogress, hash = @hash WHERE id = @id";

        await con.ExecuteAsync(query, mapped, tx);
    }

    public async Task<Block[]> PageBlocksAsync(IDbConnection con, string poolId, BlockStatus[] status,
        int page, int pageSize, CancellationToken ct)
    {
        const string query = @"SELECT * FROM blocks WHERE poolid = @poolid AND status = ANY(@status)
            ORDER BY created DESC OFFSET @offset FETCH NEXT @pageSize ROWS ONLY";

        return (await con.QueryAsync<Entities.Block>(new CommandDefinition(query, new
        {
            poolId,
            status = status.Select(x => x.ToString().ToLower()).ToArray(),
            offset = page * pageSize,
            pageSize
        }, cancellationToken: ct)))
            .Select(mapper.ToModel)
            .ToArray();
    }

    public async Task<Block[]> PageBlocksAsync(IDbConnection con, BlockStatus[] status, int page, int pageSize, CancellationToken ct)
    {
        const string query = @"SELECT * FROM blocks WHERE status = ANY(@status)
            ORDER BY created DESC OFFSET @offset FETCH NEXT @pageSize ROWS ONLY";

        return (await con.QueryAsync<Entities.Block>(new CommandDefinition(query, new
        {
            status = status.Select(x => x.ToString().ToLower()).ToArray(),
            offset = page * pageSize,
            pageSize
        }, cancellationToken: ct)))
            .Select(mapper.ToModel)
            .ToArray();
    }

    public async Task<Block[]> PageMinerBlocksAsync(IDbConnection con, string poolId, string address, BlockStatus[] status,
        int page, int pageSize, CancellationToken ct)
    {
        const string query = @"SELECT * FROM blocks WHERE poolid = @poolid AND status = ANY(@status) AND miner = @address
            ORDER BY created DESC OFFSET @offset FETCH NEXT @pageSize ROWS ONLY";

        return (await con.QueryAsync<Entities.Block>(new CommandDefinition(query, new
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

    public async Task<Block[]> GetPendingBlocksForPoolAsync(IDbConnection con, string poolId)
    {
        const string query = @"SELECT * FROM blocks WHERE poolid = @poolid AND status = @status";

        return (await con.QueryAsync<Entities.Block>(query, new { status = BlockStatus.Pending.ToString().ToLower(), poolid = poolId }))
            .Select(mapper.ToModel)
            .ToArray();
    }

    public async Task<Block> GetBlockBeforeAsync(IDbConnection con, string poolId, BlockStatus[] status, DateTime before)
    {
        const string query = @"SELECT * FROM blocks WHERE poolid = @poolid AND status = ANY(@status) AND created < @before
            ORDER BY created DESC FETCH NEXT 1 ROWS ONLY";

        return (await con.QueryAsync<Entities.Block>(query, new
        {
            poolId,
            status = status.Select(x => x.ToString().ToLower()).ToArray(),
            before
        }))
            .Select(mapper.ToModel)
            .FirstOrDefault();
    }

    public async Task<uint> GetBlockBeforeCountAsync(IDbConnection con, string poolId, BlockStatus[] status, DateTime before)
    {
        const string query = @"SELECT * FROM blocks WHERE poolid = @poolid AND status = ANY(@status) AND created < @before";

        return await con.ExecuteScalarAsync<uint>(new CommandDefinition(query, new
        {
            poolId,
            status = status.Select(x => x.ToString().ToLower()).ToArray(),
            before
        }));
    }

    public Task<uint> GetPoolBlockCountAsync(IDbConnection con, string poolId, CancellationToken ct)
    {
        const string query = @"SELECT COUNT(*) FROM blocks WHERE poolid = @poolId";

        return con.ExecuteScalarAsync<uint>(new CommandDefinition(query, new { poolId }, cancellationToken: ct));
    }

    public Task<uint> GetTotalConfirmedBlocksAsync(IDbConnection con, string poolId, CancellationToken ct)
    {
        const string query = @"SELECT COUNT(*) FROM blocks WHERE poolid = @poolId AND status = 'confirmed'";

        return con.ExecuteScalarAsync<uint>(new CommandDefinition(query, new { poolId }, cancellationToken: ct));
    }

    public Task<uint> GetTotalPendingBlocksAsync(IDbConnection con, string poolId, CancellationToken ct)
    {
        const string query = @"SELECT COUNT(*) FROM blocks WHERE poolid = @poolId AND status = 'pending'";

        return con.ExecuteScalarAsync<uint>(new CommandDefinition(query, new { poolId }, cancellationToken: ct));
    }

    public Task<decimal> GetLastConfirmedBlockRewardAsync(IDbConnection con, string poolId, CancellationToken ct)
    {
        const string query = @"SELECT reward FROM blocks WHERE poolid = @poolId AND status = 'confirmed' ORDER BY created DESC LIMIT 1";

        return con.ExecuteScalarAsync<decimal>(new CommandDefinition(query, new { poolId }, cancellationToken: ct));
    }

    public Task<uint> GetMinerBlockCountAsync(IDbConnection con, string poolId, string address, CancellationToken ct)
    {
        const string query = @"SELECT COUNT(*) FROM blocks WHERE poolid = @poolId AND miner = @address";

        return con.ExecuteScalarAsync<uint>(new CommandDefinition(query, new { poolId, address }, cancellationToken: ct));
    }

    public Task<DateTime?> GetLastPoolBlockTimeAsync(IDbConnection con, string poolId, CancellationToken ct)
    {
        const string query = @"SELECT created FROM blocks WHERE poolid = @poolId ORDER BY created DESC LIMIT 1";

        return con.ExecuteScalarAsync<DateTime?>(new CommandDefinition(query, new { poolId }, cancellationToken: ct));
    }

    public async Task<Model.Projections.PoolBlockSummary> GetPoolBlockSummaryAsync(IDbConnection con, string poolId, CancellationToken ct)
    {
        // Replaces four separate round trips (count, confirmed count, pending count, last
        // confirmed reward, last block time) with one: the whole-pool aggregate is a single row
        // (agg), so joining it to the last-confirmed-reward lookup via LATERAL doesn't multiply it.
        const string query = @"
            WITH agg AS (
                SELECT
                    COUNT(*) AS totalblocks,
                    COUNT(*) FILTER (WHERE status = 'confirmed') AS totalconfirmedblocks,
                    COUNT(*) FILTER (WHERE status = 'pending') AS totalpendingblocks,
                    MAX(created) AS lastblocktime
                FROM blocks WHERE poolid = @poolId
            )
            SELECT agg.*, r.reward AS lastconfirmedreward
            FROM agg
            LEFT JOIN LATERAL (
                SELECT reward FROM blocks WHERE poolid = @poolId AND status = 'confirmed' ORDER BY created DESC LIMIT 1
            ) r ON true";

        return await con.QuerySingleAsync<Model.Projections.PoolBlockSummary>(new CommandDefinition(query, new { poolId }, cancellationToken: ct));
    }

    public Task<DateTime?> GetLastMinerBlockTimeAsync(IDbConnection con, string poolId, string address, CancellationToken ct)
    {
        const string query = @"SELECT created FROM blocks WHERE poolid = @poolId AND miner = @address ORDER BY created DESC LIMIT 1";
        return con.ExecuteScalarAsync<DateTime?>(new CommandDefinition(query, new { poolId, address }, cancellationToken: ct));
    }

    public Task<int> GetPoolBlockCountSinceAsync(IDbConnection con, string poolId, DateTime since, CancellationToken ct)
    {
        const string query = @"SELECT COUNT(*) FROM blocks WHERE poolid = @poolId AND created >= @since";
        return con.ExecuteScalarAsync<int>(new CommandDefinition(query, new { poolId, since }, cancellationToken: ct));
    }

    public async Task<Block> GetBlockByPoolHeightAndTypeAsync(IDbConnection con, string poolId, long height, string type)
    {
        const string query = @"SELECT * FROM blocks WHERE poolid = @poolId AND blockheight = @height AND type = @type";

        return (await con.QueryAsync<Entities.Block>(query, new
        {
            poolId,
            height,
            type
        }))
            .Select(mapper.ToModel)
            .FirstOrDefault();
    }

    public async Task<uint> GetPoolDuplicateBlockCountByPoolHeightNoTypeAndStatusAsync(IDbConnection con, string poolId, long height, BlockStatus[] status)
    {
        const string query = @"SELECT COUNT(id) FROM blocks WHERE poolid = @poolId AND blockheight = @height AND status = ANY(@status)";

        return await con.ExecuteScalarAsync<uint>(new CommandDefinition(query, new
        {
            poolId,
            height,
            status = status.Select(x => x.ToString().ToLower()).ToArray()
        }));
    }

    public async Task<uint> GetPoolDuplicateBlockBeforeCountByPoolHeightNoTypeAndStatusAsync(IDbConnection con, string poolId, long height, BlockStatus[] status, DateTime before)
    {
        const string query = @"SELECT COUNT(id) FROM blocks WHERE poolid = @poolId AND blockheight = @height AND status = ANY(@status) AND created < @before";

        return await con.ExecuteScalarAsync<uint>(new CommandDefinition(query, new
        {
            poolId,
            height,
            status = status.Select(x => x.ToString().ToLower()).ToArray(),
            before
        }));
    }

    public async Task<uint> GetPoolDuplicateBlockAfterCountByPoolHeightNoTypeAndStatusAsync(IDbConnection con, string poolId, long height, BlockStatus[] status, DateTime after)
    {
        const string query = @"SELECT COUNT(id) FROM blocks WHERE poolid = @poolId AND blockheight = @height AND status = ANY(@status) AND created > @after";

        return await con.ExecuteScalarAsync<uint>(new CommandDefinition(query, new
        {
            poolId,
            height,
            status = status.Select(x => x.ToString().ToLower()).ToArray(),
            after
        }));
    }

    public async Task<uint> GetPoolDuplicateBlockBeforeCountByPoolHeightAndHashNoTypeAndStatusAsync(IDbConnection con, string poolId, long height, string hash, BlockStatus[] status, DateTime before)
    {
        const string query = @"SELECT COUNT(id) FROM blocks WHERE poolid = @poolId AND blockheight = @height AND hash = @hash AND status = ANY(@status) AND created < @before";

        return await con.ExecuteScalarAsync<uint>(new CommandDefinition(query, new
        {
            poolId,
            height,
            hash,
            status = status.Select(x => x.ToString().ToLower()).ToArray(),
            before
        }));
    }

    public async Task<uint> GetPoolDuplicateBlockAfterCountByPoolHeightAndHashNoTypeAndStatusAsync(IDbConnection con, string poolId, long height, string hash, BlockStatus[] status, DateTime after)
    {
        const string query = @"SELECT COUNT(id) FROM blocks WHERE poolid = @poolId AND blockheight = @height AND hash = @hash AND status = ANY(@status) AND created > @after";

        return await con.ExecuteScalarAsync<uint>(new CommandDefinition(query, new
        {
            poolId,
            height,
            hash,
            status = status.Select(x => x.ToString().ToLower()).ToArray(),
            after
        }));
    }
}
