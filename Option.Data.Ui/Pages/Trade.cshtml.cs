using System.Globalization;
using Option.Data.Database;
using Option.Data.Ui.Models;
using Option.Data.Ui.Services;
using Option.Data.Shared.Dto;
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
    ISessionRecommendationBuilder sessionBuilder,
    IContractMemoryBuilder memoryBuilder) : BaseOptionPageModel(context, cache, logger)
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

            // Память контракта — из тех же deltaRows (история окна/серии по срезам). curOi — ΣOI серии/окна
            // на последнем срезе; maxOi — макс. ΣOI среди ВСЕХ серий снимка (репрезентативность OI).
            List<MemorySnapshot> memHistory = BuildMemoryHistory(deltaRows, asOf);
            double curOi = memHistory.Count > 0 ? memHistory[^1].Chain.Sum(o => o.CallOi + o.PutOi) : 0;
            double maxOi = rows.GroupBy(r => r.Expiration).Select(g => g.Sum(r => r.OpenInterest)).DefaultIfEmpty(0).Max();
            ContractMemory memory = memoryBuilder.Build(memHistory, curOi, maxOi);

            if (isAggregate)
            {
                // Агрегированная сводка: окно ближних + квартальной.
                List<ExpirationAnalysis> analyses = expirationBuilder.Build(rows, aggWindow, asOf);

                if (analyses.Count == 0)
                {
                    Recommendation = null;
                    return;
                }

                Recommendation = sessionBuilder.BuildAggregate(analyses, currency, asOf, dexFlowTrend, memory);
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

                Recommendation = sessionBuilder.Build(selected, currency, asOf, dexFlowTrend, memory);
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

    /// <summary>
    /// Преобразует исторические строки экспирации/окна в срезы <see cref="MemorySnapshot"/>
    /// (по возрастанию времени, ≤ asOf): GammaStrike по (экспирация, страйк) с собственным T/IV,
    /// сводная цепочка ΣOI по страйку. Соглашения — как в BuildGammaStrikes/ConvertToOptionData.
    /// </summary>
    private static List<MemorySnapshot> BuildMemoryHistory(List<DeribitData> rows, DateTimeOffset asOf)
    {
        var snapshots = new List<MemorySnapshot>();

        foreach (IGrouping<DateTimeOffset, DeribitData> snap in rows
                     .Where(r => r.CreatedAt <= asOf)
                     .GroupBy(r => r.CreatedAt)
                     .OrderBy(g => g.Key))
        {
            double spot = snap.Max(r => r.UnderlyingPrice);
            if (spot <= 0 || !double.IsFinite(spot))
                continue;

            // GammaStrike: по каждой (экспирация, страйк) — свой T (от среза) и IV.
            var gammaStrikes = new List<SessionAnalysisMath.GammaStrike>();
            foreach (IGrouping<string, DeribitData> expG in snap.GroupBy(r => r.Expiration))
            {
                double t = OptionExposureMath.YearsToExpiry(expG.Key, snap.Key);
                if (t <= 0)
                    continue;

                foreach (IGrouping<int, DeribitData> kG in expG.GroupBy(r => r.Strike))
                {
                    double callOi = 0, putOi = 0, callIv = 0, putIv = 0;
                    foreach (DeribitData r in kG)
                    {
                        if (r.OptionTypeId == 1) { callOi += r.OpenInterest; if (r.Iv > 0) callIv = r.Iv; }
                        else { putOi += r.OpenInterest; if (r.Iv > 0) putIv = r.Iv; }
                    }
                    if (callOi <= 0 && putOi <= 0)
                        continue;

                    double sigma = (callIv > 0 ? callIv : putIv) / 100.0;
                    gammaStrikes.Add(new SessionAnalysisMath.GammaStrike(kG.Key, callOi, putOi, sigma, t));
                }
            }

            // Сводная цепочка: ΣOI по страйку через все серии окна (для OI/стен).
            List<OptionData> chain = snap
                .GroupBy(r => r.Strike)
                .Select(kG => new OptionData
                {
                    Strike = kG.Key,
                    CallOi = kG.Where(r => r.OptionTypeId == 1).Sum(r => r.OpenInterest),
                    PutOi = kG.Where(r => r.OptionTypeId == 2).Sum(r => r.OpenInterest)
                })
                .OrderBy(o => o.Strike)
                .ToList();

            snapshots.Add(new MemorySnapshot(snap.Key, spot, gammaStrikes, chain));
        }

        return snapshots;
    }
}
