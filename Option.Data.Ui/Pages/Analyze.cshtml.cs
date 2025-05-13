using Option.Data.Database;
using Option.Data.Ui.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;

namespace Option.Data.Ui.Pages;

public class Analyze : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly IMemoryCache _cache;
    private readonly ILogger<Analyze> _logger;

    public Analyze(ApplicationDbContext context, IMemoryCache cache, ILogger<Analyze> logger)
    {
        _context = context;
        _cache = cache;
        _logger = logger;
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
}