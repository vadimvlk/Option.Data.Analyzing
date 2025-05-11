using System.Net.Http.Json;
using System.Web;
using Option.Data.Shared.Configuration;
using Option.Data.Shared.Dto;
using Quartz;

namespace Option.Data.Scheduler.Jobs;

[DisallowConcurrentExecution]
public class DeribitJob: IJob
{
    private readonly ILogger<DeribitJob> _logger;
    private readonly HttpClient _httpClient;

    public DeribitJob(ILogger<DeribitJob> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient(DeribitConfig.ClientName);
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("DeribitJob executed at: {time}", DateTimeOffset.Now);
    
        var queryParams = HttpUtility.ParseQueryString(string.Empty);
        queryParams["currency"] = "ETH";
        queryParams["kind"]= "option";
        var endpoint = $"get_book_summary_by_currency?{queryParams}";



        var qq =  await _httpClient.GetFromJsonAsync<BookSummaryByInstrument>(endpoint);
        return ;
    }
}