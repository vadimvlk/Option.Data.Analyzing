using Quartz;
using Mapster;
using System.Web;
using System.Net.Http.Json;
using Option.Data.Database;
using Option.Data.Shared.Dto;
using Option.Data.Shared.Poco;
using Option.Data.Shared.Configuration;

namespace Option.Data.Scheduler.Jobs;

[DisallowConcurrentExecution]
public class DeribitJob : IJob
{
    private readonly ILogger<DeribitJob> _logger;
    private readonly HttpClient _httpClient;
    private readonly ApplicationDbContext _dbContext;

    public DeribitJob(ILogger<DeribitJob> logger, IHttpClientFactory httpClientFactory, ApplicationDbContext dbContext)
    {
        _logger = logger;
        _dbContext = dbContext;
        _httpClient = httpClientFactory.CreateClient(DeribitConfig.ClientName);
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("DeribitJob executed at: {time}", DateTimeOffset.Now);

        foreach (CurrencyType currency in Enum.GetValues(typeof(CurrencyType)))
        {
            _logger.LogInformation("Processing currency: {currency}", currency);

            var queryParams = HttpUtility.ParseQueryString(string.Empty);
            queryParams["currency"] = currency.ToString();
            queryParams["kind"] = "option";
            string endpoint = $"get_book_summary_by_currency?{queryParams}";

            try
            {
                BookSummaryByInstrument? summaryData =
                    await _httpClient.GetFromJsonAsync<BookSummaryByInstrument>(endpoint);
                if (summaryData?.Data == null || summaryData.Data.Count == 0)
                {
                    _logger.LogWarning("No data returned for {currency}", currency);
                    continue;
                }

                // Parse and map data
                List<OptionData> optionData = ParseAndMapBookSummary(summaryData.Data);


                await _dbContext.OptionData.AddRangeAsync(optionData);
                await _dbContext.SaveChangesAsync();


                _logger.LogInformation("Successfully processed {count} records for {currency}",
                    optionData.Count, currency);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving data for {currency}", currency);
            }
        }
    }

    private List<OptionData> ParseAndMapBookSummary(List<BookSummaryData> summaryDataList)
    {
        List<OptionData> optionDataList = new();

       foreach (var data in summaryDataList.Where(data => !string.IsNullOrEmpty(data.InstrumentName)))
       {
           // Parse instrument name
           var (parsedCurrency, expiration, strike, optionType) = ParseInstrumentName(data.InstrumentName!);

           // Create option data
           var optionData = data.Adapt<OptionData>();

           // Set values from the parsed instrument name
           optionData.Currency = parsedCurrency;
           optionData.Strike = strike;
           optionData.Type = optionType;
           optionData.Expiration = expiration;
           optionData.InstrumentName = data.InstrumentName!;

           // Set call/put specific data
           if (optionType == OptionType.Call)
           {
               optionData.CallOi = data.OpenInterest ?? 0;
               optionData.PutOi = 0;
               optionData.CallPrice = (data.MarkPrice ?? 0) * (data.UnderlyingPrice ?? 0);
               optionData.PutPrice = 0;
           }
           else
           {
               optionData.PutOi = data.OpenInterest ?? 0;
               optionData.CallOi = 0;
               optionData.PutPrice = (data.MarkPrice ?? 0) * (data.UnderlyingPrice ?? 0);
               optionData.CallPrice = 0;
           }

           optionDataList.Add(optionData);
       }

        return optionDataList;
    }

    private static (CurrencyType Currency, string Expiration, int Strike, OptionType Type) ParseInstrumentName(
        string instrumentName)
    {
        // Example: "ETH-25JUL25-2400-C"
        string[] parts = instrumentName.Split('-');

        if (parts.Length != 4)
        {
            throw new ArgumentException($"Invalid instrument name format: {instrumentName}");
        }

        // Parse currency
        if (!Enum.TryParse(parts[0], out CurrencyType currency))
        {
            throw new ArgumentOutOfRangeException($"Unknown currency: {parts[0]}");
        }

        // Parse expiration (25JUL25)
        string expiration = parts[1];

        // Parse strike
        if (!int.TryParse(parts[2], out int strike))
        {
            throw new ArgumentException($"Invalid strike price: {parts[2]}");
        }

        // Parse option type (C or P)
        OptionType optionType;
        switch (parts[3])
        {
            case "C":
                optionType = OptionType.Call;
                break;
            case "P":
                optionType = OptionType.Put;
                break;
            default:
                throw new ArgumentOutOfRangeException($"Unknown option type: {parts[3]}");
        }

        return (currency, expiration, strike, optionType);
    }
}