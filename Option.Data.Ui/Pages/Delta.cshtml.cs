using Option.Data.Database;
using Option.Data.Ui.Models;
using Option.Data.Shared.Poco;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;

namespace Option.Data.Ui.Pages;

public class DeltaModel(
    ApplicationDbContext context,
    IMemoryCache cache,
    ILogger<DeltaModel> logger) : BaseOptionPageModel(context, cache, logger)
{
    [BindProperty]
    public DeltaViewModel DeltaViewModel { get; set; } = new();

    private readonly ApplicationDbContext _context = context;
    private readonly IMemoryCache _cache = cache;

    public async Task OnGetAsync() => await LoadCommonDataAsync(useAvailableDates: false);

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadCommonDataAsync(useAvailableDates: false);

        if (!ModelState.IsValid)
            return Page();

        try
        {
            string cacheKey = $"DeltaSeries_{ViewModel.SelectedCurrencyId}_{ViewModel.SelectedExpiration}";

            DeltaViewModel.Series = (await _cache.GetOrCreateAsync(cacheKey, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15);

                List<DeribitData> rows = await _context.DeribitData
                    .Where(d => d.CurrencyTypeId == ViewModel.SelectedCurrencyId &&
                                d.Expiration == ViewModel.SelectedExpiration)
                    .ToListAsync();

                return rows
                    .GroupBy(d => d.CreatedAt)
                    .Select(g => new DeltaPoint
                    {
                        Time = g.Key,
                        UnderlyingPrice = g.Max(d => d.UnderlyingPrice),
                        DeltaExposure = -g.Sum(d => d.Delta * d.OpenInterest)
                    })
                    .OrderBy(p => p.Time)
                    .ToList();
            }))!;

            return Page();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error loading delta series data");
            ModelState.AddModelError(string.Empty, $"Error loading data: {e.Message}");
            return Page();
        }
    }
}
