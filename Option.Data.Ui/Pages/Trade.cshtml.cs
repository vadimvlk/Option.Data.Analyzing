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

    /// <summary>Исторический ряд дельта-экспозиции выбранной экспирации (для графика Delta Exposure).</summary>
    public List<DeltaPoint> DeltaSeries { get; set; } = new();

    /// <summary>Значение опции «Сводка» в селекте экспираций.</summary>
    public const string AggregateKey = "__AGG__";

    /// <summary>Подпись опции «Сводка» (с кодом квартальной).</summary>
    public string AggregateLabel { get; set; } = "Сводка";

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

            // Окно агрегации (ближние + квартальная) и подпись «Сводки» (по самой дальней в окне).
            List<string> aggWindow = QuarterlyAggregation.WindowExpirations(expirations, asOf);
            AggregateLabel = aggWindow.Count > 0 ? $"Сводка (до {aggWindow[^1]})" : "Сводка";

            // Дефолт — «Сводка» (агрегат до квартальной): самый богатый набор данных,
            // на нём же калибровались сигналы. Невалидный выбор тоже сводим к ней.
            if (string.IsNullOrEmpty(ViewModel.SelectedExpiration) ||
                (ViewModel.SelectedExpiration != AggregateKey &&
                 !expirations.Contains(ViewModel.SelectedExpiration)))
            {
                ViewModel.SelectedExpiration = AggregateKey;
            }

            string currency = ViewModel.SelectedCurrencyId == 1 ? "BTC" : "ETH";

            // Экспирации дельта-ряда определяются ДО построения рекомендации:
            // тренд потока DEX по истории снимков — один из сигналов направления.
            bool isAggregate = ViewModel.SelectedExpiration == AggregateKey;
            List<string> deltaExpirations = isAggregate ? aggWindow : [ViewModel.SelectedExpiration];

            // Исторический ряд дельта-экспозиции: −Σ(Δ·OI) по каждому снимку, по экспирациям
            // окна/выбора. Отдельный запрос — нужны ВСЕ снимки, а не только последний.
            string deltaCacheKey = $"TradeDelta_{ViewModel.SelectedCurrencyId}_{string.Join(",", deltaExpirations)}";
            List<DeribitData> deltaRows = (await _cache.GetOrCreateAsync(deltaCacheKey, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15);
                return await _context.DeribitData
                    .Where(d => d.CurrencyTypeId == ViewModel.SelectedCurrencyId &&
                                deltaExpirations.Contains(d.Expiration))
                    .ToListAsync();
            }))!;

            List<DeltaPoint> deltaSeries = deltaRows
                .GroupBy(d => d.CreatedAt)
                .Select(g => new DeltaPoint
                {
                    Time = g.Key,
                    UnderlyingPrice = g.Max(d => d.UnderlyingPrice),
                    DeltaExposure = -g.Sum(d => d.Delta * d.OpenInterest)
                })
                .OrderBy(p => p.Time)
                .ToList();

            // Тренд потока DEX (−1…+1) по последним снимкам — передаётся билдеру как сигнал.
            double dexFlowTrend = SessionAnalysisMath.DexTrend(deltaSeries);

            if (isAggregate)
            {
                // Агрегированная сводка: окно ближних + квартальной.
                List<ExpirationAnalysis> analyses = expirationBuilder.Build(rows, aggWindow, asOf);

                if (analyses.Count == 0)
                {
                    Recommendation = null;
                    return;
                }

                Recommendation = sessionBuilder.BuildAggregate(analyses, currency, asOf, dexFlowTrend);
            }
            else
            {
                // Одиночная выбранная экспирация.
                ExpirationAnalysis? selected = expirationBuilder
                    .Build(rows, [ViewModel.SelectedExpiration], asOf)
                    .FirstOrDefault();

                if (selected is null || selected.OptionData.Count == 0)
                {
                    Recommendation = null;
                    return;
                }

                Recommendation = sessionBuilder.Build(selected, currency, asOf, dexFlowTrend);
            }

            // Дельта-ряд показываем только вместе с рекомендацией: canvas графика
            // рендерится внутри блока @if (Recommendation != null), и заполненный ряд
            // без рекомендации привёл бы к JS-ошибке на отсутствующем элементе.
            if (Recommendation != null)
                DeltaSeries = deltaSeries;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error building trade plan");
            ModelState.AddModelError(string.Empty, $"Error loading data: {e.Message}");
            Recommendation = null;
        }
    }
}
