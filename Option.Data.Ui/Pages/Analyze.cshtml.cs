using Option.Data.Database;
using Option.Data.Ui.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;
using Option.Data.Shared.Dto;
using Option.Data.Shared.Poco;
using Option.Data.Ui.Services;

namespace Option.Data.Ui.Pages;

public class Analyze : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly IMemoryCache _cache;
    private readonly ILogger<Analyze> _logger;
    private readonly IOptionsAnalysisHtmlBuilder _htmlBuilder;

    public Analyze(ApplicationDbContext context, IMemoryCache cache, 
        ILogger<Analyze> logger, 
        IOptionsAnalysisHtmlBuilder htmlBuilder)
    {
        _context = context;
        _cache = cache;
        _logger = logger;
        _htmlBuilder = htmlBuilder;
    }

    [BindProperty]
    public OptionViewModel ViewModel { get; set; } = new();
    public async Task OnGetAsync()
    {
        try
        {
            // Загружаем доступные валюты
            ViewModel.Currencies = (await _cache.GetOrCreateAsync("CurrencyTypes", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(3);
                return await _context.CurrencyType
                    .OrderBy(c => c.Name)
                    .ToListAsync();
            }))!;

            // Загружаем даты экспирации (уникальные значения)
            ViewModel.Expirations = (await _cache.GetOrCreateAsync("Expirations", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
                return await _context.DeribitData
                    .Select(o => o.Expiration)
                    .Distinct()
                    .OrderBy(e => e)
                    .ToListAsync();
            }))!;

            // Загружаем доступные даты/время (группируем по CreatedAt)
            ViewModel.AvailableDates = (await _cache.GetOrCreateAsync("AvailableDates", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
                return await _context.DeribitData
                    .Select(o => o.CreatedAt)
                    .Distinct()
                    .OrderBy(d => d)
                    .ToListAsync();
            }))!;
        }
        catch (Exception e)
        {
            ModelState.AddModelError(string.Empty, $"Error loading data: {e.Message}");
            _logger.LogError(e, "Error loading data");
        }
    }
    
     public async Task<IActionResult> OnPostLoadDataAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        ViewModel.Currencies = _cache.Get<List<CurrencyType>>("CurrencyTypes") ?? new List<CurrencyType>();
        ViewModel.Expirations = _cache.Get<List<string>>("Expirations") ?? new List<string>();
        ViewModel.AvailableDates = _cache.Get<List<DateTimeOffset>>("AvailableDates") ?? new List<DateTimeOffset>();

        // Create a cache key based on the selected parameters
        string cacheKey = $"DataValidation_{ViewModel.SelectedExpiration}_{ViewModel.SelectedDateTime}";
        string dataCacheKey =
            $"FilteredData_{ViewModel.SelectedCurrencyId}_{ViewModel.SelectedExpiration}_{ViewModel.SelectedDateTime}";

        bool isValidTime = await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(3);
            return await _context.DeribitData
                .AnyAsync(d => d.Expiration == ViewModel.SelectedExpiration &&
                               d.CreatedAt == ViewModel.SelectedDateTime);
        });

        if (!isValidTime)
        {
            ModelState.AddModelError("ViewModel.SelectedDateTime",
                "Please select a valid date/time from the available options");
            return Page();
        }

        // Get all data matching the filter criteria
        List<DeribitData> filteredData = (await _cache.GetOrCreateAsync(dataCacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
            return await _context.DeribitData
                .Where(d => d.CurrencyTypeId == ViewModel.SelectedCurrencyId &&
                            d.Expiration == ViewModel.SelectedExpiration &&
                            d.CreatedAt == ViewModel.SelectedDateTime)
                .ToListAsync();
        }))!;


        // Group by strike and option type to combine data for each strike
        Dictionary<int, DeribitData> callOptions = filteredData.Where(d => d.OptionTypeId == 1) // CALL
            .ToDictionary(d => d.Strike);

        Dictionary<int, DeribitData> putOptions = filteredData.Where(d => d.OptionTypeId == 2) // PUT
            .ToDictionary(d => d.Strike);

        // Get all unique strikes
        HashSet<int> allStrikes = filteredData.Select(d => d.Strike).ToHashSet();
        
        ViewModel.UnderlyingPrice = filteredData.First().UnderlyingPrice;

        ViewModel.OptionData = allStrikes.OrderBy(strike => strike)
            .Select(strike =>
            {
                callOptions.TryGetValue(strike, out var call);
                putOptions.TryGetValue(strike, out var put);

                return new OptionData
                {
                    Strike = strike,
                    // Call data
                    CallOi = call?.OpenInterest ?? 0,
                    CallPrice = call != null ? call.MarkPrice * call.UnderlyingPrice : 0,
                    CallDelta = call?.Delta ?? 0,
                    CallGamma = call?.Gamma ?? 0,
                    // Use IV from call if available, otherwise from put
                    Iv = call?.Iv ?? (put?.Iv ?? 0),
                    // Put data
                    PutOi = put?.OpenInterest ?? 0,
                    PutPrice = put != null ? put.MarkPrice * put.UnderlyingPrice : 0,
                    PutDelta = put?.Delta ?? 0,
                    PutGamma = put?.Gamma ?? 0
                };
            })
            .ToList();
        
        return Page();
    }
}