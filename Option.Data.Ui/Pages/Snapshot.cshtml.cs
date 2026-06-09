using Option.Data.Database;
using Option.Data.Ui.Models;
using Option.Data.Ui.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace Option.Data.Ui.Pages;

public class SnapshotModel(
    ApplicationDbContext context,
    IMemoryCache cache,
    ILogger<SnapshotModel> logger,
    IOptionBoardSourceResolver sources)
    : BaseOptionPageModel(context, cache, logger)
{
    private readonly IOptionBoardSourceResolver _sources = sources;

    /// <summary>Net GEX (Black-76) по выбранной экспирации — профиль/flip/страйки (партиал _GammaProfile).</summary>
    public GammaView Gamma { get; set; } = new();

    public async Task OnGetAsync() => await LoadCommonDataAsync(useAvailableDates: false);

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadCommonDataAsync(useAvailableDates: false);

        if (!ModelState.IsValid)
            return Page();

        try
        {
            string currency = ViewModel.Currencies.Single(c => c.Id == ViewModel.SelectedCurrencyId).Name;
            IOptionBoardSource source = _sources.Get(ViewModel.SelectedExchange);

            OptionBoard board = await source.GetBoardAsync(currency, ViewModel.SelectedExpiration);

            ViewModel.OptionData = board.Chain;
            ViewModel.UnderlyingPrice = board.Spot;

            if (board.Chain.Count == 0)
            {
                logger.LogWarning("No option data for {Exchange}/{Currency}/{Expiration}",
                    ViewModel.SelectedExchange, currency, ViewModel.SelectedExpiration);
                return Page();
            }

            // Net GEX в рантайме по Black-76 (live: asOf = now); единый метод для обеих бирж.
            Gamma = NetGexCalculator.Build(
                board.Chain,
                board.Spot,
                OptionExposureMath.YearsToExpiry(ViewModel.SelectedExpiration, DateTimeOffset.UtcNow));

            return Page();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error loading option data");
            ModelState.AddModelError(string.Empty, $"Error loading option data: {e.Message}");
            return Page();
        }
    }

    /// <summary>Экспирации берём у выбранного источника (Deribit/Binance), а не из БД.</summary>
    protected override async Task<List<string>> GetOrCreateExpirationsAsync()
    {
        string currency = ViewModel.Currencies.FirstOrDefault(c => c.Id == ViewModel.SelectedCurrencyId)?.Name ?? "BTC";
        return await _sources.Get(ViewModel.SelectedExchange).GetExpirationsAsync(currency);
    }
}
