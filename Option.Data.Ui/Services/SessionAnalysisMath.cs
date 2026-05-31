using Option.Data.Ui.Models;
using Option.Data.Shared.Dto;

namespace Option.Data.Ui.Services;

/// <summary>
/// Чистые статические функции для синтеза торгового плана сессии: Black-76 гамма,
/// профиль Net GEX, gamma-flip, дневное σ, стены OI.
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

    private const double InvSqrt2Pi = 0.39894228040143267793994605993438; // 1/√(2π)

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
                bestDistance = distance;
                bestFlip = last.Price;
            }
        }

        return bestFlip;
    }

    /// <summary>Дневное ожидаемое движение 1σ: spot·iv·√(sessionYears). 0 при некорректных входах.</summary>
    public static double DailyExpectedMove(double spot, double atmIvFraction, double sessionYears)
    {
        if (spot <= 0 || atmIvFraction <= 0 || sessionYears <= 0)
            return 0;

        double em = spot * atmIvFraction * Math.Sqrt(sessionYears);
        return double.IsFinite(em) ? em : 0;
    }

    /// <summary>CALL-стена: запись с максимальным CallOi среди страйков выше спота. null, если таких нет.</summary>
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

        return best;
    }

    /// <summary>PUT-стена: запись с максимальным PutOi среди страйков ниже спота. null, если таких нет.</summary>
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

        return best;
    }

    /// <summary>Ограничение значения диапазоном [lo, hi].</summary>
    public static double Clamp(double v, double lo, double hi)
    {
        if (v < lo)
            return lo;
        return v > hi ? hi : v;
    }
}
