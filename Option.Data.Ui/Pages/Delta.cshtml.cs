using Option.Data.Database;
using Option.Data.Ui.Models;
using Option.Data.Ui.Services;
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
            // Кэшируем сырые строки экспирации: из них строим и дельта-ряд по времени,
            // и гамму по последнему снимку (один источник — без второго запроса к БД).
            string cacheKey = $"DeltaRows_{ViewModel.SelectedCurrencyId}_{ViewModel.SelectedExpiration}";

            List<DeribitData> rows = (await _cache.GetOrCreateAsync(cacheKey, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15);

                return await _context.DeribitData
                    .Where(d => d.CurrencyTypeId == ViewModel.SelectedCurrencyId &&
                                d.Expiration == ViewModel.SelectedExpiration)
                    .ToListAsync();
            }))!;

            if (rows.Count == 0)
                return Page();

            // Дельта-экспозиция по времени (как раньше).
            DeltaViewModel.Series = rows
                .GroupBy(d => d.CreatedAt)
                .Select(g => new DeltaPoint
                {
                    Time = g.Key,
                    UnderlyingPrice = g.Max(d => d.UnderlyingPrice),
                    DeltaExposure = -g.Sum(d => d.Delta * d.OpenInterest)
                })
                .OrderBy(p => p.Time)
                .ToList();

            // Гамма: профиль Net GEX по цене и разбивка по страйкам для последнего снимка.
            BuildGamma(rows);

            return Page();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error loading delta series data");
            ModelState.AddModelError(string.Empty, $"Error loading data: {e.Message}");
            return Page();
        }
    }

    /// <summary>
    /// Считает профиль Net GEX по цене (Black-76), gamma-flip и вклад каждого страйка
    /// для ПОСЛЕДНЕГО снимка выбранной экспирации. Все величины согласованы между собой
    /// (один источник — <see cref="SessionAnalysisMath"/>).
    /// </summary>
    private void BuildGamma(List<DeribitData> rows)
    {
        DateTimeOffset latest = rows.Max(r => r.CreatedAt);
        List<DeribitData> latestRows = rows.Where(r => r.CreatedAt == latest).ToList();
        if (latestRows.Count == 0)
            return;

        double spot = latestRows.Max(r => r.UnderlyingPrice);
        if (spot <= 0 || !double.IsFinite(spot))
            return;

        double tYears = OptionExposureMath.YearsToExpiry(ViewModel.SelectedExpiration, latest);

        // Цепочка по страйку: Call = OptionTypeId 1, Put = 2.
        List<SessionAnalysisMath.GammaStrike> strikes = latestRows
            .GroupBy(r => r.Strike)
            .Select(g =>
            {
                DeribitData? call = g.FirstOrDefault(r => r.OptionTypeId == 1);
                DeribitData? put = g.FirstOrDefault(r => r.OptionTypeId == 2);
                double iv = call?.Iv ?? put?.Iv ?? 0;
                return new SessionAnalysisMath.GammaStrike(
                    Strike: g.Key,
                    CallOi: call?.OpenInterest ?? 0,
                    PutOi: put?.OpenInterest ?? 0,
                    SigmaFraction: iv / 100.0,
                    TYears: tYears);
            })
            .Where(s => s.CallOi > 0 || s.PutOi > 0)
            .OrderBy(s => s.Strike)
            .ToList();

        if (strikes.Count == 0)
            return;

        DeltaViewModel.Spot = spot;
        DeltaViewModel.GammaProfile = SessionAnalysisMath.GammaProfile(strikes, spot);
        DeltaViewModel.GammaFlip = SessionAnalysisMath.GammaFlip(DeltaViewModel.GammaProfile, spot);
        DeltaViewModel.NetGexAtSpot = SessionAnalysisMath.NetGexAtPrice(strikes, spot);

        double scale = spot * spot * 0.01;
        DeltaViewModel.StrikeGex = strikes
            .Select(s => new StrikeGex
            {
                Strike = s.Strike,
                NetGex = SessionAnalysisMath.Black76Gamma(spot, s.Strike, s.SigmaFraction, s.TYears)
                         * (s.CallOi - s.PutOi) * scale
            })
            .Where(x => double.IsFinite(x.NetGex) && x.NetGex != 0)
            .ToList();
    }
}
