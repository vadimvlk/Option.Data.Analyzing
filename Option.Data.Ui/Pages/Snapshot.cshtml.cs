﻿using System.Web;
using Option.Data.Database;
using Option.Data.Shared.Dto;
using Microsoft.AspNetCore.Mvc;
using Option.Data.Shared.Configuration;
using Microsoft.Extensions.Caching.Memory;

namespace Option.Data.Ui.Pages;

public class SnapshotModel(
    ApplicationDbContext context,
    IMemoryCache cache,
    ILogger<SnapshotModel> logger,
    IHttpClientFactory clientFactory)
    : BaseOptionPageModel(context, cache, logger)
{
    private readonly IMemoryCache _cache = cache;
    private readonly HttpClient _httpClient = clientFactory.CreateClient(DeribitConfig.ClientName);

    public async Task OnGetAsync() => await LoadCommonDataAsync(useAvailableDates: false);

    public async Task<IActionResult> OnPostAsync()
    {
        
        await LoadCommonDataAsync(useAvailableDates: false);
        
        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            string dataCacheKey = $"DeribitData_{ViewModel.SelectedCurrencyId}";
          
            var currency = ViewModel.Currencies.Single(c => c.Id == ViewModel.SelectedCurrencyId).Name;

            var queryParams = HttpUtility.ParseQueryString(string.Empty);
            queryParams["currency"] = currency;
            queryParams["kind"] = "option";

            List<BookSummaryData>? optionsData = (await _cache.GetOrCreateAsync(dataCacheKey, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);

                BookSummaryByInstrument? summaryData =
                    await _httpClient.GetFromJsonAsync<BookSummaryByInstrument>(
                        $"get_book_summary_by_currency?{queryParams}");

                if (summaryData?.Data == null || summaryData.Data.Count == 0)
                {
                    logger.LogWarning("No data returned from Deribit server");
                    return [];
                }

                return summaryData.Data;
            }))!;

            // Process option data
            ProcessOptionData(optionsData);

            return Page();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error loading option data");
            ModelState.AddModelError(string.Empty, $"Error loading option data: {e.Message}");
            return Page();
        }
    }

    private void ProcessOptionData(List<BookSummaryData>? filteredData)
    {
        if (filteredData == null || filteredData.Count == 0)
        {
            ViewModel.OptionData = [];
            return;
        }

        // Parse all instrument names first
        var parsedInstruments = filteredData
            .Select(d =>
            {
                try
                {
                    var parsed = ParseInstrumentName(d.InstrumentName!);
                    return new { Data = d, Parsed = parsed };
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to parse instrument name: {ArgInstrumentName}", d.InstrumentName);
                    return null;
                }
            })
            .Where(x => x != null
                        && x.Parsed.Expiration == ViewModel.SelectedExpiration)
            .ToList();

        // Group-by-strike and option type to combine data for each strike
        Dictionary<int, BookSummaryData> callOptions = parsedInstruments
            .Where(x => x!.Parsed.Type == "Call")
            .ToDictionary(x => x!.Parsed.Strike, x => x.Data);

        Dictionary<int, BookSummaryData> putOptions = parsedInstruments
            .Where(x => x!.Parsed.Type == "Put")
            .ToDictionary(x => x!.Parsed.Strike, x => x.Data);

        HashSet<int> allStrikes = parsedInstruments
            .Select(x => x!.Parsed.Strike)
            .ToHashSet();

        ViewModel.UnderlyingPrice = parsedInstruments.Max(x => x!.Data.UnderlyingPrice) ?? 0;

        ViewModel.OptionData = allStrikes.OrderBy(strike => strike)
            .Select(strike =>
            {
                callOptions.TryGetValue(strike, out var call);
                putOptions.TryGetValue(strike, out var put);

                return new OptionData
                {
                    Strike = strike,
                    CallOi = call?.OpenInterest ?? 0,
                    CallPrice = call?.MarkPrice * call?.UnderlyingPrice ?? 0,
                    CallDelta = 0,
                    CallGamma = 0,
                    Iv = call?.MarkIv ?? (put?.MarkIv ?? 0),
                    PutOi = put?.OpenInterest ?? 0,
                    PutPrice = put?.MarkPrice * put?.UnderlyingPrice ?? 0,
                    PutDelta = 0,
                    PutGamma = 0
                };
            })
            .ToList();
    }

    protected override async Task<List<string>> GetOrCreateExpirationsAsync()
    {
        return (await _cache.GetOrCreateAsync("AvailableExpirations", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15);

            Expirations? expirations =
                await _httpClient.GetFromJsonAsync<Expirations>("get_expirations?currency=any&kind=option");

            if (expirations?.Data.Options != null && expirations.Data.Options.Count != 0)
                return expirations.Data.Options;

            logger.LogWarning("No data returned for expirations");
            return [];
        }))!;
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