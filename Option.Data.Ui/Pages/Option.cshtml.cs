using Option.Data.Database;
using Option.Data.Ui.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;

namespace Option.Data.Ui.Pages;

public class OptionModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly IMemoryCache _cache;

    [BindProperty]
    public OptionViewModel ViewModel { get; set; } = new();

    public OptionModel(ApplicationDbContext context,
        IMemoryCache cache)
    {
        _context = context;
        _cache = cache;
    }

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
            ViewModel.Currencies = new List<Option.Data.Shared.Poco.CurrencyType>();
            ViewModel.Expirations = new List<string>();
            ViewModel.AvailableDates = new List<DateTimeOffset>();
        }
    }

    public async Task<IActionResult> OnPostLoadDataAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        // Create a cache key based on the selected parameters
        var cacheKey = $"DataValidation_{ViewModel.SelectedExpiration}_{ViewModel.SelectedDateTime}";
        
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

        // Здесь будет обработка загрузки данных
        // Можно добавить логику для фильтрации данных

        return Page();
    }
}