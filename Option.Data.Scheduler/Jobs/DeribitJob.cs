using Quartz;
using Mapster;
using System.Web;
using System.Net.Http.Json;
using Option.Data.Database;
using Option.Data.Shared.Dto;
using Option.Data.Shared.Poco;
using Microsoft.EntityFrameworkCore;
using Option.Data.Shared.Configuration;

namespace Option.Data.Scheduler.Jobs;

[DisallowConcurrentExecution]
public class DeribitJob : IJob
{
    private readonly ILogger<DeribitJob> _logger;
    private readonly HttpClient _httpClient;
    private readonly ApplicationDbContext _dbContext;
    private readonly DateTimeOffset _dateTimeOffset;

    public DeribitJob(ILogger<DeribitJob> logger,
        IHttpClientFactory httpClientFactory,
        ApplicationDbContext dbContext)
    {
        _logger = logger;
        _dbContext = dbContext;
        _httpClient = httpClientFactory.CreateClient(DeribitConfig.ClientName);
        _dateTimeOffset = DateTimeOffset.UtcNow;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("DeribitJob executed at: {time}", _dateTimeOffset);

        List<string> currencies = await _dbContext.CurrencyType
            .Select(x => x.Name)
            .ToListAsync();

        foreach (string currency in currencies)
        {
            _logger.LogInformation("Processing currency: {currency}", currency);

            var queryParams = HttpUtility.ParseQueryString(string.Empty);
            queryParams["currency"] = currency;
            queryParams["kind"] = "option";
            string bookSummaryEndpoint = $"get_book_summary_by_currency?{queryParams}";

            try
            {
                BookSummaryByInstrument? summaryData =
                    await _httpClient.GetFromJsonAsync<BookSummaryByInstrument>(bookSummaryEndpoint);

                if (summaryData?.Data == null || summaryData.Data.Count == 0)
                {
                    _logger.LogWarning("No data returned for {currency}", currency);
                    continue;
                }

                // Parse and map data
                List<DeribitData> optionData = await ParseAndMapBookSummaryAsync(summaryData.Data);

                await _dbContext.DeribitData.AddRangeAsync(optionData);
                await _dbContext.SaveChangesAsync();


                _logger.LogInformation("Successfully processed {count} records for {currency}",
                    optionData.Count, currency);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving data for {currency}", currency);
            }
        }
        _logger.LogInformation("Finished executed DeribitJob at {Total} minutes", (DateTimeOffset.UtcNow -_dateTimeOffset).Minutes );
    }

    private async Task<List<DeribitData>> ParseAndMapBookSummaryAsync(List<BookSummaryData> summaryDataList)
    {
        List<DeribitData> optionDataList = new();

        Dictionary<string, OptionType> optionTypes = await _dbContext.OptionType.ToDictionaryAsync(o => o.Name);
        Dictionary<string, CurrencyType> currencyTypes = await _dbContext.CurrencyType.ToDictionaryAsync(c => c.Name);

        foreach (var data in summaryDataList.Where(data => !string.IsNullOrEmpty(data.InstrumentName)))
        {
            // Parse instrument name
            var (parsedCurrency, expiration, strike, optionType) = ParseInstrumentName(data.InstrumentName!);

            // Create option data
            DeribitData deribitData = data.Adapt<DeribitData>();

            // Set values from the parsed instrument name
            deribitData.CurrencyTypeId = currencyTypes[parsedCurrency].Id;

            deribitData.Strike = strike;
            deribitData.OptionTypeId = optionTypes[optionType].Id;

            deribitData.Expiration = expiration;
            deribitData.CreatedAt = _dateTimeOffset;

            // GET GREEKS new API CALL.
            var queryParams = HttpUtility.ParseQueryString(string.Empty);
            queryParams["instrument_name"] = deribitData.InstrumentName;

            OrderBookByInstrument? orderBook =
                await _httpClient.GetFromJsonAsync<OrderBookByInstrument>($"get_order_book?{queryParams}");

            deribitData.Delta = orderBook?.Data?.Greeks?.Delta ?? 0;
            deribitData.Gamma = orderBook?.Data?.Greeks?.Gamma ?? 0;

            optionDataList.Add(deribitData);
            await Task.Delay(500);
        }

        return optionDataList;
    }

    private static (string Currency, string Expiration, int Strike, string Type) ParseInstrumentName(
        string instrumentName)
    {
        // Example: "ETH-25JUL25-2400-C"
        string[] parts = instrumentName.Split('-');

        if (parts.Length != 4)
        {
            throw new ArgumentException($"Invalid instrument name format: {instrumentName}");
        }

        // Parse currency (ETH)
        string currency = parts[0];

        // Parse expiration (25JUL25)
        string expiration = parts[1];

        // Parse strike (2400)
        if (!int.TryParse(parts[2], out int strike))
        {
            throw new ArgumentException($"Invalid strike price: {parts[2]}");
        }

        // Parse Type (C)
        string optionType = parts[3] switch
        {
            "C" => "Call",
            "P" => "Put",
            _ => throw new ArgumentOutOfRangeException($"Unknown option type: {parts[3]}")
        };

        return (currency, expiration, strike, optionType);
    }
}