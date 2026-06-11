using System.Globalization;
using Option.Data.Database;
using Option.Data.Ui.Models;
using Option.Data.Ui.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Option.Data.Ui.Pages;

/// <summary>
/// /Sell — рекомендация непокрытой продажи опционов: живая доска Deribit (bid-цены) +
/// контекст из БД (EWMA-RV фронта и DEX-ряд выбранной экспирации). Дефолт — ближайшая
/// активная экспирация с DTE ≥ 0.5 дня (короче — пин-риск, авто-сдвиг с пометкой).
/// </summary>
public class SellModel(
    ApplicationDbContext context,
    IMemoryCache cache,
    ILogger<SellModel> logger,
    IOptionBoardSourceResolver sources,
    ISellRecommendationBuilder sellBuilder)
    : BaseOptionPageModel(context, cache, logger)
{
    private readonly ApplicationDbContext _context = context;
    private readonly IMemoryCache _cache = cache;
    private readonly ILogger<SellModel> _logger = logger;
    private readonly IOptionBoardSourceResolver _sources = sources;

    public SellRecommendation? Recommendation { get; set; }

    /// <summary>Пометка авто-сдвига дефолтной экспирации из-за пин-риска (DTE &lt; 0.5 дня).</summary>
    public string? ExpirationShiftNote { get; set; }

    public async Task OnGetAsync()
    {
        await LoadCommonDataAsync(useAvailableDates: false);
        if (ViewModel.SelectedCurrencyId == 0)
            ViewModel.SelectedCurrencyId = 1;
        await Compute();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadCommonDataAsync(useAvailableDates: false);
        if (!ModelState.IsValid)
            return Page();
        await Compute();
        return Page();
    }

    /// <summary>Экспирации — живьём с Deribit (как Snapshot), не из БД.</summary>
    protected override async Task<List<string>> GetOrCreateExpirationsAsync()
    {
        string currency = ViewModel.Currencies.FirstOrDefault(c => c.Id == ViewModel.SelectedCurrencyId)?.Name ?? "BTC";
        return await _sources.Get(ExchangeSource.Deribit).GetExpirationsAsync(currency);
    }

    private async Task Compute()
    {
        try
        {
            string currency = ViewModel.Currencies.FirstOrDefault(c => c.Id == ViewModel.SelectedCurrencyId)?.Name ?? "BTC";
            DateTimeOffset asOf = DateTimeOffset.UtcNow;

            List<string> active = ViewModel.Expirations
                .Where(e => OptionExposureMath.YearsToExpiry(e, asOf) > 0)
                .OrderBy(e => DateTime.ParseExact(e, "dMMMyy", CultureInfo.InvariantCulture))
                .ToList();
            ViewModel.Expirations = active;
            if (active.Count == 0)
            {
                ModelState.AddModelError(string.Empty, "Нет активных экспираций.");
                return;
            }

            if (string.IsNullOrEmpty(ViewModel.SelectedExpiration) || !active.Contains(ViewModel.SelectedExpiration))
            {
                // Дефолт: ближайшая с DTE ≥ 0.5 дня; совсем короткий фронт пропускаем с пометкой.
                string? preferred = active.FirstOrDefault(e => OptionExposureMath.YearsToExpiry(e, asOf) * 365.0 >= 0.5);
                if (preferred is not null && preferred != active[0])
                    ExpirationShiftNote = $"Фронт {active[0]} истекает менее чем через 12 ч (пин-риск) — выбрана {preferred}.";
                ViewModel.SelectedExpiration = preferred ?? active[0];
            }

            OptionBoard board = await _sources.Get(ExchangeSource.Deribit)
                .GetBoardAsync(currency, ViewModel.SelectedExpiration);
            if (board.Chain.Count == 0)
            {
                ModelState.AddModelError(string.Empty,
                    $"Deribit не вернул данных по {currency}/{ViewModel.SelectedExpiration}.");
                return;
            }
            ViewModel.OptionData = board.Chain;
            ViewModel.UnderlyingPrice = board.Spot;

            (List<PricePoint> priceHistory, List<DeltaPoint> dexSeries) =
                await LoadHistoryAsync(ViewModel.SelectedCurrencyId, ViewModel.SelectedExpiration);

            Recommendation = sellBuilder.Build(
                board, currency, ViewModel.SelectedExpiration, asOf, dexSeries, priceHistory);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error building sell recommendation");
            ModelState.AddModelError(string.Empty, $"Error loading data: {e.Message}");
            Recommendation = null;
        }
    }

    /// <summary>
    /// История из БД за 30 дней одной выборкой: 3ч-ряд цены фронта (для EWMA-RV) +
    /// DEX-ряд выбранной экспирации (для потока ΔDEX). При недоступной БД — пустые ряды
    /// (билдер уйдёт в фолбэки и пометит это в Notes).
    /// </summary>
    private async Task<(List<PricePoint> Price, List<DeltaPoint> Dex)> LoadHistoryAsync(
        int currencyId, string expiration)
    {
        try
        {
            DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(-30);
            string key = $"SellHist_{currencyId}";
            var rows = (await _cache.GetOrCreateAsync(key, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15);
                return await _context.DeribitData
                    .Where(d => d.CurrencyTypeId == currencyId && d.CreatedAt >= cutoff)
                    .GroupBy(d => new { d.CreatedAt, d.Expiration })
                    .Select(g => new
                    {
                        g.Key.CreatedAt,
                        g.Key.Expiration,
                        Px = g.Max(x => x.UnderlyingPrice),
                        Dex = -g.Sum(x => x.Delta * x.OpenInterest)
                    })
                    .ToListAsync();
            }))!;

            // Цена: на каждом снимке — форвард БЛИЖАЙШЕЙ ещё живой экспирации.
            List<PricePoint> price = rows
                .GroupBy(r => r.CreatedAt)
                .Select(g =>
                {
                    var front = g
                        .Where(r => OptionExposureMath.YearsToExpiry(r.Expiration, g.Key) > 0)
                        .OrderBy(r => OptionExposureMath.YearsToExpiry(r.Expiration, g.Key))
                        .FirstOrDefault();
                    return front is null ? default : new PricePoint(g.Key, front.Px);
                })
                .Where(p => p.Price > 0)
                .OrderBy(p => p.Time)
                .ToList();

            List<DeltaPoint> dex = rows
                .Where(r => r.Expiration == expiration)
                .Select(r => new DeltaPoint { Time = r.CreatedAt, UnderlyingPrice = r.Px, DeltaExposure = r.Dex })
                .OrderBy(p => p.Time)
                .ToList();

            return (price, dex);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Sell: history unavailable, falling back to IV-only sigma");
            return (new List<PricePoint>(), new List<DeltaPoint>());
        }
    }
}
