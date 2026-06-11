using Option.Data.Ui.Models;

namespace Option.Data.Ui.Services;

/// <summary>
/// Чистая математика непокрытой продажи опционов: Black-76 (r=0 — конвенция греков Deribit
/// на форварде, согласовано с <see cref="SessionAnalysisMath.Black76Gamma"/>), вероятности,
/// маржа Deribit, EWMA реализованной волатильности.
/// Соглашения: σ — ДОЛЯ (mark_iv/100); T — доля года (ACT/365); цены — USD, если в имени
/// не сказано Coin; вероятности — риск-нейтральные по переданной σ.
/// </summary>
public static class SellMath
{
    private const double InvSqrt2Pi = 0.39894228040143267793994605993438; // 1/√(2π)

    /// <summary>Стандартная нормальная CDF, аппроксимация Abramowitz–Stegun 26.2.17 (|err| &lt; 7.5e-8).</summary>
    public static double NormCdf(double x)
    {
        if (double.IsNaN(x))
            return double.NaN;
        if (x < -8)
            return 0;
        if (x > 8)
            return 1;

        double t = 1.0 / (1.0 + 0.2316419 * Math.Abs(x));
        double poly = t * (0.319381530 + t * (-0.356563782 + t * (1.781477937 + t * (-1.821255978 + t * 1.330274429))));
        double tail = InvSqrt2Pi * Math.Exp(-0.5 * x * x) * poly;
        return x >= 0 ? 1 - tail : tail;
    }

    private static (double D1, double D2) D1D2(double f, double k, double sigma, double t)
    {
        double sqrtT = Math.Sqrt(t);
        double d1 = (Math.Log(f / k) + 0.5 * sigma * sigma * t) / (sigma * sqrtT);
        return (d1, d1 - sigma * sqrtT);
    }

    /// <summary>
    /// Цена Black-76 (r=0), USD: call = F·Φ(d1) − K·Φ(d2); put = K·Φ(−d2) − F·Φ(−d1).
    /// Вырожденные входы (σ≤0 или T≤0) → внутренняя стоимость; f≤0/k≤0 → 0.
    /// </summary>
    public static double Black76Price(bool isCall, double f, double k, double sigma, double t)
    {
        if (f <= 0 || k <= 0)
            return 0;
        if (sigma <= 0 || t <= 0)
            return Math.Max(0, isCall ? f - k : k - f);

        (double d1, double d2) = D1D2(f, k, sigma, t);
        double p = isCall
            ? f * NormCdf(d1) - k * NormCdf(d2)
            : k * NormCdf(-d2) - f * NormCdf(-d1);
        return double.IsFinite(p) && p > 0 ? p : 0;
    }

    /// <summary>Дельта Black-76 (на форвард): call Φ(d1) ∈ (0,1); put Φ(d1)−1 ∈ (−1,0).</summary>
    public static double Black76Delta(bool isCall, double f, double k, double sigma, double t)
    {
        if (f <= 0 || k <= 0)
            return 0;
        if (sigma <= 0 || t <= 0)
            return isCall ? (f > k ? 1 : 0) : (f < k ? -1 : 0);

        (double d1, _) = D1D2(f, k, sigma, t);
        double d = isCall ? NormCdf(d1) : NormCdf(d1) - 1;
        return double.IsFinite(d) ? d : 0;
    }

    /// <summary>
    /// Тета держателя, USD в ДЕНЬ (отрицательна; доход продавца = −тета):
    /// θ_год = −F·φ(d1)·σ/(2√T); делим на 365 (ACT/365, как всё в проекте).
    /// </summary>
    public static double ThetaPerDayUsd(double f, double k, double sigma, double t)
    {
        if (f <= 0 || k <= 0 || sigma <= 0 || t <= 0)
            return 0;

        (double d1, _) = D1D2(f, k, sigma, t);
        double pdf = InvSqrt2Pi * Math.Exp(-0.5 * d1 * d1);
        double theta = -f * pdf * sigma / (2 * Math.Sqrt(t)) / 365.0;
        return double.IsFinite(theta) ? theta : 0;
    }

    /// <summary>Вега, USD на 1 ПУНКТ IV (1%): F·φ(d1)·√T / 100.</summary>
    public static double VegaPerVolPointUsd(double f, double k, double sigma, double t)
    {
        if (f <= 0 || k <= 0 || sigma <= 0 || t <= 0)
            return 0;

        (double d1, _) = D1D2(f, k, sigma, t);
        double pdf = InvSqrt2Pi * Math.Exp(-0.5 * d1 * d1);
        double vega = f * pdf * Math.Sqrt(t) / 100.0;
        return double.IsFinite(vega) ? vega : 0;
    }

    /// <summary>P(экспирация в деньгах) риск-нейтрально: call Φ(d2), put Φ(−d2).</summary>
    public static double ProbItm(bool isCall, double f, double k, double sigma, double t)
    {
        if (f <= 0 || k <= 0)
            return 0;
        if (sigma <= 0 || t <= 0)
            return (isCall ? f > k : f < k) ? 1 : 0;

        (_, double d2) = D1D2(f, k, sigma, t);
        double p = isCall ? NormCdf(d2) : NormCdf(-d2);
        return double.IsFinite(p) ? p : 0;
    }

    /// <summary>
    /// P(касания страйка до экспирации) ≈ min(1, 2·P(ITM)) — стандартная
    /// reflection-аппроксимация (точна при нулевом дрейфе лог-цены).
    /// </summary>
    public static double ProbTouch(bool isCall, double f, double k, double sigma, double t)
        => Math.Min(1.0, 2.0 * ProbItm(isCall, f, k, sigma, t));

    /// <summary>
    /// Начальная маржа Deribit непокрытой продажи, В МОНЕТЕ на 1 контракт
    /// (docs.deribit.com → Knowledge base → Margin, options):
    /// short call IM = max(0.15 − OTM/F, 0.10) + mark_coin;
    /// short put  IM = max(max(0.15 − OTM/F, 0.10) + mark_coin, MM_put),
    ///   где MM_put = max(0.075, 0.075·mark_coin) + mark_coin.
    /// OTM: call max(0, K−F); put max(0, F−K).
    /// </summary>
    public static double ShortMarginCoin(bool isCall, double f, double k, double markCoin)
    {
        if (f <= 0 || k <= 0 || markCoin < 0)
            return 0;

        double otm = isCall ? Math.Max(0, k - f) : Math.Max(0, f - k);
        double im = Math.Max(0.15 - otm / f, 0.10) + markCoin;
        if (!isCall)
        {
            double mm = Math.Max(0.075, 0.075 * markCoin) + markCoin;
            im = Math.Max(im, mm);
        }

        return double.IsFinite(im) ? im : 0;
    }

    /// <summary>
    /// Маржа короткого стрэнгла Deribit, в монете: бОльшая из IM ног + марк ВТОРОЙ ноги.
    /// </summary>
    public static double StrangleMarginCoin(double imCall, double imPut, double markCallCoin, double markPutCoin)
        => imCall >= imPut ? imCall + markPutCoin : imPut + markCallCoin;

    /// <summary>
    /// EWMA реализованная волатильность по нерегулярному ряду цен (3ч-снимки), годовая ДОЛЯ.
    /// Веса w = 0.5^(возраст_дней/halfLife); квадраты лог-доходностей нормируются на свой Δt:
    /// σ²_год = Σ w·r²/Δt_лет / Σ w. 0 — меньше 8 валидных шагов (потребитель уходит в фолбэк).
    /// </summary>
    public static double EwmaRealizedVolAnnual(IReadOnlyList<PricePoint> series, double halfLifeDays = 7)
    {
        if (series is null || series.Count < 9 || halfLifeDays <= 0)
            return 0;

        DateTimeOffset last = series[^1].Time;
        double sumW = 0, sumWR2 = 0;
        int steps = 0;

        for (int i = 1; i < series.Count; i++)
        {
            double p0 = series[i - 1].Price;
            double p1 = series[i].Price;
            double dtYears = (series[i].Time - series[i - 1].Time).TotalDays / 365.0;
            if (p0 <= 0 || p1 <= 0 || dtYears <= 0)
                continue;

            double r = Math.Log(p1 / p0);
            double ageDays = (last - series[i].Time).TotalDays;
            double w = Math.Pow(0.5, ageDays / halfLifeDays);
            sumW += w;
            sumWR2 += w * r * r / dtYears;
            steps++;
        }

        if (steps < 8 || sumW <= 0)
            return 0;

        double vol = Math.Sqrt(sumWR2 / sumW);
        return double.IsFinite(vol) ? vol : 0;
    }
}
