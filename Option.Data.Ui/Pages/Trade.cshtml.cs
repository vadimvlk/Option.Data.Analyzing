using System.Globalization;
using Option.Data.Database;
using Option.Data.Ui.Models;
using Option.Data.Ui.Services;
using Option.Data.Shared.Poco;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Option.Data.Ui.Pages;

public class TradeModel(
    ApplicationDbContext context,
    IMemoryCache cache,
    ILogger<TradeModel> logger,
    IExpirationAnalysisBuilder expirationBuilder,
    ISessionRecommendationBuilder sessionBuilder) : BaseOptionPageModel(context, cache, logger)
{
    [BindProperty]
    public SessionRecommendation? Recommendation { get; set; }

    private readonly ApplicationDbContext _context = context;
    private readonly IMemoryCache _cache = cache;
    private readonly ILogger<TradeModel> _logger = logger;

    public async Task OnGetAsync()
    {
        await LoadCommonDataAsync();

        // Валюта по умолчанию — BTC (Id=1).
        if (ViewModel.SelectedCurrencyId == 0)
            ViewModel.SelectedCurrencyId = 1;

        await Compute();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadCommonDataAsync();

        if (!ModelState.IsValid)
            return Page();

        await Compute();
        return Page();
    }

    private async Task Compute()
    {
        try
        {
            string dataCacheKey = $"TradeData_{ViewModel.SelectedCurrencyId}_{ViewModel.SelectedDateTime}";

            // Грузим строки последнего снимка по (валюта, дата).
            List<DeribitData> rows = (await _cache.GetOrCreateAsync(dataCacheKey, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15);
                return await _context.DeribitData
                    .Where(d => d.CurrencyTypeId == ViewModel.SelectedCurrencyId &&
                                d.CreatedAt == ViewModel.SelectedDateTime)
                    .ToListAsync();
            }))!;

            if (rows.Count == 0)
            {
                Recommendation = null;
                return;
            }

            // Экспирации берём из самого снимка (distinct), сортировка по дате dMMMyy.
            List<string> expirations = rows
                .Select(d => d.Expiration)
                .Distinct()
                .OrderBy(e => DateTime.ParseExact(e, "dMMMyy", CultureInfo.InvariantCulture))
                .ToList();

            List<ExpirationAnalysis> analyses =
                expirationBuilder.Build(rows, expirations, ViewModel.SelectedDateTime);

            if (analyses.Count == 0)
            {
                Recommendation = null;
                return;
            }

            string currency = ViewModel.SelectedCurrencyId == 1 ? "BTC" : "ETH";
            Recommendation = sessionBuilder.Build(analyses, currency, ViewModel.SelectedDateTime);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error building trade plan");
            ModelState.AddModelError(string.Empty, $"Error loading data: {e.Message}");
            Recommendation = null;
        }
    }
}
