using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using System.Net;
using Autofac;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Miningcore.Api.Extensions;
using Miningcore.Api.Responses;
using Miningcore.Blockchain;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Mining;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Model.Projections;
using Miningcore.Persistence.Repositories;
using Miningcore.Time;
using NLog;

namespace Miningcore.Api.Controllers;

[Route("api/pools")]
[ApiController]
public class PoolApiController : ApiControllerBase
{
    public PoolApiController(IComponentContext ctx, IActionDescriptorCollectionProvider _adcp) : base(ctx)
    {
        statsRepo = ctx.Resolve<IStatsRepository>();
        blocksRepo = ctx.Resolve<IBlockRepository>();
        auxBlocksRepo = ctx.Resolve<IAuxBlockRepository>();
        minerRepo = ctx.Resolve<IMinerRepository>();
        shareRepo = ctx.Resolve<IShareRepository>();
        paymentsRepo = ctx.Resolve<IPaymentRepository>();
        clock = ctx.Resolve<IMasterClock>();
        pools = ctx.Resolve<ConcurrentDictionary<string, IMiningPool>>();
        cache = ctx.Resolve<IMemoryCache>();
        adcp = _adcp;
    }

    private readonly IStatsRepository statsRepo;
    private readonly IBlockRepository blocksRepo;
    private readonly IAuxBlockRepository auxBlocksRepo;
    private readonly IPaymentRepository paymentsRepo;
    private readonly IMinerRepository minerRepo;
    private readonly IShareRepository shareRepo;
    private readonly IMasterClock clock;
    private readonly IActionDescriptorCollectionProvider adcp;
    private readonly ConcurrentDictionary<string, IMiningPool> pools;
    private readonly IMemoryCache cache;

    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();

    #region Actions

    [HttpGet]
    public async Task<GetPoolsResponse> Get(CancellationToken ct, [FromQuery] uint topMinersRange = 24)
    {
        var cacheKey = $"pools-summary-{topMinersRange}";
        if(cache.TryGetValue(cacheKey, out GetPoolsResponse cached))
            return cached;

        var response = new GetPoolsResponse
        {
            Pools = await Task.WhenAll(clusterConfig.Pools.Where(x => x.Enabled).Select(async config =>
            {
                // get pool
                pools.TryGetValue(config.Id, out var pool);

                var auxExtra = config.Extra?.SafeExtensionDataAs<Blockchain.Bitcoin.Configuration.BitcoinPoolConfigExtra>();
                var hasAux = auxExtra?.AuxChains is { Length: > 0 };

                var from = clock.Now.AddHours(-topMinersRange);

                // none of these depend on each other: fire them all off together instead of
                // chaining ~9 awaits end-to-end (that chain, times every enabled pool running
                // concurrently, was what made this endpoint take ~5s to answer). The block and
                // aux-block counts/times/reward are each now one merged query instead of 5 + 2.
                var statsTask = cf.Run(con => statsRepo.GetLastPoolStatsAsync(con, config.Id, ct));
                var totalPaidTask = cf.Run(con => statsRepo.GetTotalPoolPaymentsAsync(con, config.Id, ct));
                var blockSummaryTask = cf.Run(con => blocksRepo.GetPoolBlockSummaryAsync(con, config.Id, ct));
                var auxSummaryTask = hasAux ? cf.Run(con => auxBlocksRepo.GetPoolAuxBlockSummaryAsync(con, config.Id, ct)) : null;
                var minersTask = cf.Run(con => statsRepo.PagePoolMinersByHashrateAsync(con, config.Id, from, 0, 15, ct));

                if(auxSummaryTask != null)
                    await Task.WhenAll(statsTask, totalPaidTask, blockSummaryTask, auxSummaryTask, minersTask);
                else
                    await Task.WhenAll(statsTask, totalPaidTask, blockSummaryTask, minersTask);

                // map
                var result = config.ToPoolInfo(mapper, statsTask.Result, pool);

                // enrich
                result.TotalPaid = totalPaidTask.Result;
                var blockSummary = blockSummaryTask.Result;
                result.TotalBlocks = blockSummary.TotalBlocks;
                result.TotalConfirmedBlocks = blockSummary.TotalConfirmedBlocks;
                result.TotalPendingBlocks = blockSummary.TotalPendingBlocks;
                // get reward of the last confirmed block and set BlockReward; fall back to current job's coinbase value
                result.BlockReward = blockSummary.LastConfirmedReward ?? 0;
                if(result.BlockReward == 0 && pool != null)
                    result.BlockReward = pool.NetworkStats?.BlockReward ?? 0;
                var lastBlockTime = blockSummary.LastBlockTime;
                result.LastPoolBlockTime = lastBlockTime;

                if(auxSummaryTask != null)
                {
                    result.TotalAuxBlocks = auxSummaryTask.Result.TotalAuxBlocks;
                    result.LastAuxBlockTime = auxSummaryTask.Result.LastAuxBlockTime;
                }

                var payoutConfig = config.PaymentProcessing;
                // display block finder percentage only if PPLNSBF is activated
                if(payoutConfig?.PayoutScheme != PayoutScheme.PPLNSBF && result.PaymentProcessing?.PayoutSchemeConfig != null)
                    result.PaymentProcessing.PayoutSchemeConfig.BlockFinderPercentage = null;

                // depends on lastBlockTime, so it can't join the batch above
                if(lastBlockTime.HasValue && pool != null)
                {
                    var startTime = lastBlockTime.Value;
                    var poolEffort = await cf.Run(con => shareRepo.GetEffortBetweenCreatedAsync(con, config.Id, pool.EffortShareConstant, startTime, clock.Now, ct));
                    if(poolEffort.HasValue)
                        result.PoolEffort = poolEffort.Value;
                }

                result.TopMiners = minersTask.Result.Select(mapper.ToResponse).ToArray();

                return result;
            }).ToArray())
        };

        cache.Set(cacheKey, response, TimeSpan.FromSeconds(15));
        return response;
    }

    [HttpGet("/api/help")]
    public ActionResult GetHelp()
    {
        var tmp = adcp.ActionDescriptors.Items
            .Where(x => x.AttributeRouteInfo != null)
            .Select(x =>
            {
                // Get and pad http method
                var method = x.ActionConstraints?.OfType<HttpMethodActionConstraint>().FirstOrDefault()?.HttpMethods.First();
                method = $"{method,-5}";

                return $"{method} -> {x.AttributeRouteInfo.Template}";
            });

        // convert curly braces
        var result = string.Join("\n", tmp).Replace("{", "<").Replace("}", ">") + "\n";

        return Content(result);
    }

    [HttpGet("/api/health-check")]
    public ActionResult GetHealthCheck()
    {
        return Content("👍");
    }

    [HttpGet("{poolId}")]
    public async Task<GetPoolResponse> GetPoolInfoAsync(string poolId, CancellationToken ct, [FromQuery] uint topMinersRange = 24)
    {
        var pool = GetPool(poolId);

        // get pool
        pools.TryGetValue(pool.Id, out var poolInstance);

        var auxExtra = pool.Extra?.SafeExtensionDataAs<Blockchain.Bitcoin.Configuration.BitcoinPoolConfigExtra>();
        var hasAux = auxExtra?.AuxChains is { Length: > 0 };

        var from = clock.Now.AddHours(-topMinersRange);

        // none of these depend on each other: fire them all off together instead of
        // chaining ~9 awaits end-to-end. The block and aux-block counts/times/reward are each
        // now one merged query instead of 5 + 2.
        var statsTask = cf.Run(con => statsRepo.GetLastPoolStatsAsync(con, pool.Id, ct));
        var totalPaidTask = cf.Run(con => statsRepo.GetTotalPoolPaymentsAsync(con, pool.Id, ct));
        var blockSummaryTask = cf.Run(con => blocksRepo.GetPoolBlockSummaryAsync(con, pool.Id, ct));
        var auxSummaryTask = hasAux ? cf.Run(con => auxBlocksRepo.GetPoolAuxBlockSummaryAsync(con, pool.Id, ct)) : null;
        var minersTask = cf.Run(con => statsRepo.PagePoolMinersByHashrateAsync(con, pool.Id, from, 0, 15, ct));

        if(auxSummaryTask != null)
            await Task.WhenAll(statsTask, totalPaidTask, blockSummaryTask, auxSummaryTask, minersTask);
        else
            await Task.WhenAll(statsTask, totalPaidTask, blockSummaryTask, minersTask);

        var response = new GetPoolResponse
        {
            Pool = pool.ToPoolInfo(mapper, statsTask.Result, poolInstance)
        };

        // enrich
        response.Pool.TotalPaid = totalPaidTask.Result;
        var blockSummary = blockSummaryTask.Result;
        response.Pool.TotalBlocks = blockSummary.TotalBlocks;
        response.Pool.TotalConfirmedBlocks = blockSummary.TotalConfirmedBlocks;
        response.Pool.TotalPendingBlocks = blockSummary.TotalPendingBlocks;
        // get reward of the last confirmed block and set BlockReward; fall back to current job's coinbase value
        response.Pool.BlockReward = blockSummary.LastConfirmedReward ?? 0;
        if(response.Pool.BlockReward == 0 && poolInstance != null)
            response.Pool.BlockReward = poolInstance.NetworkStats?.BlockReward ?? 0;
        var lastBlockTime = blockSummary.LastBlockTime;
        response.Pool.LastPoolBlockTime = lastBlockTime;

        if(auxSummaryTask != null)
        {
            response.Pool.TotalAuxBlocks = auxSummaryTask.Result.TotalAuxBlocks;
            response.Pool.LastAuxBlockTime = auxSummaryTask.Result.LastAuxBlockTime;
        }

        var payoutConfig = pool.PaymentProcessing;
        // display block finder percentage only if PPLNSBF is activated
        if(payoutConfig?.PayoutScheme != PayoutScheme.PPLNSBF && response.Pool.PaymentProcessing?.PayoutSchemeConfig != null)
            response.Pool.PaymentProcessing.PayoutSchemeConfig.BlockFinderPercentage = null;

        // depends on lastBlockTime, so it can't join the batch above
        if(lastBlockTime.HasValue && poolInstance != null)
        {
            var startTime = lastBlockTime.Value;
            var poolEffort = await cf.Run(con => shareRepo.GetEffortBetweenCreatedAsync(con, pool.Id, poolInstance.EffortShareConstant, startTime, clock.Now, ct));
            if(poolEffort.HasValue)
                response.Pool.PoolEffort = poolEffort.Value;
        }

        response.Pool.TopMiners = minersTask.Result
            .Select(mapper.ToResponse)
            .ToArray();

        return response;
    }

    [HttpGet("{poolId}/performance")]
    public async Task<GetPoolStatsResponse> GetPoolPerformanceAsync(string poolId,
        [FromQuery(Name = "r")] SampleRange range = SampleRange.Day,
        [FromQuery(Name = "i")] SampleInterval interval = SampleInterval.Hour)
    {
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        // set range
        var end = clock.Now;
        DateTime start;

        switch(range)
        {
            case SampleRange.Day:
                start = end.AddDays(-1);
                break;

            case SampleRange.Month:
                start = end.AddDays(-30);
                break;

            default:
                throw new ApiException("invalid interval");
        }

        var stats = await cf.Run(con => statsRepo.GetPoolPerformanceBetweenAsync(con, pool.Id, interval, start, end, ct));

        var response = new GetPoolStatsResponse
        {
            Stats = stats.Select(mapper.ToResponse).ToArray()
        };

        return response;
    }

    [HttpGet("{poolId}/miners")]
    public async Task<MinerPerformanceStats[]> PagePoolMinersAsync(
        string poolId, [FromQuery] int page, [FromQuery] int pageSize = 15, [FromQuery] uint topMinersRange = 24)
    {
        pageSize = ClampPageSize(pageSize);
        page = ClampPage(page);
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        // set range
        var end = clock.Now;
        var start = end.AddHours(-topMinersRange);

        var miners = (await cf.Run(con => statsRepo.PagePoolMinersByHashrateAsync(con, pool.Id, start, page, pageSize, ct)))
            .Select(mapper.ToResponse)
            .ToArray();

        return miners;
    }

    [HttpGet("{poolId}/blocks")]
    public async Task<Responses.Block[]> PagePoolBlocksAsync(
        string poolId, [FromQuery] int page, [FromQuery] int pageSize = 15, [FromQuery] BlockStatus[] state = null)
    {
        pageSize = ClampPageSize(pageSize);
        page = ClampPage(page);
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        var blockStates = state is { Length: > 0 } ?
            state :
            new[] { BlockStatus.Confirmed, BlockStatus.Pending, BlockStatus.Orphaned };

        var blocks = (await cf.Run(con => blocksRepo.PageBlocksAsync(con, pool.Id, blockStates, page, pageSize, ct)))
            .Select(mapper.ToResponse)
            .ToArray();

        // enrich blocks
        var blockInfobaseDict = pool.Template?.ExplorerBlockLinks;

        foreach(var block in blocks)
        {
            // compute infoLink
            if(blockInfobaseDict != null)
            {
                blockInfobaseDict.TryGetValue(!string.IsNullOrEmpty(block.Type) ? block.Type : "block", out var blockInfobaseUrl);

                if(!string.IsNullOrEmpty(blockInfobaseUrl))
                {
                    if(blockInfobaseUrl.Contains(CoinMetaData.BlockHeightPH))
                        block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHeightPH, block.BlockHeight.ToString(CultureInfo.InvariantCulture));
                    else if(blockInfobaseUrl.Contains(CoinMetaData.BlockHashPH) && !string.IsNullOrEmpty(block.Hash))
                        block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHashPH, block.Hash);
                }
            }
        }

        return blocks;
    }

    [HttpGet("/api/v2/pools/{poolId}/blocks")]
    public async Task<PagedResultResponse<Responses.Block[]>> PagePoolBlocksV2Async(
        string poolId, [FromQuery] int page, [FromQuery] int pageSize = 15, [FromQuery] BlockStatus[] state = null)
    {
        pageSize = ClampPageSize(pageSize);
        page = ClampPage(page);
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        var blockStates = state is { Length: > 0 } ?
            state :
            new[] { BlockStatus.Confirmed, BlockStatus.Pending, BlockStatus.Orphaned };

        uint itemCount = await cf.Run(con => blocksRepo.GetPoolBlockCountAsync(con, poolId, ct));
        uint pageCount = (uint) Math.Floor(itemCount / (double) pageSize);

        var blocks = (await cf.Run(con => blocksRepo.PageBlocksAsync(con, pool.Id, blockStates, page, pageSize, ct)))
            .Select(mapper.ToResponse)
            .ToArray();

        // enrich blocks
        var blockInfobaseDict = pool.Template?.ExplorerBlockLinks;

        foreach(var block in blocks)
        {
            // compute infoLink
            if(blockInfobaseDict != null)
            {
                blockInfobaseDict.TryGetValue(!string.IsNullOrEmpty(block.Type) ? block.Type : "block", out var blockInfobaseUrl);

                if(!string.IsNullOrEmpty(blockInfobaseUrl))
                {
                    if(blockInfobaseUrl.Contains(CoinMetaData.BlockHeightPH))
                        block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHeightPH, block.BlockHeight.ToString(CultureInfo.InvariantCulture));
                    else if(blockInfobaseUrl.Contains(CoinMetaData.BlockHashPH) && !string.IsNullOrEmpty(block.Hash))
                        block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHashPH, block.Hash);
                }
            }
        }

        var response = new PagedResultResponse<Responses.Block[]>(blocks, itemCount, pageCount);
        return response;
    }

    [HttpGet("{poolId}/payments")]
    public async Task<Responses.Payment[]> PagePoolPaymentsAsync(
        string poolId, [FromQuery] int page, [FromQuery] int pageSize = 15)
    {
        pageSize = ClampPageSize(pageSize);
        page = ClampPage(page);
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        var payments = (await cf.Run(con => paymentsRepo.PagePaymentsAsync(
                con, pool.Id, null, page, pageSize, ct)))
            .Select(mapper.ToResponse)
            .ToArray();

        // enrich payments
        var txInfobaseUrl = pool.Template.ExplorerTxLink;
        var addressInfobaseUrl = pool.Template.ExplorerAccountLink;

        foreach(var payment in payments)
        {
            // compute transaction infoLink
            if(!string.IsNullOrEmpty(txInfobaseUrl))
                payment.TransactionInfoLink = string.Format(txInfobaseUrl, payment.TransactionConfirmationData);

            // pool wallet link
            if(!string.IsNullOrEmpty(addressInfobaseUrl))
                payment.AddressInfoLink = string.Format(addressInfobaseUrl, payment.Address);
        }

        return payments;
    }

    [HttpGet("/api/v2/pools/{poolId}/payments")]
    public async Task<PagedResultResponse<Responses.Payment[]>> PagePoolPaymentsV2Async(
        string poolId, [FromQuery] int page, [FromQuery] int pageSize = 15)
    {
        pageSize = ClampPageSize(pageSize);
        page = ClampPage(page);
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        uint itemCount = await cf.Run(con => paymentsRepo.GetPaymentsCountAsync(con, poolId, null, ct));
        uint pageCount = (uint) Math.Floor(itemCount / (double) pageSize);

        var payments = (await cf.Run(con => paymentsRepo.PagePaymentsAsync(
                con, pool.Id, null, page, pageSize, ct)))
            .Select(mapper.ToResponse)
            .ToArray();

        // enrich payments
        var txInfobaseUrl = pool.Template.ExplorerTxLink;
        var addressInfobaseUrl = pool.Template.ExplorerAccountLink;

        foreach(var payment in payments)
        {
            // compute transaction infoLink
            if(!string.IsNullOrEmpty(txInfobaseUrl))
                payment.TransactionInfoLink = string.Format(txInfobaseUrl, payment.TransactionConfirmationData);

            // pool wallet link
            if(!string.IsNullOrEmpty(addressInfobaseUrl))
                payment.AddressInfoLink = string.Format(addressInfobaseUrl, payment.Address);
        }

        var response = new PagedResultResponse<Responses.Payment[]>(payments, itemCount, pageCount);
        return response;
    }

    [HttpGet("{poolId}/miners/{address}")]
    public async Task<Responses.MinerStats> GetMinerInfoAsync(
        string poolId, string address, [FromQuery] SampleRange perfMode = SampleRange.Day)
    {
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        if(string.IsNullOrEmpty(address))
            throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

        if(pool.Template.Family == CoinFamily.Ethereum)
            address = address.ToLower();

        var statsResult = await cf.RunTx((con, tx) =>
            statsRepo.GetMinerStatsAsync(con, tx, pool.Id, address, ct), true, IsolationLevel.Serializable);

        Responses.MinerStats stats = null;

        if(statsResult != null)
        {
            stats = mapper.ToResponse(statsResult);

            // pre-multiply pending shares to cause less confusion with users
            if(pool.Template.Family == CoinFamily.Bitcoin)
                stats.PendingShares *= pool.Template.As<BitcoinTemplate>().ShareMultiplier;

            // optional fields
            if(statsResult.LastPayment != null)
            {
                // Set timestamp of last payment
                stats.LastPayment = statsResult.LastPayment.Created;

                // Compute info link
                var baseUrl = pool.Template.ExplorerTxLink;
                if(!string.IsNullOrEmpty(baseUrl))
                    stats.LastPaymentLink = string.Format(baseUrl, statsResult.LastPayment.TransactionConfirmationData);
            }

            var lastBlockTime = await cf.Run(con => blocksRepo.GetLastPoolBlockTimeAsync(con, pool.Id, ct));
            if(lastBlockTime.HasValue)
            {
                var startTime = lastBlockTime.Value;
                var minerEffort = await cf.Run(con => shareRepo.GetMinerEffortBetweenCreatedAsync(con, pool.Id, address, startTime, clock.Now, ct));
                if(minerEffort.HasValue)
                    stats.MinerEffort = minerEffort.Value;
            }

            stats.PerformanceSamples = await GetMinerPerformanceInternal(perfMode, pool, address, ct);

            // add total confirmed and pending blocks
            var totalConfirmedBlocks = await cf.Run(con => statsRepo.GetMinerTotalConfirmedBlocksAsync(con, pool.Id, address, ct));
            var totalPendingBlocks = await cf.Run(con => statsRepo.GetMinerTotalPendingBlocksAsync(con, pool.Id, address, ct));
            stats.TotalConfirmedBlocks = totalConfirmedBlocks;
            stats.TotalPendingBlocks = totalPendingBlocks;
        }

        return stats;
    }

    [HttpGet("{poolId}/miners/{address}/blocks")]
    public async Task<Responses.Block[]> PageMinerBlocksAsync(
        string poolId, string address, [FromQuery] int page, [FromQuery] int pageSize = 15, [FromQuery] BlockStatus[] state = null)
    {
        pageSize = ClampPageSize(pageSize);
        page = ClampPage(page);
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        if(string.IsNullOrEmpty(address))
            throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

        if(pool.Template.Family == CoinFamily.Ethereum)
            address = address.ToLower();

        var blockStates = state is { Length: > 0 } ?
            state :
            new[] { BlockStatus.Confirmed, BlockStatus.Pending, BlockStatus.Orphaned };

        var blocks = (await cf.Run(con => blocksRepo.PageMinerBlocksAsync(con, pool.Id, address, blockStates, page, pageSize, ct)))
            .Select(mapper.ToResponse)
            .ToArray();

        // enrich blocks
        var blockInfobaseDict = pool.Template?.ExplorerBlockLinks;

        foreach(var block in blocks)
        {
            // compute infoLink
            if(blockInfobaseDict != null)
            {
                blockInfobaseDict.TryGetValue(!string.IsNullOrEmpty(block.Type) ? block.Type : "block", out var blockInfobaseUrl);

                if(!string.IsNullOrEmpty(blockInfobaseUrl))
                {
                    if(blockInfobaseUrl.Contains(CoinMetaData.BlockHeightPH))
                        block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHeightPH, block.BlockHeight.ToString(CultureInfo.InvariantCulture));
                    else if(blockInfobaseUrl.Contains(CoinMetaData.BlockHashPH) && !string.IsNullOrEmpty(block.Hash))
                        block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHashPH, block.Hash);
                }
            }
        }

        return blocks;
    }

    [HttpGet("/api/v2/pools/{poolId}/miners/{address}/blocks")]
    public async Task<PagedResultResponse<Responses.Block[]>> PageMinerBlocksV2Async(
        string poolId, string address, [FromQuery] int page, [FromQuery] int pageSize = 15, [FromQuery] BlockStatus[] state = null)
    {
        pageSize = ClampPageSize(pageSize);
        page = ClampPage(page);
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        if(string.IsNullOrEmpty(address))
            throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

        if(pool.Template.Family == CoinFamily.Ethereum)
            address = address.ToLower();

        var blockStates = state is { Length: > 0 } ?
            state :
            new[] { BlockStatus.Confirmed, BlockStatus.Pending, BlockStatus.Orphaned };

        uint itemCount = await cf.Run(con => blocksRepo.GetMinerBlockCountAsync(con, poolId, address, ct));
        uint pageCount = (uint) Math.Floor(itemCount / (double) pageSize);

        var blocks = (await cf.Run(con => blocksRepo.PageMinerBlocksAsync(con, pool.Id, address, blockStates, page, pageSize, ct)))
            .Select(mapper.ToResponse)
            .ToArray();

        // enrich blocks
        var blockInfobaseDict = pool.Template?.ExplorerBlockLinks;

        foreach(var block in blocks)
        {
            // compute infoLink
            if(blockInfobaseDict != null)
            {
                blockInfobaseDict.TryGetValue(!string.IsNullOrEmpty(block.Type) ? block.Type : "block", out var blockInfobaseUrl);

                if(!string.IsNullOrEmpty(blockInfobaseUrl))
                {
                    if(blockInfobaseUrl.Contains(CoinMetaData.BlockHeightPH))
                        block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHeightPH, block.BlockHeight.ToString(CultureInfo.InvariantCulture));
                    else if(blockInfobaseUrl.Contains(CoinMetaData.BlockHashPH) && !string.IsNullOrEmpty(block.Hash))
                        block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHashPH, block.Hash);
                }
            }
        }

        var response = new PagedResultResponse<Responses.Block[]>(blocks, itemCount, pageCount);
        return response;
    }

    [HttpGet("{poolId}/miners/{address}/payments")]
    public async Task<Responses.Payment[]> PageMinerPaymentsAsync(
        string poolId, string address, [FromQuery] int page, [FromQuery] int pageSize = 15)
    {
        pageSize = ClampPageSize(pageSize);
        page = ClampPage(page);
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        if(string.IsNullOrEmpty(address))
            throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

        if(pool.Template.Family == CoinFamily.Ethereum)
            address = address.ToLower();

        var payments = (await cf.Run(con => paymentsRepo.PagePaymentsAsync(
                con, pool.Id, address, page, pageSize, ct)))
            .Select(mapper.ToResponse)
            .ToArray();

        // enrich payments
        var txInfobaseUrl = pool.Template.ExplorerTxLink;
        var addressInfobaseUrl = pool.Template.ExplorerAccountLink;

        foreach(var payment in payments)
        {
            // compute transaction infoLink
            if(!string.IsNullOrEmpty(txInfobaseUrl))
                payment.TransactionInfoLink = string.Format(txInfobaseUrl, payment.TransactionConfirmationData);

            // pool wallet link
            if(!string.IsNullOrEmpty(addressInfobaseUrl))
                payment.AddressInfoLink = string.Format(addressInfobaseUrl, payment.Address);
        }

        return payments;
    }

    [HttpGet("/api/v2/pools/{poolId}/miners/{address}/payments")]
    public async Task<PagedResultResponse<Responses.Payment[]>> PageMinerPaymentsV2Async(
        string poolId, string address, [FromQuery] int page, [FromQuery] int pageSize = 15)
    {
        pageSize = ClampPageSize(pageSize);
        page = ClampPage(page);
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        if(string.IsNullOrEmpty(address))
            throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

        if(pool.Template.Family == CoinFamily.Ethereum)
            address = address.ToLower();

        uint itemCount = await cf.Run(con => paymentsRepo.GetPaymentsCountAsync(con, poolId, address, ct));
        uint pageCount = (uint) Math.Floor(itemCount / (double) pageSize);

        var payments = (await cf.Run(con => paymentsRepo.PagePaymentsAsync(
                con, pool.Id, address, page, pageSize, ct)))
            .Select(mapper.ToResponse)
            .ToArray();

        // enrich payments
        var txInfobaseUrl = pool.Template.ExplorerTxLink;
        var addressInfobaseUrl = pool.Template.ExplorerAccountLink;

        foreach(var payment in payments)
        {
            // compute transaction infoLink
            if(!string.IsNullOrEmpty(txInfobaseUrl))
                payment.TransactionInfoLink = string.Format(txInfobaseUrl, payment.TransactionConfirmationData);

            // pool wallet link
            if(!string.IsNullOrEmpty(addressInfobaseUrl))
                payment.AddressInfoLink = string.Format(addressInfobaseUrl, payment.Address);
        }

        var response = new PagedResultResponse<Responses.Payment[]>(payments, itemCount, pageCount);
        return response;
    }

    [HttpGet("{poolId}/miners/{address}/balancechanges")]
    public async Task<Responses.BalanceChange[]> PageMinerBalanceChangesAsync(
        string poolId, string address, [FromQuery] int page, [FromQuery] int pageSize = 15)
    {
        pageSize = ClampPageSize(pageSize);
        page = ClampPage(page);
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        if(string.IsNullOrEmpty(address))
            throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

        if(pool.Template.Family == CoinFamily.Ethereum)
            address = address.ToLower();

        var balanceChanges = (await cf.Run(con => paymentsRepo.PageBalanceChangesAsync(
                con, pool.Id, address, page, pageSize, ct)))
            .Select(mapper.ToResponse)
            .ToArray();

        return balanceChanges;
    }

    [HttpGet("/api/v2/pools/{poolId}/miners/{address}/balancechanges")]
    public async Task<PagedResultResponse<Responses.BalanceChange[]>> PageMinerBalanceChangesV2Async(
        string poolId, string address, [FromQuery] int page, [FromQuery] int pageSize = 15)
    {
        pageSize = ClampPageSize(pageSize);
        page = ClampPage(page);
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        if(string.IsNullOrEmpty(address))
            throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

        if(pool.Template.Family == CoinFamily.Ethereum)
            address = address.ToLower();

        uint itemCount = await cf.Run(con => paymentsRepo.GetBalanceChangesCountAsync(con, poolId, address));
        uint pageCount = (uint) Math.Floor(itemCount / (double) pageSize);

        var balanceChanges = (await cf.Run(con => paymentsRepo.PageBalanceChangesAsync(
                con, pool.Id, address, page, pageSize, ct)))
            .Select(mapper.ToResponse)
            .ToArray();

        var response = new PagedResultResponse<Responses.BalanceChange[]>(balanceChanges, itemCount, pageCount);
        return response;
    }

    [HttpGet("{poolId}/miners/{address}/earnings/daily")]
    public async Task<AmountByDate[]> PageMinerEarningsByDayAsync(
        string poolId, string address, [FromQuery] int page, [FromQuery] int pageSize = 15)
    {
        pageSize = ClampPageSize(pageSize);
        page = ClampPage(page);
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        if(string.IsNullOrEmpty(address))
            throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

        if(pool.Template.Family == CoinFamily.Ethereum)
            address = address.ToLower();

        var earnings = (await cf.Run(con => paymentsRepo.PageMinerPaymentsByDayAsync(
                con, pool.Id, address, page, pageSize, ct)))
            .ToArray();

        return earnings;
    }

    [HttpGet("/api/v2/pools/{poolId}/miners/{address}/earnings/daily")]
    public async Task<PagedResultResponse<AmountByDate[]>> PageMinerEarningsByDayV2Async(
        string poolId, string address, [FromQuery] int page, [FromQuery] int pageSize = 15)
    {
        pageSize = ClampPageSize(pageSize);
        page = ClampPage(page);
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        if(string.IsNullOrEmpty(address))
            throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

        if(pool.Template.Family == CoinFamily.Ethereum)
            address = address.ToLower();

        uint itemCount = await cf.Run(con => paymentsRepo.GetMinerPaymentsByDayCountAsync(con, poolId, address));
        uint pageCount = (uint) Math.Floor(itemCount / (double) pageSize);

        var earnings = (await cf.Run(con => paymentsRepo.PageMinerPaymentsByDayAsync(
                con, pool.Id, address, page, pageSize, ct)))
            .ToArray();

        var response = new PagedResultResponse<AmountByDate[]>(earnings, itemCount, pageCount);
        return response;
    }

    [HttpGet("{poolId}/miners/{address}/performance")]
    public async Task<Responses.WorkerPerformanceStatsContainer[]> GetMinerPerformanceAsync(
        string poolId, string address, [FromQuery] SampleRange mode = SampleRange.Day)
    {
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        if(string.IsNullOrEmpty(address))
            throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

        // the enum binds any integer; an undefined value used to fall through and answer 200 [], and with the
        // compile-time mapper (null collections stay null) it would answer 204
        if(!Enum.IsDefined(mode))
            throw new ApiException("Invalid mode", HttpStatusCode.BadRequest);

        if(pool.Template.Family == CoinFamily.Ethereum)
            address = address.ToLower();

        var result = await GetMinerPerformanceInternal(mode, pool, address, ct);

        return result;
    }

    [HttpGet("{poolId}/miners/{address}/settings")]
    public async Task<Responses.MinerSettings> GetMinerSettingsAsync(string poolId, string address)
    {
        var pool = GetPool(poolId);

        if(string.IsNullOrEmpty(address))
            throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

        if(pool.Template.Family == CoinFamily.Ethereum)
            address = address.ToLower();

        var result = await cf.Run(con => minerRepo.GetSettingsAsync(con, null, pool.Id, address));

        if(result == null)
            throw new ApiException("No settings found", HttpStatusCode.NotFound);

        return mapper.ToResponse(result);
    }

    [HttpPost("{poolId}/miners/{address}/settings")]
    public async Task<Responses.MinerSettings> SetMinerSettingsAsync(string poolId, string address,
        [FromBody] Requests.UpdateMinerSettingsRequest request, CancellationToken ct)
    {
        var pool = GetPool(poolId);

        if(string.IsNullOrEmpty(address))
            throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

        if(pool.Template.Family == CoinFamily.Ethereum)
            address = address.ToLower();

        if(request?.Settings == null)
            throw new ApiException("Invalid or missing settings", HttpStatusCode.BadRequest);

        if(!IPAddress.TryParse(request.IpAddress, out var requestIp))
            throw new ApiException("Invalid IP address", HttpStatusCode.BadRequest);

        // fetch recent IPs
        var ips = await cf.Run(con => shareRepo.GetRecentyUsedIpAddressesAsync(con, null, poolId, address, ct));

        // any known ips?
        if(ips == null || ips.Length == 0)
            throw new ApiException("Address not recently used for mining", HttpStatusCode.NotFound);

        // match?
        if(!ips.Any(x => IPAddress.TryParse(x, out var ipAddress) && ipAddress.IsEqual(requestIp)))
            throw new ApiException("None of the recently used IP addresses matches the request", HttpStatusCode.Forbidden);

        // map settings
        var mapped = mapper.ToModel(request.Settings);

        // clamp limit
        if(pool.PaymentProcessing != null)
            mapped.PaymentThreshold = Math.Max(mapped.PaymentThreshold, pool.PaymentProcessing.MinimumPayment);

        mapped.PoolId = pool.Id;
        mapped.Address = address;

        // finally update the settings
        return await cf.RunTx(async (con, tx) =>
        {
            await minerRepo.UpdateSettingsAsync(con, tx, mapped);

            logger.Info(() => $"Updated settings for pool {pool.Id}, miner {address}");

            var result = await minerRepo.GetSettingsAsync(con, tx, mapped.PoolId, mapped.Address);
            return mapper.ToResponse(result);
        });
    }

    #endregion // Actions

    private async Task<Responses.WorkerPerformanceStatsContainer[]> GetMinerPerformanceInternal(
        SampleRange mode, PoolConfig pool, string address, CancellationToken ct)
    {
        Persistence.Model.Projections.WorkerPerformanceStatsContainer[] stats = null;
        var end = clock.Now;
        DateTime start;

        switch(mode)
        {
            case SampleRange.Hour:
                end = end.AddSeconds(-end.Second);

                start = end.AddHours(-1);

                stats = await cf.Run(con => statsRepo.GetMinerPerformanceBetweenThreeMinutelyAsync(con, pool.Id, address, start, end, ct));
                break;

            case SampleRange.Day:
                // set range
                if(end.Minute < 30)
                    end = end.AddHours(-1);

                end = end.AddMinutes(-end.Minute);
                end = end.AddSeconds(-end.Second);

                start = end.AddDays(-1);

                stats = await cf.Run(con => statsRepo.GetMinerPerformanceBetweenHourlyAsync(con, pool.Id, address, start, end, ct));
                break;

            case SampleRange.Month:
                if(end.Hour < 12)
                    end = end.AddDays(-1);

                end = end.Date;

                // set range
                start = end.AddMonths(-1);

                stats = await cf.Run(con => statsRepo.GetMinerPerformanceBetweenDailyAsync(con, pool.Id, address, start, end, ct));
                break;
        }

        // map
        var result = mapper.ToResponse(stats);
        return result;
    }

    [HttpGet("{poolId}/auxblocks")]
    public async Task<Responses.AuxBlock[]> PagePoolAuxBlocksAsync(
        string poolId, [FromQuery] int page, [FromQuery] int pageSize = 15, [FromQuery] BlockStatus[] state = null)
    {
        pageSize = ClampPageSize(pageSize);
        page = ClampPage(page);
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        var blockStates = state is { Length: > 0 } ?
            state :
            new[] { BlockStatus.Confirmed, BlockStatus.Pending, BlockStatus.Orphaned };

        var blocks = (await cf.Run(con => auxBlocksRepo.PagePoolAuxBlocksAsync(con, poolId, blockStates, page, pageSize, ct)))
            .Select(mapper.ToResponse)
            .ToArray();

        EnrichAuxBlockInfoLinks(pool, blocks);
        return blocks;
    }

    [HttpGet("{poolId}/miners/{address}/auxblocks")]
    public async Task<Responses.AuxBlock[]> PageMinerAuxBlocksAsync(
        string poolId, string address, [FromQuery] int page, [FromQuery] int pageSize = 15, [FromQuery] BlockStatus[] state = null)
    {
        pageSize = ClampPageSize(pageSize);
        page = ClampPage(page);
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        if(string.IsNullOrEmpty(address))
            throw new ApiException("Invalid or missing miner address", System.Net.HttpStatusCode.NotFound);

        var blockStates = state is { Length: > 0 } ?
            state :
            new[] { BlockStatus.Confirmed, BlockStatus.Pending, BlockStatus.Orphaned };

        var blocks = (await cf.Run(con => auxBlocksRepo.PageMinerAuxBlocksAsync(con, poolId, address, blockStates, page, pageSize, ct)))
            .Select(mapper.ToResponse)
            .ToArray();

        EnrichAuxBlockInfoLinks(pool, blocks);
        return blocks;
    }

    [HttpGet("/api/summary")]
    public async Task<GetSummaryResponse> GetSummaryAsync(CancellationToken ct)
    {
        const string cacheKey = "api-summary";
        if(cache.TryGetValue(cacheKey, out GetSummaryResponse cached))
            return cached;

        var todayUtc = DateTime.UtcNow.Date;

        var poolEntries = await Task.WhenAll(clusterConfig.Pools.Where(x => x.Enabled).Select(async config =>
        {
            var stats = await cf.Run(con => statsRepo.GetLastPoolStatsAsync(con, config.Id, ct));
            pools.TryGetValue(config.Id, out var poolInstance);

            var totalBlocks = await cf.Run(con => blocksRepo.GetPoolBlockCountAsync(con, config.Id, ct));
            var blocksToday = await cf.Run(con => blocksRepo.GetPoolBlockCountSinceAsync(con, config.Id, todayUtc, ct));
            var lastBlockTime = await cf.Run(con => blocksRepo.GetLastPoolBlockTimeAsync(con, config.Id, ct));
            var blockReward = await cf.Run(con => blocksRepo.GetLastConfirmedBlockRewardAsync(con, config.Id, ct));
            if(blockReward == 0 && poolInstance != null)
                blockReward = poolInstance.NetworkStats?.BlockReward ?? 0;

            double? poolEffort = null;
            if(lastBlockTime.HasValue && poolInstance != null)
            {
                var effort = await cf.Run(con => shareRepo.GetEffortBetweenCreatedAsync(con, config.Id, poolInstance.EffortShareConstant, lastBlockTime.Value, clock.Now, ct));
                if(effort.HasValue)
                    poolEffort = effort.Value;
            }

            SummaryMergeMineEntry[] mergeMinedCoins = null;
            var bitcoinExtra = config.Extra?.SafeExtensionDataAs<Blockchain.Bitcoin.Configuration.BitcoinPoolConfigExtra>();
            var entries = new List<SummaryMergeMineEntry>();
            if(bitcoinExtra?.AuxChains is { Length: > 0 })
                entries.AddRange(bitcoinExtra.AuxChains.Select(a => new SummaryMergeMineEntry { Id = a.Id, Name = a.Name ?? a.Id }));
            if(bitcoinExtra?.RskChain != null)
                entries.Add(new SummaryMergeMineEntry { Id = bitcoinExtra.RskChain.Id, Name = bitcoinExtra.RskChain.Name ?? bitcoinExtra.RskChain.Id });
            if(bitcoinExtra?.HathorChain != null)
                entries.Add(new SummaryMergeMineEntry { Id = bitcoinExtra.HathorChain.Id, Name = bitcoinExtra.HathorChain.Name ?? bitcoinExtra.HathorChain.Id });
            if(entries.Count > 0)
            {
                // Per-chain counts in one query, so the UI's merge-mining page no longer has to pull
                // thousands of aux block rows every minute just to count them per chain.
                var chainStats = await cf.Run(con => auxBlocksRepo.GetChainSummariesAsync(con, config.Id, todayUtc, ct));
                foreach(var entry in entries)
                {
                    var cs = chainStats.FirstOrDefault(x => string.Equals(x.ChainId, entry.Id, StringComparison.OrdinalIgnoreCase));
                    if(cs == null)
                        continue;
                    entry.TotalBlocks = cs.Total;
                    entry.BlocksToday = cs.Today;
                    entry.LastBlockTime = cs.LastCreated;
                    entry.LastReward = cs.LastReward;
                }
                mergeMinedCoins = entries.ToArray();
            }

            var auxBlocksToday = mergeMinedCoins != null
                ? await cf.Run(con => auxBlocksRepo.GetPoolAuxBlockCountSinceAsync(con, config.Id, todayUtc, ct))
                : 0;
            var totalAuxBlocks = mergeMinedCoins != null
                ? await cf.Run(con => auxBlocksRepo.GetPoolAuxBlockCountAsync(con, config.Id, ct))
                : 0;
            var lastAuxBlockTime = mergeMinedCoins != null
                ? await cf.Run(con => auxBlocksRepo.GetLastPoolAuxBlockTimeAsync(con, config.Id, ct))
                : null;

            return new SummaryPoolEntry
            {
                Id = config.Id,
                Scheme = config.PaymentProcessing?.PayoutScheme.ToString(),
                ConnectedMiners = stats?.ConnectedMiners ?? 0,
                ConnectedWorkers = stats?.ConnectedWorkers ?? 0,
                PoolHashrate = stats?.PoolHashrate ?? 0,
                NetworkHashrate = stats?.NetworkHashrate ?? 0,
                NetworkDifficulty = stats?.NetworkDifficulty ?? 0,
                BlockHeight = stats?.BlockHeight ?? 0,
                PoolEffort = poolEffort,
                LastPoolBlockTime = lastBlockTime,
                BlockReward = blockReward,
                Coin = new SummaryPoolCoin
                {
                    Symbol = config.Template?.Symbol,
                    Algorithm = config.Template?.GetAlgorithmName()
                },
                BlocksToday = blocksToday,
                TotalBlocks = totalBlocks,
                AuxBlocksToday = auxBlocksToday,
                TotalAuxBlocks = totalAuxBlocks,
                LastAuxBlockTime = lastAuxBlockTime,
                Fee = config.RewardRecipients != null ? (float) config.RewardRecipients.Sum(x => x.Percentage) : 0,
                MinimumPayment = config.PaymentProcessing?.MinimumPayment ?? 0,
                MergeMinedCoins = mergeMinedCoins
            };
        }).ToArray());

        var totals = new SummaryTotals
        {
            TotalHashrate = poolEntries.Sum(p => p.PoolHashrate),
            TotalMiners = poolEntries.Sum(p => p.ConnectedMiners),
            TotalWorkers = poolEntries.Sum(p => p.ConnectedWorkers),
            ActivePools = poolEntries.Count(p => p.ConnectedMiners > 0),
            PoolCount = poolEntries.Length,
            TotalBlocksAllTime = poolEntries.Sum(p => (long) p.TotalBlocks),
            TotalBlocksToday = poolEntries.Sum(p => p.BlocksToday),
            TotalAuxBlocksAllTime = poolEntries.Sum(p => p.TotalAuxBlocks),
            TotalAuxBlocksToday = poolEntries.Sum(p => p.AuxBlocksToday)
        };

        var response = new GetSummaryResponse
        {
            GeneratedAt = clock.Now,
            Stale = false,
            Pools = poolEntries,
            Totals = totals
        };

        cache.Set(cacheKey, response, TimeSpan.FromSeconds(15));
        return response;
    }

    private static void EnrichAuxBlockInfoLinks(Configuration.PoolConfig pool, Responses.AuxBlock[] blocks)
    {
        var bitcoinExtra = pool.Extra?.SafeExtensionDataAs<Blockchain.Bitcoin.Configuration.BitcoinPoolConfigExtra>();
        if(bitcoinExtra == null)
            return;

        // (explorerBlockLink, requiredConfirmations) per chain id — aux tree chains
        // plus the standalone RSK and Hathor commitments, which live in their own config keys
        var chainMap = new Dictionary<string, (string Link, int Confirmations)>(StringComparer.OrdinalIgnoreCase);

        if(bitcoinExtra.AuxChains != null)
            foreach(var a in bitcoinExtra.AuxChains)
                chainMap[a.Id] = (a.ExplorerBlockLink, a.RequiredConfirmations);

        if(bitcoinExtra.RskChain != null)
            chainMap[bitcoinExtra.RskChain.Id] = (bitcoinExtra.RskChain.ExplorerBlockLink, bitcoinExtra.RskChain.RequiredConfirmations);

        if(bitcoinExtra.HathorChain != null)
            chainMap[bitcoinExtra.HathorChain.Id] = (bitcoinExtra.HathorChain.ExplorerBlockLink, bitcoinExtra.HathorChain.RequiredConfirmations);

        if(chainMap.Count == 0)
            return;

        foreach(var block in blocks)
        {
            if(!chainMap.TryGetValue(block.ChainId ?? "", out var chainCfg))
                continue;

            block.RequiredConfirmations = chainCfg.Confirmations > 0 ? chainCfg.Confirmations : 100;

            if(!string.IsNullOrEmpty(chainCfg.Link))
            {
                var link = chainCfg.Link;
                if(block.AuxBlockHash != null) link = link.Replace("{hash}", block.AuxBlockHash);
                if(block.BlockHeight.HasValue) link = link.Replace("{height}", block.BlockHeight.Value.ToString());
                if(!link.Contains('{')) block.InfoLink = link;
            }
        }
    }
}
