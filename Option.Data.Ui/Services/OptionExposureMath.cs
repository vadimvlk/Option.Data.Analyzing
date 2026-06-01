using System.Globalization;
using Option.Data.Shared.Dto;

namespace Option.Data.Ui.Services;

/// <summary>
/// Чистые функции риск-метрик по цепочке опционов ОДНОЙ экспирации.
///
/// Соглашения Deribit (проверены по docs.deribit.com), на которых построены формулы:
/// <list type="bullet">
/// <item>греки — Black-76 (считаются на форвард): δ_call ∈ (0,1], δ_put ∈ [-1,0), γ ≥ 0 у коллов и путов;</item>
/// <item><see cref="OptionData.CallOi"/>/<see cref="OptionData.PutOi"/> — в монетах (1 контракт = 1 BTC/ETH);</item>
/// <item><see cref="OptionData.Iv"/> = mark_iv — в ПРОЦЕНТАХ годовых (80 = 80%);</item>
/// <item>UnderlyingPrice — форвард экспирации в USD.</item>
/// </list>
/// Все денежные результаты — в USD.
/// </summary>
public static class OptionExposureMath
{
    /// <summary>Опционы Deribit истекают в 08:00 UTC.</summary>
    private const int DeribitExpiryHourUtc = 8;

    /// <summary>
    /// DEX — долларовый дельта-нотионал «глобального продавца» (delta-adjusted notional, USD);
    /// численно ≈ изменение стоимости позиции при движении спота на 100%.
    /// DEX = -Σ(δ_call·OI_call + δ_put·OI_put)·S.
    /// Продавец считается шортом всего открытого интереса: δ_put&lt;0 даёт + (продавец путов
    /// выигрывает от роста), δ_call&gt;0 даёт −. Множитель S переводит «монето-дельту» (δ·OI,
    /// чувствительность на $1 движения) в долларовый нотионал. NB: это НЕ «USD на $1 движения»
    /// (та величина — без S); в скоринге направления нормируется обратно на спот·OI.
    /// </summary>
    public static double DollarDeltaExposure(IReadOnlyList<OptionData> chain, double underlyingPrice)
        => -chain.Sum(o => o.CallDelta * o.CallOi + o.PutDelta * o.PutOi) * underlyingPrice;

    /// <summary>
    /// Net GEX — чистая дилерская гамма-экспозиция (конвенция SpotGamma: дилеры в лонге коллов
    /// и шорте путов), USD на 1% движения спота:
    /// GEX = Σ(γ_call·OI_call − γ_put·OI_put)·S²·0.01.
    /// В отличие от прежней -Σ(γ·OI) (которая суммировала колл- и пут-гамму с одним знаком и
    /// потому НИКОГДА не меняла знак), эта величина может пересекать ноль ⇒ имеет смысл режим:
    /// GEX&gt;0 — дилеры гасят волатильность (стабилизация), GEX&lt;0 — усиливают движения.
    /// </summary>
    public static double NetGammaExposure(IReadOnlyList<OptionData> chain, double underlyingPrice)
        => chain.Sum(o => o.CallGamma * o.CallOi - o.PutGamma * o.PutOi)
           * underlyingPrice * underlyingPrice * 0.01;

    /// <summary>
    /// Max Pain — страйк, минимизирующий суммарную внутреннюю стоимость опционов держателей
    /// (Σ OI_call·max(0,K−Kᵢ) + Σ OI_put·max(0,Kᵢ−K)).
    /// </summary>
    public static double MaxPain(IReadOnlyList<OptionData> chain)
    {
        double bestStrike = 0;
        double minLoss = double.MaxValue;

        foreach (OptionData target in chain)
        {
            double loss = 0;
            foreach (OptionData o in chain)
            {
                loss += o.CallOi * Math.Max(0, target.Strike - o.Strike);
                loss += o.PutOi * Math.Max(0, o.Strike - target.Strike);
            }

            if (loss < minLoss)
            {
                minLoss = loss;
                bestStrike = target.Strike;
            }
        }

        return bestStrike;
    }

    /// <summary>
    /// Доля года до экспирации (ACT/365) от момента снимка <paramref name="asOf"/>.
    /// Код экспирации — формата "25JUL25" ("dMMMyy", как в BaseOptionPageModel),
    /// время истечения — 08:00 UTC. Никогда не отрицательна.
    /// </summary>
    public static double YearsToExpiry(string expiration, DateTimeOffset asOf)
    {
        DateTime d = DateTime.ParseExact(expiration, "dMMMyy", CultureInfo.InvariantCulture);
        var expiryUtc = new DateTimeOffset(d.Year, d.Month, d.Day, DeribitExpiryHourUtc, 0, 0, TimeSpan.Zero);
        return Math.Max((expiryUtc - asOf).TotalDays / 365.0, 0.0);
    }

    /// <summary>
    /// ATM implied volatility как ДОЛЯ (mark_iv/100): IV страйка, ближайшего к форварду.
    /// </summary>
    public static double AtmIvFraction(IReadOnlyList<OptionData> chain, double underlyingPrice)
    {
        if (chain.Count == 0)
            return 0;

        OptionData atm = chain
            .Where(o => o.Iv > 0)
            .DefaultIfEmpty(chain[0])
            .OrderBy(o => Math.Abs(o.Strike - underlyingPrice))
            .First();

        return atm.Iv / 100.0;
    }

    /// <summary>
    /// Ожидаемое движение 1σ к экспирации, USD: EM = S · σ_ATM · √T.
    /// <paramref name="atmIvFraction"/> — доля (уже делённая на 100), <paramref name="yearsToExpiry"/> — доля года.
    /// </summary>
    public static double ExpectedMove1Sigma(double underlyingPrice, double atmIvFraction, double yearsToExpiry)
        => underlyingPrice * atmIvFraction * Math.Sqrt(yearsToExpiry);

    /// <summary>
    /// 25-дельта Risk Reversal (скос), в пунктах волатильности (%): IV(25Δ put) − IV(25Δ call).
    /// &gt;0 — путы дороже коллов (страх падения), &lt;0 — коллы дороже (жадность).
    /// IV интерполируется линейно по |δ|. Возвращает null, если греков/IV недостаточно
    /// (например, на странице Snapshot, где греки занулены).
    /// </summary>
    public static double? RiskReversal25Delta(IReadOnlyList<OptionData> chain)
    {
        double? callIv = InterpolateIvAtAbsDelta(
            chain.Where(o => o.CallOi > 0 && o.CallDelta > 0 && o.Iv > 0)
                 .Select(o => (AbsDelta: o.CallDelta, o.Iv)),
            0.25);

        // Put-нога скоса считается по СОБСТВЕННОЙ put-IV (o.PutIv), а не по объединённой
        // Iv (которая на страйке с коллом равна call-IV) — иначе путовая дельта пэйрится
        // с call-волатильностью. Фолбэк на o.Iv, если put-IV отсутствует (PutIv==0).
        double? putIv = InterpolateIvAtAbsDelta(
            chain.Where(o => o.PutOi > 0 && o.PutDelta < 0)
                 .Select(o => (AbsDelta: Math.Abs(o.PutDelta), Iv: o.PutIv > 0 ? o.PutIv : o.Iv))
                 .Where(x => x.Iv > 0),
            0.25);

        if (callIv is null || putIv is null)
            return null;

        return putIv - callIv;
    }

    /// <summary>
    /// Линейная интерполяция IV по модулю дельты к целевому |δ| без экстраполяции.
    /// Если целевая |δ| вне наблюдаемого диапазона — край принимается только при близости
    /// (допуск <see cref="DeltaEdgeTolerance"/>), иначе возвращается null (RR недоступен),
    /// чтобы не выдавать IV узла с произвольной дельтой за «25-дельта».
    /// </summary>
    private const double DeltaEdgeTolerance = 0.10;

    private static double? InterpolateIvAtAbsDelta(IEnumerable<(double AbsDelta, double Iv)> points, double targetAbsDelta)
    {
        List<(double AbsDelta, double Iv)> p = points.OrderBy(x => x.AbsDelta).ToList();

        if (p.Count == 0)
            return null;
        if (p.Count == 1)
            return p[0].Iv;

        for (int i = 0; i < p.Count - 1; i++)
        {
            if (targetAbsDelta >= p[i].AbsDelta && targetAbsDelta <= p[i + 1].AbsDelta)
            {
                double span = p[i + 1].AbsDelta - p[i].AbsDelta;
                if (span <= 0)
                    return p[i].Iv;

                double w = (targetAbsDelta - p[i].AbsDelta) / span;
                return p[i].Iv + w * (p[i + 1].Iv - p[i].Iv);
            }
        }

        bool belowRange = targetAbsDelta < p[0].AbsDelta;
        double edgeDelta = belowRange ? p[0].AbsDelta : p[^1].AbsDelta;
        double edgeIv = belowRange ? p[0].Iv : p[^1].Iv;
        return Math.Abs(edgeDelta - targetAbsDelta) <= DeltaEdgeTolerance ? edgeIv : null;
    }
}
