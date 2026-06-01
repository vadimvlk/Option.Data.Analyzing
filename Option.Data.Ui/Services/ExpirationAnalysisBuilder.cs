using Option.Data.Ui.Models;
using Option.Data.Shared.Dto;
using Option.Data.Shared.Poco;

namespace Option.Data.Ui.Services;

/// <summary>
/// Строит per-expiration аналитику (<see cref="ExpirationAnalysis"/>) из строк снимка БД.
/// Логика вынесена из <c>ExposureModel</c> без изменения формул; общий код для страниц
/// Exposure и Trade. Конвертация типа опциона — по <see cref="DeribitData.OptionTypeId"/>
/// (1=Call, 2=Put), чтобы не зависеть от EF <c>Include(Type)</c>.
/// </summary>
public class ExpirationAnalysisBuilder : IExpirationAnalysisBuilder
{
    public List<ExpirationAnalysis> Build(
        IReadOnlyList<DeribitData> snapshotRows,
        IReadOnlyList<string> expirations,
        DateTimeOffset asOf)
    {
        var result = new List<ExpirationAnalysis>();

        // Группируем данные по экспирации для более эффективного доступа
        Dictionary<string, List<DeribitData>> dataByExpiration = snapshotRows
            .GroupBy(d => d.Expiration)
            .ToDictionary(g => g.Key, g => g.ToList());

        // For each expiration, calculate analysis data
        foreach (string expiration in expirations)
        {
            // Проверяем, есть ли данные для этой экспирации
            if (!dataByExpiration.TryGetValue(expiration, out List<DeribitData>? expirationOptions)) continue;

            // Преобразуем данные в формат OptionData
            List<OptionData> optionData = ConvertToOptionData(expirationOptions);

            if (optionData.Count == 0) continue;

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

            // OI-взвешенный центр тяжести всей цепочки (call+put вместе) — корректный «магнит»
            // с учётом дисбаланса OI. При нулевом суммарном OI → 0 (sentinel «недоступно»),
            // downstream (SessionRecommendationBuilder) такие значения пропускает.
            double totalOi = optionData.Sum(o => o.CallOi + o.PutOi);
            double oiCentroid = totalOi > 0
                ? optionData.Sum(o => o.Strike * (o.CallOi + o.PutOi)) / totalOi
                : 0;
            if (!double.IsFinite(oiCentroid)) oiCentroid = 0;

            // Calculate break-even points (upper and lower boundaries)
            var boundaries = CalculateBreakEvenPoints(optionData, underlyingPrice);

            // Risk metrics (DEX / Net GEX / Max Pain / Expected Move / Skew).
            // T меряется от момента снимка (asOf), а не от UtcNow, чтобы
            // исторические снимки давали корректное время до экспирации.
            double atmIv = OptionExposureMath.AtmIvFraction(optionData, underlyingPrice);
            double yearsToExpiry = OptionExposureMath.YearsToExpiry(expiration, asOf);

            result.Add(new ExpirationAnalysis
            {
                UnderlyingPrice = underlyingPrice,
                Expiration = expiration,
                OptionData = optionData,
                CallCenterOfGravity = callCog,
                PutCenterOfGravity = putCog,
                GravityEquilibrium = gravityEquilibrium,
                OiCentroid = oiCentroid,
                UpperBoundary = boundaries.upperBoundary,
                LowerBoundary = boundaries.lowerBoundary,
                DollarDeltaExposure = OptionExposureMath.DollarDeltaExposure(optionData, underlyingPrice),
                NetGammaExposure = OptionExposureMath.NetGammaExposure(optionData, underlyingPrice),
                MaxPain = OptionExposureMath.MaxPain(optionData),
                ExpectedMove1Sigma = OptionExposureMath.ExpectedMove1Sigma(underlyingPrice, atmIv, yearsToExpiry),
                RiskReversal25Delta = OptionExposureMath.RiskReversal25Delta(optionData)
            });
        }

        return result;
    }

    private static List<OptionData> ConvertToOptionData(List<DeribitData> data)
    {
        if (data.Count == 0)
        {
            return [];
        }

        // Group by strike
        List<int> strikes = data.Select(d => d.Strike).Distinct().OrderBy(s => s).ToList();

        // Конвертация типа по OptionTypeId (1=Call, 2=Put), а не по Type.Name —
        // чтобы не зависеть от EF Include(Type).
        return (from strike in strikes
                let callData = data.FirstOrDefault(d => d.Strike == strike && d.OptionTypeId == 1)
                let putData = data.FirstOrDefault(d => d.Strike == strike && d.OptionTypeId == 2)
                select new OptionData
                {
                    Strike = strike,
                    CallOi = callData?.OpenInterest ?? 0,
                    CallPrice = callData?.MarkPrice * callData?.UnderlyingPrice ?? 0,
                    CallDelta = callData?.Delta ?? 0,
                    CallGamma = callData?.Gamma ?? 0,
                    Iv = callData?.Iv ?? (putData?.Iv ?? 0),
                    PutIv = putData?.Iv ?? 0,
                    PutOi = putData?.OpenInterest ?? 0,
                    PutPrice = putData?.MarkPrice * putData?.UnderlyingPrice ?? 0,
                    PutDelta = putData?.Delta ?? 0,
                    PutGamma = putData?.Gamma ?? 0
                })
            .ToList();
    }

    private static (double? upperBoundary, double? lowerBoundary) CalculateBreakEvenPoints(List<OptionData> data, double currentPrice)
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

        // Защита от зависания: при битом снимке (UnderlyingPrice==0 ⇒ step==0) цикл ниже
        // не продвигался бы бесконечно. Деградируем к «границы недоступны».
        if (currentPrice <= 0 || step <= 0)
            return (null, null);

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
    private static double CalculateSellerCallPnL(List<OptionData> data, double price)
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
    private static double CalculateSellerPutPnL(List<OptionData> data, double price)
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
}
