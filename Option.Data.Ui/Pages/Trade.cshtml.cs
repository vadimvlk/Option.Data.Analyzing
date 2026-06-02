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
            // Всегда последний снимок — выбор среза из UI убран (в прошлое не откатываемся).
            ViewModel.SelectedDateTime = ViewModel.AvailableDates.LastOrDefault();
            if (ViewModel.SelectedDateTime == default)
            {
                Recommendation = null;
                return;
            }

            DateTimeOffset asOf = ViewModel.SelectedDateTime;
            string dataCacheKey = $"TradeData_{ViewModel.SelectedCurrencyId}_{asOf}";

            // Строки последнего снимка по валюте (все экспирации).
            List<DeribitData> rows = (await _cache.GetOrCreateAsync(dataCacheKey, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15);
                return await _context.DeribitData
                    .Where(d => d.CurrencyTypeId == ViewModel.SelectedCurrencyId &&
                                d.CreatedAt == asOf)
                    .ToListAsync();
            }))!;

            if (rows.Count == 0)
            {
                Recommendation = null;
                return;
            }

            // Неистёкшие экспирации снимка с открытым интересом, по дате ↑.
            List<string> expirations = rows
                .GroupBy(d => d.Expiration)
                .Where(g => g.Sum(d => d.OpenInterest) > 0 &&
                            OptionExposureMath.YearsToExpiry(g.Key, asOf) > 0)
                .Select(g => g.Key)
                .OrderBy(e => DateTime.ParseExact(e, "dMMMyy", CultureInfo.InvariantCulture))
                .ToList();

            ViewModel.Expirations = expirations;

            if (expirations.Count == 0)
            {
                Recommendation = null;
                return;
            }

            // Дефолт/валидация: ближайшая неистёкшая, если выбор пуст или вне списка.
            if (string.IsNullOrEmpty(ViewModel.SelectedExpiration) ||
                !expirations.Contains(ViewModel.SelectedExpiration))
            {
                ViewModel.SelectedExpiration = expirations[0];
            }

            // Аналитика по одной выбранной экспирации.
            ExpirationAnalysis? selected = expirationBuilder
                .Build(rows, [ViewModel.SelectedExpiration], asOf)
                .FirstOrDefault();

            if (selected is null || selected.OptionData.Count == 0)
            {
                Recommendation = null;
                return;
            }

            string currency = ViewModel.SelectedCurrencyId == 1 ? "BTC" : "ETH";
            Recommendation = sessionBuilder.Build(selected, currency, asOf);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error building trade plan");
            ModelState.AddModelError(string.Empty, $"Error loading data: {e.Message}");
            Recommendation = null;
        }
    }
}
