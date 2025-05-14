using Quartz;
using System.Web;
using System.Net.Http.Json;
using Option.Data.Database;
using JetBrains.Annotations;
using Option.Data.Shared.Dto;
using Microsoft.EntityFrameworkCore;
using Option.Data.Shared.Configuration;
using Option.Data.Shared.Poco;

namespace Option.Data.Scheduler.Jobs;

[UsedImplicitly]
[DisallowConcurrentExecution]
public class CleanupJob : IJob
{
    private readonly ILogger<CleanupJob> _logger;
    private readonly HttpClient _httpClient;
    private readonly ApplicationDbContext _dbContext;
    private readonly DateTimeOffset _dateTimeOffset;

    public CleanupJob(ILogger<CleanupJob> logger, IHttpClientFactory clientFactory, ApplicationDbContext dbContext)
    {
        _logger = logger;
        _httpClient = clientFactory.CreateClient(DeribitConfig.ClientName);
        _dbContext = dbContext;
        _dateTimeOffset = DateTimeOffset.UtcNow;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("CleanupJob executed at: {time}", _dateTimeOffset);

        List<CurrencyType> currencies = await _dbContext.CurrencyType
            .ToListAsync();

        foreach (CurrencyType currency in currencies)
        {
            _logger.LogInformation("Processing currency: {currency}", currency.Name);

            var queryParams = HttpUtility.ParseQueryString(string.Empty);
            queryParams["currency"] = currency.Name;
            queryParams["kind"] = "option";
            string expirationsEndpoint = $"get_expirations?{queryParams}";

            try
            {
                ExpirationsByInstrument? expirationsByInstrument =
                    await _httpClient.GetFromJsonAsync<ExpirationsByInstrument>(expirationsEndpoint);

                if (expirationsByInstrument?.Data == null || expirationsByInstrument.Data.Count == 0 ||
                    !expirationsByInstrument.TryGetOptions(currency.Name, out var expirations))
                {
                    _logger.LogWarning("No data returned expirations for {currency}", currency.Name);
                    continue;
                }

                int deletedCount = await _dbContext.DeribitData
                    .Where(x=> x.CurrencyTypeId == currency.Id)
                    .Where(d =>
                        !expirations.Contains(d.Expiration) &&
                        d.CreatedAt < _dateTimeOffset.AddDays(-1))
                    .ExecuteDeleteAsync();

                _logger.LogInformation("Deleted {Count} expired DeribitData for {Currency} records", deletedCount, currency.Name);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error retrieving expirations for {currency}", currency.Name);
            }
            
            
        }
        
        _logger.LogInformation("Finished executed CleanupJob at {Total} seconds", (DateTimeOffset.UtcNow -_dateTimeOffset).Seconds );
    }
}