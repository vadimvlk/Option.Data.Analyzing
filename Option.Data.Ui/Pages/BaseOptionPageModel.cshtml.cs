using System.Globalization;
using Option.Data.Database;
using Option.Data.Ui.Models;
using Option.Data.Shared.Dto;
using Option.Data.Shared.Poco;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;

namespace Option.Data.Ui.Pages;

public abstract class BaseOptionPageModel(ApplicationDbContext context, IMemoryCache cache, ILogger logger) : PageModel
{
    [BindProperty]
    public OptionViewModel ViewModel { get; set; } = new();

    // Shared method to load initial data
    protected async Task LoadCommonDataAsync()
    {
        try
        {
            ViewModel.Currencies = await GetOrCreateCurrencyTypesAsync();
            ViewModel.Expirations = await GetOrCreateExpirationsAsync();
            ViewModel.AvailableDates = await GetOrCreateAvailableDatesAsync();
            
            // Set datetime if not already.
            if (ViewModel.SelectedDateTime == default && ViewModel.AvailableDates.Count != 0)
            {
                ViewModel.SelectedDateTime = ViewModel.AvailableDates.Last();
            }
        }
        catch (Exception e)
        {
            ModelState.AddModelError(string.Empty, $"Error loading data: {e.Message}");
            ViewModel.Currencies = new List<CurrencyType>();
            ViewModel.Expirations = new List<string>();
            ViewModel.AvailableDates = new List<DateTimeOffset>();
        }
    }

    private async Task<List<CurrencyType>> GetOrCreateCurrencyTypesAsync()
    {
        return (await cache.GetOrCreateAsync("CurrencyTypes", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(3);
            return await context.CurrencyType.OrderBy(c => c.Name).ToListAsync();
        }))!;
    }

    private async Task<List<string>> GetOrCreateExpirationsAsync()
    {
        return (await cache.GetOrCreateAsync("Expirations", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
            List<string> rawExpirations = await context.DeribitData
                .Select(o => o.Expiration)
                .Distinct()
                .ToListAsync();

            return rawExpirations
                .OrderBy(e => DateTime.ParseExact(e, "dMMMyy", CultureInfo.InvariantCulture))
                .ToList();
        }))!;
    }

    private async Task<List<DateTimeOffset>> GetOrCreateAvailableDatesAsync()
    {
        return (await cache.GetOrCreateAsync("AvailableDates", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15);
            return await context.DeribitData
                .Select(o => o.CreatedAt)
                .Distinct()
                .OrderBy(d => d)
                .ToListAsync();
        }))!;
    }
    
    protected async Task<IActionResult> LoadFilteredDataAsync()
    {
        if (!ModelState.IsValid)
            return Page();

        ViewModel.Currencies = cache.Get<List<CurrencyType>>("CurrencyTypes") ?? [];
        ViewModel.Expirations = cache.Get<List<string>>("Expirations") ?? [];
        ViewModel.AvailableDates = cache.Get<List<DateTimeOffset>>("AvailableDates") ?? [];

       // Create a cache key based on the selected parameters
        string cacheKey = $"DataValidation_{ViewModel.SelectedExpiration}_{ViewModel.SelectedDateTime}";
        string dataCacheKey = $"FilteredData_{ViewModel.SelectedCurrencyId}_{ViewModel.SelectedExpiration}_{ViewModel.SelectedDateTime}";

        bool isValidTime = await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(3);
            return await context.DeribitData
                .AnyAsync(d => d.Expiration == ViewModel.SelectedExpiration &&
                               d.CreatedAt == ViewModel.SelectedDateTime);
        });

        if (!isValidTime)
        {
            logger.LogWarning("Selected is not valid expiration time. CacheKey for validation {CacheKey}", cacheKey);
            
            ModelState.AddModelError("ViewModel.SelectedDateTime",
                "Please select a valid date/time from the available options");
            return Page();
        }

        // Get all data matching the filter criteria
        List<DeribitData> filteredData = (await cache.GetOrCreateAsync(dataCacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
            return await context.DeribitData
                .Where(d => d.CurrencyTypeId == ViewModel.SelectedCurrencyId &&
                            d.Expiration == ViewModel.SelectedExpiration &&
                            d.CreatedAt == ViewModel.SelectedDateTime)
                .ToListAsync();
        }))!;


        // Group-by-strike and option type to combine data for each strike
        Dictionary<int, DeribitData> callOptions = filteredData.Where(d => d.OptionTypeId == 1) // CALL
            .ToDictionary(d => d.Strike);

        Dictionary<int, DeribitData> putOptions = filteredData.Where(d => d.OptionTypeId == 2) // PUT
            .ToDictionary(d => d.Strike);

        // Get all unique strikes
        HashSet<int> allStrikes = filteredData.Select(d => d.Strike).ToHashSet();
        
        ViewModel.UnderlyingPrice = filteredData.Max(x => x.UnderlyingPrice);

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