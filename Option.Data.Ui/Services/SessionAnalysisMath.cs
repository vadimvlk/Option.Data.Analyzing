using Option.Data.Ui.Models;
using Option.Data.Shared.Dto;

namespace Option.Data.Ui.Services;

/// <summary>
/// Чистые статические функции для синтеза торгового плана сессии: Black-76 гамма,
/// профиль Net GEX, gamma-flip, GEX-взвешенные стены, пин-страйк, лог-нормальные σ-границы,
/// сессионная σ и тренд дельта-экспозиции по истории снимков.
///
/// Соглашения согласованы с <see cref="OptionExposureMath"/>:
/// <list type="bullet">
/// <item>гамма считается по Black-76 (на форвард <c>f</c>), γ ≥ 0;</item>
/// <item>σ передаётся ДОЛЕЙ (mark_iv/100), не в процентах;</item>
/// <item>знак Net GEX: дилер +гамма по коллам, −по путам (как в <see cref="OptionExposureMath.NetGammaExposure"/>);</item>
/// <item>денежные результаты — в USD на 1% движения цены.</item>
/// </list>
/// </summary>
public static class SessionAnalysisMath
{
    public readonly record struct GammaStrike(double Strike, double CallOi, double PutOi, double SigmaFraction, double TYears);

    /// <summary>
    /// Разложение GEX по страйку в текущей цене: колл- и пут-нога отдельно (обе ≥ 0, USD/1%).
    /// Net = CallGex − PutGex; «масса» страйка = CallGex + PutGex.
    /// </summary>
    public readonly record struct StrikeGexBreakdown(double Strike, double CallGex, double PutGex);

    private const double InvSqrt2Pi = 0.39894228040143267793994605993438; // 1/√(2π)

    /// <summary>Доля года в одной торговой сессии (1 календарный день, ACT/365).</summary>
    public const double SessionYears = 1.0 / 365.0;

    /// <summary>
    /// Black-76 гамма: d1 = (ln(f/k) + 0.5·σ²·T) / (σ·√T); γ = φ(d1) / (f·σ·√T),
    /// где φ(x) = exp(−x²/2)/√(2π) — стандартная нормальная плотность.
    /// Возврат 0 при σ≤0, T≤0, f≤0, k≤0 либо нечисловом результате.
    /// </summary>
    public static double Black76Gamma(double f, double k, double sigmaFraction, double tYears)
    {
        if (sigmaFraction <= 0 || tYears <= 0 || f <= 0 || k <= 0)
            return 0;

        double sqrtT = Math.Sqrt(tYears);
        double denom = f * sigmaFraction * sqrtT;
        if (denom <= 0)
            return 0;

        double d1 = (Math.Log(f / k) + 0.5 * sigmaFraction * sigmaFraction * tYears) / (sigmaFraction * sqrtT);
        double pdf = InvSqrt2Pi * Math.Exp(-0.5 * d1 * d1);
        double gamma = pdf / denom;

        return double.IsFinite(gamma) ? gamma : 0;
    }

    /// <summary>
    /// Σ Black76Gamma(price,K,σ,T)·(CallOi−PutOi)·price²·0.01 — Net GEX по набору страйков в заданной цене.
    /// Конвенция как в <see cref="OptionExposureMath.NetGammaExposure"/>.
    /// </summary>
    public static double NetGexAtPrice(IReadOnlyList<GammaStrike> strikes, double price)
    {
        if (strikes.Count == 0 || price <= 0 || !double.IsFinite(price))
            return 0;

        double scale = price * price * 0.01;
        double sum = 0;

        for (int i = 0; i < strikes.Count; i++)
        {
            GammaStrike s = strikes[i];
            double gamma = Black76Gamma(price, s.Strike, s.SigmaFraction, s.TYears);
            if (gamma == 0)
                continue;

            sum += gamma * (s.CallOi - s.PutOi) * scale;
        }

        return double.IsFinite(sum) ? sum : 0;
    }

    /// <summary>
    /// Разложение GEX по страйкам в заданной цене (агрегация по страйку через все экспирации
    /// набора). CallGex = Σγ·CallOi·S²·0.01, PutGex = Σγ·PutOi·S²·0.01 — обе неотрицательны.
    /// Источник GEX-взвешенных стен и пин-страйка.
    /// </summary>
    public static List<StrikeGexBreakdown> StrikeGexAtPrice(IReadOnlyList<GammaStrike> strikes, double price)
    {
        var result = new List<StrikeGexBreakdown>();
        if (strikes.Count == 0 || price <= 0 || !double.IsFinite(price))
            return result;

        double scale = price * price * 0.01;
        var byStrike = new Dictionary<double, (double Call, double Put)>();

        for (int i = 0; i < strikes.Count; i++)
        {
            GammaStrike s = strikes[i];
            double gamma = Black76Gamma(price, s.Strike, s.SigmaFraction, s.TYears);
            if (gamma == 0)
                continue;

            byStrike.TryGetValue(s.Strike, out (double Call, double Put) acc);
            acc.Call += gamma * s.CallOi * scale;
            acc.Put += gamma * s.PutOi * scale;
            byStrike[s.Strike] = acc;
        }

        foreach (KeyValuePair<double, (double Call, double Put)> kv in byStrike)
        {
            if (double.IsFinite(kv.Value.Call) && double.IsFinite(kv.Value.Put))
                result.Add(new StrikeGexBreakdown(kv.Key, kv.Value.Call, kv.Value.Put));
        }

        result.Sort((a, b) => a.Strike.CompareTo(b.Strike));
        return result;
    }

    /// <summary>
    /// GEX-взвешенная CALL-стена: страйк выше спота с максимальной колл-ногой GEX.
    /// В отличие от стены по «сырому» OI, гамма-вес гасит вклад дальних лотерейных страйков
    /// и поднимает значимость близких к деньгам. null — нет страйков выше спота с ненулевым GEX.
    /// </summary>
    public static double? GexCallWall(IReadOnlyList<StrikeGexBreakdown> breakdown, double spot)
    {
        double? best = null;
        double bestGex = 0;
        foreach (StrikeGexBreakdown b in breakdown)
        {
            if (b.Strike <= spot || b.CallGex <= 0)
                continue;
            if (best is null || b.CallGex > bestGex)
            {
                best = b.Strike;
                bestGex = b.CallGex;
            }
        }
        return best;
    }

    /// <summary>GEX-взвешенная PUT-стена: страйк ниже спота с максимальной пут-ногой GEX. См. <see cref="GexCallWall"/>.</summary>
    public static double? GexPutWall(IReadOnlyList<StrikeGexBreakdown> breakdown, double spot)
    {
        double? best = null;
        double bestGex = 0;
        foreach (StrikeGexBreakdown b in breakdown)
        {
            if (b.Strike >= spot || b.PutGex <= 0)
                continue;
            if (best is null || b.PutGex > bestGex)
            {
                best = b.Strike;
                bestGex = b.PutGex;
            }
        }
        return best;
    }

    /// <summary>
    /// Пин-страйк: страйк с максимальной суммарной «массой» гаммы (CallGex+PutGex) в пределах
    /// [lower, upper]. В +гамме работает как магнит цены (пин). null — нет данных в диапазоне.
    /// </summary>
    public static double? PeakGexStrike(IReadOnlyList<StrikeGexBreakdown> breakdown, double lower, double upper)
    {
        double? best = null;
        double bestMass = 0;
        foreach (StrikeGexBreakdown b in breakdown)
        {
            if (b.Strike < lower || b.Strike > upper)
                continue;
            double mass = b.CallGex + b.PutGex;
            if (mass <= 0)
                continue;
            if (best is null || mass > bestMass)
            {
                best = b.Strike;
                bestMass = mass;
            }
        }
        return best;
    }

    /// <summary>
    /// Профиль Net GEX в диапазоне [spot·lowFactor … spot·highFactor], <paramref name="steps"/> точек.
    /// </summary>
    public static List<GammaProfilePoint> GammaProfile(IReadOnlyList<GammaStrike> strikes, double spot, double lowFactor = 0.85, double highFactor = 1.15, int steps = 120)
    {
        var result = new List<GammaProfilePoint>();

        if (spot <= 0 || !double.IsFinite(spot) || steps < 1 || highFactor <= lowFactor)
            return result;

        double low = spot * lowFactor;
        double high = spot * highFactor;

        if (steps == 1)
        {
            result.Add(new GammaProfilePoint { Price = low, NetGex = NetGexAtPrice(strikes, low) });
            return result;
        }

        double stepSize = (high - low) / (steps - 1);
        result.Capacity = steps;

        for (int i = 0; i < steps; i++)
        {
            double price = low + stepSize * i;
            result.Add(new GammaProfilePoint
            {
                Price = price,
                NetGex = NetGexAtPrice(strikes, price)
            });
        }

        return result;
    }

    /// <summary>
    /// Ближайшая к споту цена смены знака Net GEX (линейная интерполяция между соседними точками).
    /// null — если знак не меняется ни на одном интервале.
    /// </summary>
    public static double? GammaFlip(IReadOnlyList<GammaProfilePoint> profile, double spot)
    {
        if (profile.Count < 2)
            return null;

        double? bestFlip = null;
        double bestDistance = double.MaxValue;

        for (int i = 0; i < profile.Count - 1; i++)
        {
            double y0 = profile[i].NetGex;
            double y1 = profile[i + 1].NetGex;

            if (!double.IsFinite(y0) || !double.IsFinite(y1))
                continue;

            // Точное попадание в ноль на левом крае интервала.
            bool crosses = (y0 < 0 && y1 > 0) || (y0 > 0 && y1 < 0);
            bool touchesZero = y0 == 0;

            double crossPrice;
            if (touchesZero)
            {
                crossPrice = profile[i].Price;
            }
            else if (crosses)
            {
                double x0 = profile[i].Price;
                double x1 = profile[i + 1].Price;
                double span = y1 - y0;
                if (span == 0)
                    continue;

                double w = -y0 / span; // доля пути от x0 до x1, где y=0
                crossPrice = x0 + w * (x1 - x0);
            }
            else
            {
                continue;
            }

            if (!double.IsFinite(crossPrice))
                continue;

            double distance = Math.Abs(crossPrice - spot);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestFlip = crossPrice;
            }
        }

        // Проверка последней точки на точный ноль (если интервал её не покрыл).
        GammaProfilePoint last = profile[^1];
        if (last.NetGex == 0 && double.IsFinite(last.Price))
        {
            double distance = Math.Abs(last.Price - spot);
            if (distance < bestDistance)
            {
                bestFlip = last.Price;
            }
        }

        return bestFlip;
    }

    /// <summary>
    /// σ одной торговой сессии, USD: S·σ_ATM·√(min(T, 1/365)). Не длиннее времени до экспирации.
    /// Размер зон входа/стопов/буферов — В ЭТОЙ величине, а не в σ до горизонта.
    /// </summary>
    public static double SessionSigmaUsd(double spot, double atmIvFraction, double tYears)
    {
        if (spot <= 0 || atmIvFraction <= 0 || tYears <= 0)
            return 0;

        double t = Math.Min(tYears, SessionYears);
        double s = spot * atmIvFraction * Math.Sqrt(t);
        return double.IsFinite(s) ? s : 0;
    }

    /// <summary>
    /// Лог-нормальные границы k·σ до горизонта: (S·e^{−kσ√T}, S·e^{+kσ√T}).
    /// Корректны при больших σ√T (крипта/дальние экспирации), нижняя граница всегда &gt; 0.
    /// </summary>
    public static (double Lower, double Upper) LogNormalBand(double spot, double atmIvFraction, double tYears, double k)
    {
        if (spot <= 0 || atmIvFraction <= 0 || tYears <= 0 || k <= 0)
            return (spot, spot);

        double w = k * atmIvFraction * Math.Sqrt(tYears);
        double lower = spot * Math.Exp(-w);
        double upper = spot * Math.Exp(w);
        return (double.IsFinite(lower) ? lower : spot, double.IsFinite(upper) ? upper : spot);
    }

    /// <summary>
    /// Тренд дельта-экспозиции по истории снимков, −1…+1 (НЕ направленный бычий/медвежий знак,
    /// а знак ИЗМЕНЕНИЯ DEX: &gt;0 — DEX растёт). Считается как наклон линейной регрессии
    /// DeltaExposure по последним <paramref name="window"/> точкам, нормированный на средний
    /// |DEX| окна и пропущенный через tanh. 0 — мало точек (&lt;4) или вырожденные данные.
    /// Интерпретация знака — на стороне вызывающего (рост дилерского DEX ⇒ хедж-давление вниз).
    /// </summary>
    public static double DexTrend(IReadOnlyList<DeltaPoint> series, int window = 12)
    {
        if (series is null || series.Count < 4 || window < 4)
            return 0;

        int n = Math.Min(window, series.Count);
        int start = series.Count - n;

        double meanX = (n - 1) / 2.0;
        double meanY = 0, meanAbs = 0;
        for (int i = 0; i < n; i++)
        {
            double y = series[start + i].DeltaExposure;
            if (!double.IsFinite(y))
                return 0;
            meanY += y;
            meanAbs += Math.Abs(y);
        }
        meanY /= n;
        meanAbs /= n;
        if (meanAbs <= 0)
            return 0;

        double sxy = 0, sxx = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = i - meanX;
            sxy += dx * (series[start + i].DeltaExposure - meanY);
            sxx += dx * dx;
        }
        if (sxx <= 0)
            return 0;

        // Суммарное изменение за окно в долях среднего |DEX|: наклон·(n−1)/mean|DEX|.
        double slopePerStep = sxy / sxx;
        double relChange = slopePerStep * (n - 1) / meanAbs;
        double t = Math.Tanh(relChange);
        return double.IsFinite(t) ? Clamp(t, -1, 1) : 0;
    }

    /// <summary>CALL-стена по сырому OI (фолбэк, когда GEX-веса недоступны: нет IV/греков).</summary>
    public static OptionData? CallWall(IReadOnlyList<OptionData> chain, double spot)
    {
        OptionData? best = null;
        for (int i = 0; i < chain.Count; i++)
        {
            OptionData o = chain[i];
            if (o.Strike <= spot)
                continue;

            if (best is null || o.CallOi > best.CallOi)
                best = o;
        }

        return best is { CallOi: > 0 } ? best : null;
    }

    /// <summary>PUT-стена по сырому OI (фолбэк, когда GEX-веса недоступны: нет IV/греков).</summary>
    public static OptionData? PutWall(IReadOnlyList<OptionData> chain, double spot)
    {
        OptionData? best = null;
        for (int i = 0; i < chain.Count; i++)
        {
            OptionData o = chain[i];
            if (o.Strike >= spot)
                continue;

            if (best is null || o.PutOi > best.PutOi)
                best = o;
        }

        return best is { PutOi: > 0 } ? best : null;
    }

    /// <summary>Ограничение значения диапазоном [lo, hi].</summary>
    public static double Clamp(double v, double lo, double hi)
    {
        if (v < lo)
            return lo;
        return v > hi ? hi : v;
    }
}
