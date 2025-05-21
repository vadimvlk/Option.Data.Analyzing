using Option.Data.Database;
using Option.Data.Ui.Models;
using Option.Data.Shared.Dto;
using Option.Data.Shared.Poco;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Option.Data.Shared.Configuration;
using Microsoft.Extensions.Caching.Memory;

namespace Option.Data.Ui.Pages;

public class ExposureModel(
    ApplicationDbContext context,
    IMemoryCache cache,
    ILogger<ExposureModel> logger,
    IHttpClientFactory clientFactory) : BaseOptionPageModel(context, cache: cache, logger)
{
    [BindProperty]
    public ExposureViewModel ExposureViewModel { get; set; } = new();


    private readonly HttpClient _httpClient = clientFactory.CreateClient(DeribitConfig.ClientName);
    private readonly ApplicationDbContext _context = context;
    private readonly IMemoryCache _cache = cache;

    public async Task OnGetAsync() => await LoadCommonDataAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadCommonDataAsync();
        
        if (!ModelState.IsValid)
        {
            return Page();
        }
        
        try
        {
            // Load data for each expiration
            ExposureViewModel.ExpirationsData = [];
            
            string dataCacheKey = $"ExposureData_{ViewModel.SelectedCurrencyId}_{ViewModel.SelectedDateTime}";
            
            // Получаем все данные по опционам для выбранной даты и валюты.
            List<DeribitData> allOptionData = (await _cache.GetOrCreateAsync(dataCacheKey, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15);
                return await _context.DeribitData
                    .Where(d => d.CurrencyTypeId == ViewModel.SelectedCurrencyId &&
                                d.CreatedAt == ViewModel.SelectedDateTime)
                    .Include(deribitData => deribitData.Type)
                    .ToListAsync();
            }))!;

            // Группируем данные по экспирации для более эффективного доступа
            Dictionary<string, List<DeribitData>> dataByExpiration = allOptionData
                .GroupBy(d => d.Expiration)
                .ToDictionary(g => g.Key, g => g.ToList());

            // For each expiration, calculate analysis data
            foreach (string expiration in ViewModel.Expirations)
            {
                // Проверяем, есть ли данные для этой экспирации
                if (!dataByExpiration.TryGetValue(expiration, out List<DeribitData>? expirationOptions)) continue;
                // Преобразуем данные в формат OptionData
                
                List<OptionData> optionData = ConvertToOptionData(expirationOptions);

                if (optionData.Count != 0)
                {
                    // Находим цену базового актива для этой экспирации
                    double underlyingPrice = expirationOptions.Max(d => d.UnderlyingPrice);

                    // Calculate gravity equilibrium and centers of gravity
                    double callCog = optionData.Where(o => o.CallOi > 0)
                                               .Sum(o => o.Strike * o.CallOi) /
                                     optionData.Sum(o => o.CallOi);
                    if (double.IsNaN(callCog)) callCog = 0;

                    double putCog = optionData.Where(o => o.PutOi > 0)
                                              .Sum(o => o.Strike * o.PutOi) /
                                    optionData.Sum(o => o.PutOi);
                    if (double.IsNaN(putCog)) putCog = 0;

                    double gravityEquilibrium = (callCog + putCog) / 2;

                    // Calculate break-even points (upper and lower boundaries)
                    var boundaries = CalculateBreakEvenPoints(optionData, underlyingPrice);

                    ExposureViewModel.ExpirationsData.Add(new ExpirationAnalysis
                    {
                        UnderlyingPrice = underlyingPrice,
                        Expiration = expiration,
                        OptionData = optionData,
                        CallCenterOfGravity = callCog,
                        PutCenterOfGravity = putCog,
                        GravityEquilibrium = gravityEquilibrium,
                        UpperBoundary = boundaries.upperBoundary,
                        LowerBoundary = boundaries.lowerBoundary
                    });
                }
            }

            return Page();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error loading exposure analysis data");
            ModelState.AddModelError(string.Empty, $"Error loading data: {e.Message}");
            return Page();
        }
    }

    private static List<OptionData> ConvertToOptionData(List<DeribitData> data)
    {
        if (data.Count == 0)
        {
            return [];
        }

        // Group by strike
        List<int> strikes = data.Select(d => d.Strike).Distinct().OrderBy(s => s).ToList();

        return (from strike in strikes
                let callData = data.FirstOrDefault(d => d.Strike == strike && d.Type.Name == "Call")
                let putData = data.FirstOrDefault(d => d.Strike == strike && d.Type.Name == "Put")
                select new OptionData
                {
                    Strike = strike,
                    CallOi = callData?.OpenInterest ?? 0,
                    CallPrice = callData?.MarkPrice * callData?.UnderlyingPrice ?? 0,
                    CallDelta = callData?.Delta ?? 0,
                    CallGamma = callData?.Gamma ?? 0,
                    Iv = callData?.Iv ?? (putData?.Iv ?? 0),
                    PutOi = putData?.OpenInterest ?? 0,
                    PutPrice = putData?.MarkPrice * putData?.UnderlyingPrice ?? 0,
                    PutDelta = putData?.Delta ?? 0,
                    PutGamma = putData?.Gamma ?? 0
                })
            .ToList();
    }

    private (double? upperBoundary, double? lowerBoundary) CalculateBreakEvenPoints(List<OptionData> data, double currentPrice)
    {
        if (data.Count == 0)
            return (null, null);

        // Define price range for analysis
        double minStrike = data.Min(d => d.Strike);
        double maxStrike = data.Max(d => d.Strike);
        double rangeExtensionPercent = 0.3;
        double minPrice = minStrike * (1 - rangeExtensionPercent);
        double maxPrice = maxStrike * (1 + rangeExtensionPercent);
        double step = currentPrice * 0.001;

        // Calculate PnL at different price points
        List<(double Price, double TotalPnL)> pnlData = new();

        for (double price = minPrice; price <= maxPrice; price += step)
        {
            double callPnL = CalculateSellerCallPnL(data, price);
            double putPnL = CalculateSellerPutPnL(data, price);
            double totalPnL = callPnL + putPnL;

            pnlData.Add((price, totalPnL));
        }

        // Find break-even points (where PnL crosses zero)
        List<double> breakEvenPoints = new();

        for (int i = 0; i < pnlData.Count - 1; i++)
        {
            // If PnL changes sign between adjacent points, this is a break-even point
            if ((pnlData[i].TotalPnL >= 0 && pnlData[i + 1].TotalPnL < 0) ||
                (pnlData[i].TotalPnL < 0 && pnlData[i + 1].TotalPnL >= 0))
            {
                // Linear interpolation to find exact value
                double pnl1 = pnlData[i].TotalPnL;
                double pnl2 = pnlData[i + 1].TotalPnL;
                double price1 = pnlData[i].Price;
                double price2 = pnlData[i + 1].Price;

                // Linear interpolation formula: price = price1 + (0 - pnl1) * (price2 - price1) / (pnl2 - pnl1)
                double breakEvenPrice = price1 + (0 - pnl1) * (price2 - price1) / (pnl2 - pnl1);
                breakEvenPoints.Add(breakEvenPrice);
            }
        }

        // Sort break-even points
        breakEvenPoints.Sort();

        // Find closest break-even points above and below current price
        double? lowerBoundary = breakEvenPoints
            .Where(p => p < currentPrice)
            .DefaultIfEmpty(double.NaN)
            .Max();

        double? upperBoundary = breakEvenPoints
            .Where(p => p > currentPrice)
            .DefaultIfEmpty(double.NaN)
            .Min();

        if (double.IsNaN((double)lowerBoundary))
            lowerBoundary = null;

        if (double.IsNaN((double)upperBoundary))
            upperBoundary = null;

        return (upperBoundary, lowerBoundary);
    }

    // Method to calculate PnL for Call options for sellers
    private double CalculateSellerCallPnL(List<OptionData> data, double price)
    {
        double pnl = 0;
        foreach (OptionData option in data)
        {
            if (option.CallOi > 0)
            {
                // Sellers receive premium but pay the difference if price > strike
                double gain = Math.Max(0, price - option.Strike);
                pnl += (option.CallPrice - gain) * option.CallOi;
            }
        }
        return pnl;
    }

    // Method to calculate PnL for Put options for sellers
    private double CalculateSellerPutPnL(List<OptionData> data, double price)
    {
        double pnl = 0;
        foreach (OptionData option in data)
        {
            if (option.PutOi > 0)
            {
                // Sellers receive premium but pay the difference if price < strike
                double gain = Math.Max(0, option.Strike - price);
                pnl += (option.PutPrice - gain) * option.PutOi;
            }
        }
        return pnl;
    }

    protected override async Task<List<string>> GetOrCreateExpirationsAsync()
    {
        return (await _cache.GetOrCreateAsync("AvailableExpirations", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15);

            Expirations? expirations =
                await _httpClient.GetFromJsonAsync<Expirations>("get_expirations?currency=any&kind=option");

            if (expirations?.Data.Options != null && expirations.Data.Options.Count != 0)
                return expirations.Data.Options;

            logger.LogWarning("No data returned for expirations");
            return [];
        }))!;
    }
}
