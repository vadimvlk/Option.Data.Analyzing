using System.Globalization;
using Option.Data.Shared.Dto;
using Option.Data.Ui.Models;

namespace Option.Data.Ui.Services;

/// <summary>
/// Синтезирует торговый план Trade (<see cref="SessionRecommendation"/>): профиль Net GEX,
/// gamma-flip, режим волатильности, σ-диапазон, ключевые уровни и решающую сделку.
/// Поддерживает два режима через общее ядро <see cref="Assemble"/>:
/// <see cref="Build"/> — по одной выбранной экспирации; <see cref="BuildAggregate"/> — по окну
/// «ближние + квартальная» (профиль собирается по всем экспирациям окна со своими T/σ;
/// Max Pain/стены/центроид/детали — по сводной цепочке; σ — по квартальной).
///
/// Направление и главная сделка строятся по дилерской логике (раздел C спека): положение спота
/// относительно gamma-flip + знак DEX (DEX&gt;0 → шорт). Flip ведёт, DEX подтверждает/ослабляет.
///
/// Соглашения согласованы с <see cref="OptionExposureMath"/> и <see cref="SessionAnalysisMath"/>:
/// σ передаётся долей (mark_iv/100); DEX — долларовый дельта-нотионал; Net GEX — USD на 1% движения.
/// </summary>
public class SessionRecommendationBuilder : ISessionRecommendationBuilder
{
    /// <summary>
    /// Порог «≈0» для Net GEX у спота при определении режима: доля от пикового |NetGex| профиля.
    /// </summary>
    private const double NeutralGexProfileFraction = 0.05;

    /// <summary>Допуск дедупликации уровней по цене, % от спота.</summary>
    private const double LevelDedupPercent = 0.1;

    /// <summary>Полуширина зоны входа в долях σ (вход «от рынка» вокруг спота). КАЛИБРУЕМО.</summary>
    private const double EntryZoneSigma = 0.15;

    // --- Решающая логика направления (flip + DEX). КАЛИБРУЕМО. ---
    /// <summary>Нормированный |DEX|, при котором dStr≈tanh(1). КАЛИБРУЕМО.</summary>
    private const double DexSensitivity = 0.10;
    /// <summary>База магнитуды направления при споте по верную сторону flip. КАЛИБРУЕМО.</summary>
    private const double FlipBaseMag = 0.35;
    /// <summary>Прирост магнитуды за удаление спота от flip (в σ). КАЛИБРУЕМО.</summary>
    private const double FlipDistGain = 0.45;
    /// <summary>Усиление, когда DEX подтверждает сторону flip. КАЛИБРУЕМО.</summary>
    private const double DexConfirmBoost = 0.20;
    /// <summary>Ослабление, когда DEX против flip (flip всё равно ведёт). КАЛИБРУЕМО.</summary>
    private const double DexOpposeDamp = 0.25;
    /// <summary>Пол магнитуды при DEX-против (держит сторону flip, не «Нейтрально»). КАЛИБРУЕМО.</summary>
    private const double DexOpposeFloor = 0.18;
    /// <summary>База магнитуды, когда направление ведёт только DEX (нет flip). КАЛИБРУЕМО.</summary>
    private const double DexOnlyBase = 0.20;
    /// <summary>Прирост магнитуды DEX-only за силу DEX. КАЛИБРУЕМО.</summary>
    private const double DexOnlyGain = 0.30;

    public SessionRecommendation Build(ExpirationAnalysis selected, string currency, DateTimeOffset asOf)
    {
        if (selected is null || selected.OptionData.Count == 0)
        {
            var empty = new SessionRecommendation { Currency = currency, AsOf = asOf };
            empty.Notes.Add("Пустой снимок: нет данных по выбранной экспирации — план не строится.");
            empty.Primary = StandAsidePrimary("нет данных по выбранной экспирации — план не строится.");
            return empty;
        }

        double tYears = OptionExposureMath.YearsToExpiry(selected.Expiration, asOf);
        double spot = selected.UnderlyingPrice;
        double atmIv = OptionExposureMath.AtmIvFraction(selected.OptionData, spot);
        double sigma1 = selected.ExpectedMove1Sigma > 0 && double.IsFinite(selected.ExpectedMove1Sigma)
            ? selected.ExpectedMove1Sigma
            : OptionExposureMath.ExpectedMove1Sigma(spot, atmIv, tYears);

        List<SessionAnalysisMath.GammaStrike> gammaStrikes = BuildGammaStrikes(selected, asOf);

        return Assemble(currency, asOf, selected.Expiration, tYears * 365.0, spot,
            gammaStrikes, selected.OptionData, sigma1, atmIv, tYears,
            selected.DollarDeltaExposure, isAggregated: false, aggregatedCount: 1);
    }

    public SessionRecommendation BuildAggregate(
        IReadOnlyList<ExpirationAnalysis> window, string currency,
        DateTimeOffset asOf)
    {
        if (window is null || window.Count == 0 || window.All(a => a.OptionData.Count == 0))
        {
            var empty = new SessionRecommendation { Currency = currency, AsOf = asOf, IsAggregated = true };
            empty.Notes.Add("Пустой снимок: нет данных для агрегированного расчёта — план не строится.");
            empty.Primary = StandAsidePrimary("нет данных для агрегированного расчёта — план не строится.");
            return empty;
        }

        double spot = window.Max(a => a.UnderlyingPrice);

        // Горизонт = самая дальняя экспирация окна (квартальная): её T и σ.
        ExpirationAnalysis horizon = window
            .OrderByDescending(a => OptionExposureMath.YearsToExpiry(a.Expiration, asOf))
            .First();
        double tYears = OptionExposureMath.YearsToExpiry(horizon.Expiration, asOf);
        double atmIv = OptionExposureMath.AtmIvFraction(horizon.OptionData, horizon.UnderlyingPrice);
        double sigma1 = horizon.ExpectedMove1Sigma > 0 && double.IsFinite(horizon.ExpectedMove1Sigma)
            ? horizon.ExpectedMove1Sigma
            : OptionExposureMath.ExpectedMove1Sigma(spot, atmIv, tYears);

        // Профиль Net GEX: GammaStrike каждой экспирации окна (свои T/σ), конкатенация.
        var gammaStrikes = new List<SessionAnalysisMath.GammaStrike>();
        foreach (ExpirationAnalysis a in window)
            gammaStrikes.AddRange(BuildGammaStrikes(a, asOf));

        // Сводная цепочка (ΣOI по страйку) — Max Pain/стены/центроид/детали.
        List<OptionData> chain = AggregateChain(window);

        // DEX — сумма по окну.
        double dexRaw = window.Sum(a => a.DollarDeltaExposure);

        return Assemble(currency, asOf, horizon.Expiration, tYears * 365.0, spot,
            gammaStrikes, chain, sigma1, atmIv, tYears, dexRaw,
            isAggregated: true, aggregatedCount: window.Count);
    }

    /// <summary>Сводная цепочка окна: ΣCallOi/ΣPutOi по страйку (страйки — точные значения из int БД).</summary>
    private static List<OptionData> AggregateChain(IReadOnlyList<ExpirationAnalysis> window)
    {
        var byStrike = new Dictionary<double, OptionData>();
        foreach (ExpirationAnalysis a in window)
        foreach (OptionData o in a.OptionData)
        {
            if (!byStrike.TryGetValue(o.Strike, out OptionData? agg))
            {
                agg = new OptionData { Strike = o.Strike };
                byStrike[o.Strike] = agg;
            }
            agg.CallOi += o.CallOi;
            agg.PutOi += o.PutOi;
        }
        return byStrike.Values.OrderBy(o => o.Strike).ToList();
    }

    /// <summary>
    /// Общее ядро плана: профиль гаммы (диапазон покрывает 2σ), режим, σ-диапазон, стены,
    /// Max Pain, центроид, уровни, направление (flip+DEX) и главная сделка. Примитивы уже
    /// посчитаны вызывающим (Build/BuildAggregate).
    /// </summary>
    private SessionRecommendation Assemble(
        string currency, DateTimeOffset asOf,
        string expirationLabel, double horizonDte, double spot,
        List<SessionAnalysisMath.GammaStrike> gammaStrikes,
        IReadOnlyList<OptionData> chain,
        double sigma1, double atmIv, double tYears,
        double dexRaw, bool isAggregated, int aggregatedCount)
    {
        var rec = new SessionRecommendation
        {
            Currency = currency,
            AsOf = asOf,
            FrontExpiration = expirationLabel,
            FrontDaysToExpiry = horizonDte,
            HoursToFrontExpiry = Math.Max(horizonDte * 24.0, 0.0),
            Spot = spot,
            FrontChain = chain.ToList(),
            IsAggregated = isAggregated,
            AggregatedCount = aggregatedCount
        };

        if (spot <= 0 || !double.IsFinite(spot))
        {
            rec.Notes.Add("Некорректная цена базового актива — расчёт диапазона недоступен.");
            rec.Primary = StandAsidePrimary("некорректная цена базового актива (битый/частичный снимок) — план не строится.");
            return rec;
        }

        rec.Range = new SessionRange
        {
            AtmIvPercent = atmIv * 100.0,
            SessionYears = tYears,
            DailySigma1 = sigma1,
            Lower1 = spot - sigma1,
            Upper1 = spot + sigma1,
            Lower2 = spot - 2 * sigma1,
            Upper2 = spot + 2 * sigma1
        };

        // Профиль Net GEX: диапазон покрывает 2σ (минимум ±15%).
        double half = Math.Max(0.15, 2 * sigma1 / spot * 1.15);
        double lowFactor = Math.Max(0.30, 1 - half);
        double highFactor = 1 + half;
        rec.GammaProfile = SessionAnalysisMath.GammaProfile(gammaStrikes, spot, lowFactor, highFactor);

        double? gammaFlip = SessionAnalysisMath.GammaFlip(rec.GammaProfile, spot);
        rec.GammaFlip = gammaFlip;

        double netGexAtSpot = SessionAnalysisMath.NetGexAtPrice(gammaStrikes, spot);
        if (!double.IsFinite(netGexAtSpot)) netGexAtSpot = 0;
        rec.NetGexAtSpot = netGexAtSpot;

        double profilePeak = rec.GammaProfile.Count > 0
            ? rec.GammaProfile.Max(p => double.IsFinite(p.NetGex) ? Math.Abs(p.NetGex) : 0)
            : 0;
        double neutralBand = profilePeak * NeutralGexProfileFraction;
        rec.Regime = netGexAtSpot > neutralBand
            ? VolatilityRegime.PositiveGamma
            : netGexAtSpot < -neutralBand
                ? VolatilityRegime.NegativeGamma
                : VolatilityRegime.Neutral;

        OptionData? callWall = SessionAnalysisMath.CallWall(chain, spot);
        OptionData? putWall = SessionAnalysisMath.PutWall(chain, spot);
        double maxPain = OptionExposureMath.MaxPain(chain);
        double totalOi = chain.Sum(o => o.CallOi + o.PutOi);
        double centroid = totalOi > 0
            ? chain.Sum(o => o.Strike * (o.CallOi + o.PutOi)) / totalOi
            : maxPain;
        if (!double.IsFinite(centroid)) centroid = maxPain;

        rec.Levels = BuildLevels(rec, spot, callWall, putWall, maxPain, centroid, gammaFlip);

        // Экстремумы профиля (цели; на карту не наносятся).
        double gammaMinPrice = 0, gammaMaxPrice = 0;
        if (rec.GammaProfile.Count > 0)
        {
            gammaMinPrice = rec.GammaProfile.Aggregate((a, c) => c.NetGex < a.NetGex ? c : a).Price;
            gammaMaxPrice = rec.GammaProfile.Aggregate((a, c) => c.NetGex > a.NetGex ? c : a).Price;
        }

        int conviction = ScoreDirection(rec, spot, sigma1, gammaFlip, dexRaw, totalOi);

        rec.Primary = BuildPrimaryTrade(rec, spot, callWall, putWall, maxPain, centroid,
            gammaFlip, gammaMinPrice, gammaMaxPrice, sigma1, dexRaw, conviction);

        return rec;
    }

    /// <summary>
    /// Строит список GammaStrike по каждому страйку экспирации: σ — долей (Iv/100),
    /// T — годы до экспирации от момента снимка. Пропускает страйки без OI.
    /// </summary>
    private static List<SessionAnalysisMath.GammaStrike> BuildGammaStrikes(
        ExpirationAnalysis analysis, DateTimeOffset asOf)
    {
        var result = new List<SessionAnalysisMath.GammaStrike>();
        double tYears = OptionExposureMath.YearsToExpiry(analysis.Expiration, asOf);

        foreach (OptionData o in analysis.OptionData)
        {
            if (o.CallOi <= 0 && o.PutOi <= 0)
                continue;

            result.Add(new SessionAnalysisMath.GammaStrike(
                Strike: o.Strike,
                CallOi: o.CallOi,
                PutOi: o.PutOi,
                SigmaFraction: o.Iv / 100.0,
                TYears: tYears));
        }

        return result;
    }

    /// <summary>
    /// Направление = синтез gamma-flip и DEX (дилерская трактовка: DEX&gt;0 → шорт). Flip ведёт:
    /// при споте по верную сторону flip знак фиксирован, DEX лишь усиливает/ослабляет. Нет flip —
    /// ведёт DEX. Возвращает конвикцию 0..100; заполняет Bias/BiasScore/BiasComponents (драйверы).
    /// </summary>
    private int ScoreDirection(
        SessionRecommendation rec, double spot, double sigma1,
        double? gammaFlip, double dexRaw, double totalOi)
    {
        double safeSigma = sigma1 > 0 && double.IsFinite(sigma1) ? sigma1 : spot * 0.01;

        int flipVote = gammaFlip is { } flip && double.IsFinite(flip)
            ? (spot < flip ? -1 : spot > flip ? +1 : 0)
            : 0;
        int dexVote = dexRaw > 0 ? -1 : dexRaw < 0 ? +1 : 0;

        double fStr = gammaFlip is { } gf && double.IsFinite(gf)
            ? Math.Tanh(Math.Abs(spot - gf) / safeSigma)
            : 0;
        double dexNorm = totalOi > 0 && double.IsFinite(totalOi)
            ? SessionAnalysisMath.Clamp(Math.Abs(dexRaw) / (spot * totalOi), 0, 1)
            : 0;
        double dStr = Math.Tanh(dexNorm / DexSensitivity);

        double b;
        if (flipVote != 0)
        {
            double mag = FlipBaseMag + FlipDistGain * fStr;
            if (dexVote == flipVote) mag = Math.Min(1.0, mag + DexConfirmBoost * dStr);
            else if (dexVote == -flipVote) mag = Math.Max(DexOpposeFloor, mag - DexOpposeDamp * dStr);
            b = flipVote * mag;
        }
        else if (dexVote != 0)
        {
            b = dexVote * (DexOnlyBase + DexOnlyGain * dStr);
        }
        else
        {
            b = 0;
        }

        b = SessionAnalysisMath.Clamp(b, -1, 1);

        var components = new List<BiasComponent>();
        if (flipVote != 0 && gammaFlip is { } f2)
        {
            components.Add(new BiasComponent
            {
                Name = "Gamma-flip",
                RawValue = spot - f2,
                Normalized = flipVote,
                Weight = 0.6,
                Contribution = flipVote * 0.6,
                Explanation = $"спот {Fmt(spot)} {(flipVote < 0 ? "ниже" : "выше")} gamma-flip {Fmt(f2)} " +
                              $"→ {(flipVote < 0 ? "шорт" : "лонг")} ({(rec.Regime == VolatilityRegime.PositiveGamma ? "+гамма" : rec.Regime == VolatilityRegime.NegativeGamma ? "−гамма" : "гамма≈0")})."
            });
        }
        if (dexVote != 0)
        {
            components.Add(new BiasComponent
            {
                Name = "DEX (дилер)",
                RawValue = dexRaw,
                Normalized = dexVote,
                Weight = 0.4,
                Contribution = dexVote * 0.4,
                Explanation = $"DEX {FmtUsd(dexRaw)} ({(dexRaw > 0 ? ">0" : "<0")}) → дилер хеджирует " +
                              $"{(dexRaw > 0 ? "продажей" : "покупкой")} → {(dexVote < 0 ? "шорт" : "лонг")}."
            });
        }

        rec.BiasScore = b;
        rec.BiasComponents = components;
        rec.Bias = MapBias(b);

        return (int)Math.Round(100 * Math.Abs(b));
    }

    /// <summary>
    /// Главная сделка: направление из BiasScore (flip+DEX), вход «от рынка» (спот ∓ 0.15σ),
    /// цель — ближайший уровень в сторону сделки (стены/Max Pain/центроид/σ + экстремумы Net GEX,
    /// если в пределах 2σ; на карту не наносятся), стоп — за gamma-flip (если он на стоп-стороне)
    /// либо спот ∓ буфер. «ВНЕ РЫНКА» — только при отсутствии и flip, и DEX (BiasScore == 0).
    /// </summary>
    private PrimaryTrade BuildPrimaryTrade(
        SessionRecommendation rec, double spot,
        OptionData? callWall, OptionData? putWall,
        double maxPain, double centroid, double? gammaFlip,
        double gammaMinPrice, double gammaMaxPrice,
        double sigma1, double dexRaw, int conviction)
    {
        double b = rec.BiasScore;
        double safeSigma = sigma1 > 0 && double.IsFinite(sigma1) ? sigma1 : spot * 0.01;
        double upper1 = rec.Range.Upper1, lower1 = rec.Range.Lower1;
        double upper2 = rec.Range.Upper2, lower2 = rec.Range.Lower2;

        var trade = new PrimaryTrade
        {
            Conviction = conviction,
            ConvictionLabel = ConvictionLabel(conviction),
            Drivers = TopDrivers(rec.BiasComponents, 2)
        };

        if (b == 0)
        {
            trade.Action = TradeAction.StandAside;
            trade.Side = TradeSide.None;
            trade.Headline = "ВНЕ РЫНКА";
            trade.Reason = "нет ни gamma-flip, ни дельта-сигнала — направление не определено.";
            trade.Setup = "Сетап появится при формировании gamma-flip или сдвиге дельта-экспозиции.";
            trade.Invalidation = "—";
            return trade;
        }

        int dir = b < 0 ? -1 : +1;
        TradeSide side = dir < 0 ? TradeSide.Short : TradeSide.Long;
        trade.Action = TradeAction.Directional;
        trade.Side = side;

        double entryHalf = EntryZoneSigma * safeSigma;
        trade.EntryLow = spot - entryHalf;
        trade.EntryHigh = spot + entryHalf;

        // Кандидаты-цели в сторону сделки: структурные + экстремум Net GEX (в пределах 2σ).
        var candidates = new List<double>();
        if (dir < 0)
        {
            if (putWall is not null) candidates.Add(putWall.Strike);
            candidates.Add(maxPain);
            candidates.Add(centroid);
            candidates.Add(lower1);
            candidates.Add(lower2);
            if (gammaMinPrice > 0 && Math.Abs(gammaMinPrice - spot) <= 2 * safeSigma)
                candidates.Add(gammaMinPrice);
        }
        else
        {
            if (callWall is not null) candidates.Add(callWall.Strike);
            candidates.Add(maxPain);
            candidates.Add(centroid);
            candidates.Add(upper1);
            candidates.Add(upper2);
            if (gammaMaxPrice > 0 && Math.Abs(gammaMaxPrice - spot) <= 2 * safeSigma)
                candidates.Add(gammaMaxPrice);
        }

        double target = NearestTargetInDirection(candidates, spot, dir, safeSigma);
        trade.Target = target;

        double stopBuffer = Math.Max(safeSigma, spot * 0.0025);
        bool flipOnStopSide = gammaFlip is { } gfs && double.IsFinite(gfs) &&
                              (dir < 0 ? gfs > spot : gfs < spot);
        if (flipOnStopSide && gammaFlip is { } flipPx)
        {
            trade.Stop = dir < 0 ? flipPx + 0.5 * stopBuffer : flipPx - 0.5 * stopBuffer;
            trade.Invalidation = $"возврат за gamma-flip {Fmt(flipPx)} — смена режима, идея отменяется";
        }
        else
        {
            trade.Stop = dir < 0 ? spot + stopBuffer : spot - stopBuffer;
            trade.Invalidation = $"закрытие {(dir < 0 ? "выше" : "ниже")} {Fmt(trade.Stop.Value)}";
        }

        trade.Headline = $"{(side == TradeSide.Short ? "ШОРТ" : "ЛОНГ")} от {Fmt(spot)} → цель {Fmt(target)}";

        // Ярлык гаммы в тексте — из того же трёхзначного режима, что и бейдж «Режим»
        // (с нейтральной полосой), чтобы карточка и бейдж не противоречили.
        string gammaWord = rec.Regime switch
        {
            VolatilityRegime.PositiveGamma => "+гамма",
            VolatilityRegime.NegativeGamma => "−гамма",
            _ => "гамма≈0"
        };
        string flipPart = gammaFlip is { } f3 && double.IsFinite(f3)
            ? $"спот {Fmt(spot)} {(spot < f3 ? "ниже" : "выше")} gamma-flip {Fmt(f3)} ({gammaWord})"
            : "gamma-flip не определён";

        // Согласие DEX со стороной flip («подтверждает»/«против»); при отсутствии flip — опускаем.
        int flipVote = gammaFlip is { } fv && double.IsFinite(fv) ? (spot < fv ? -1 : spot > fv ? +1 : 0) : 0;
        int dexVote = dexRaw > 0 ? -1 : dexRaw < 0 ? +1 : 0;
        string dexQual = flipVote != 0 && dexVote != 0
            ? (dexVote == flipVote ? " подтверждает" : " против")
            : "";
        string dexPart = dexRaw > 0 ? $"DEX {FmtUsd(dexRaw)} (дилер продаёт){dexQual}"
            : dexRaw < 0 ? $"DEX {FmtUsd(dexRaw)} (дилер покупает){dexQual}"
            : "DEX ≈ 0";
        trade.Reason = $"{flipPart}; {dexPart} → {(side == TradeSide.Short ? "ШОРТ" : "ЛОНГ")}.";

        trade.PlanB = gammaFlip is { } f4 && double.IsFinite(f4)
            ? $"Закрепление за gamma-flip {Fmt(f4)} — смена режима, разворот к {(side == TradeSide.Short ? "лонгу" : "шорту")}."
            : "Смена знака дельта-экспозиции — пересмотр направления.";

        trade.RiskReward = ComputeRiskReward(trade);
        return trade;
    }

    /// <summary>
    /// Ближайший к споту кандидат-цель строго в сторону <paramref name="dir"/> (исключая уровни
    /// ближе 0.1σ к споту). Фолбэк — измеренный ход спот + dir·max(σ, 0.5%·спот).
    /// </summary>
    private static double NearestTargetInDirection(IReadOnlyList<double> candidates, double spot, int dir, double safeSigma)
    {
        double eps = Math.Max(spot * 0.0005, 1e-6);
        double minDist = 0.1 * safeSigma;
        double best = 0, bestDist = double.MaxValue;

        foreach (double p in candidates)
        {
            if (p <= 0 || !double.IsFinite(p)) continue;
            bool onSide = dir > 0 ? p > spot + eps : p < spot - eps;
            if (!onSide) continue;
            double d = Math.Abs(p - spot);
            if (d < minDist) continue;
            if (d < bestDist) { bestDist = d; best = p; }
        }

        if (best <= 0 || !double.IsFinite(best))
            best = spot + dir * Math.Max(safeSigma > 0 ? safeSigma : 0, spot * 0.005);

        return best;
    }

    /// <summary>Заглушка «ВНЕ РЫНКА» для вырожденных случаев (нет данных/спота) — чтобы вёрстка
    /// отрисовала честную ветку, а не дефолтную направленную сделку (FadeRange/Long).</summary>
    private static PrimaryTrade StandAsidePrimary(string reason) => new()
    {
        Action = TradeAction.StandAside,
        Side = TradeSide.None,
        Headline = "ВНЕ РЫНКА",
        Reason = reason,
        Setup = "План появится при корректном снимке с открытым интересом и ценой базового актива.",
        Invalidation = "—"
    };

    /// <summary>Ярлык конвикции: ≥60 Высокая, 35..59 Средняя, иначе Низкая.</summary>
    private static string ConvictionLabel(int conviction)
        => conviction >= 60 ? "Высокая" : conviction >= 35 ? "Средняя" : "Низкая";

    /// <summary>Топ-N драйверов по модулю вклада в bias (короткой строкой).</summary>
    private static List<string> TopDrivers(IReadOnlyList<BiasComponent> components, int n)
        => components
            .Where(c => Math.Abs(c.Contribution) > 1e-6)
            .OrderByDescending(c => Math.Abs(c.Contribution))
            .Take(n)
            .Select(c => $"{c.Name}: {(c.Normalized >= 0 ? "вверх" : "вниз")} (вклад {c.Contribution:+0.00;-0.00})")
            .ToList();

    /// <summary>R:R = |цель−середина входа| / |середина входа−стоп|; null при неполных данных.</summary>
    private static double? ComputeRiskReward(PrimaryTrade t)
    {
        if (t.EntryLow is not { } lo || t.EntryHigh is not { } hi
            || t.Target is not { } tgt || t.Stop is not { } stop)
            return null;

        double entry = (lo + hi) / 2;
        double reward = Math.Abs(tgt - entry);
        double risk = Math.Abs(entry - stop);
        if (risk <= 0 || !double.IsFinite(reward) || !double.IsFinite(risk))
            return null;

        double rr = reward / risk;
        return double.IsFinite(rr) ? Math.Round(rr, 2) : null;
    }

    private List<PriceLevel> BuildLevels(
        SessionRecommendation rec, double spot,
        OptionData? callWall, OptionData? putWall,
        double maxPain, double centroidNear, double? gammaFlip)
    {
        var levels = new List<PriceLevel>();

        void Add(LevelKind kind, string label, double price, string role, double? oi = null)
        {
            if (price <= 0 || !double.IsFinite(price))
                return;
            levels.Add(new PriceLevel
            {
                Kind = kind,
                Label = label,
                Price = price,
                Role = role,
                OpenInterest = oi,
                DistancePercent = (price - spot) / spot * 100.0
            });
        }

        Add(LevelKind.Spot, "Спот", spot, "Спот");

        if (callWall is not null)
            Add(LevelKind.CallWall, $"CALL-стена {Fmt(callWall.Strike)}", callWall.Strike, "Сопротивление", callWall.CallOi);

        if (putWall is not null)
            Add(LevelKind.PutWall, $"PUT-стена {Fmt(putWall.Strike)}", putWall.Strike, "Поддержка", putWall.PutOi);

        Add(LevelKind.MaxPain, $"Max Pain {Fmt(maxPain)}", maxPain, "Магнит");
        Add(LevelKind.GravityEquilibrium, $"Центр тяжести {Fmt(centroidNear)}", centroidNear, "Магнит");

        if (gammaFlip is { } flip)
            Add(LevelKind.GammaFlip, $"Gamma-flip {Fmt(flip)}", flip, "Пивот");

        Add(LevelKind.Sigma1Up, $"+1σ {Fmt(rec.Range.Upper1)}", rec.Range.Upper1, "Граница");
        Add(LevelKind.Sigma1Down, $"−1σ {Fmt(rec.Range.Lower1)}", rec.Range.Lower1, "Граница");
        Add(LevelKind.Sigma2Up, $"+2σ {Fmt(rec.Range.Upper2)}", rec.Range.Upper2, "Граница");
        Add(LevelKind.Sigma2Down, $"−2σ {Fmt(rec.Range.Lower2)}", rec.Range.Lower2, "Граница");

        // Сортировка по Price убыв.
        levels = levels.OrderByDescending(l => l.Price).ToList();

        // Дедуп близких (<0.1% от спота): оставляем значимый по приоритету роли.
        double tol = spot * (LevelDedupPercent / 100.0);
        var deduped = new List<PriceLevel>();
        foreach (PriceLevel lvl in levels)
        {
            PriceLevel? near = deduped.FirstOrDefault(d => Math.Abs(d.Price - lvl.Price) <= tol);
            if (near is null)
            {
                deduped.Add(lvl);
            }
            else if (LevelPriority(lvl.Kind) < LevelPriority(near.Kind))
            {
                // более значимый уровень вытесняет менее значимый на той же цене
                deduped[deduped.IndexOf(near)] = lvl;
            }
        }

        return deduped;
    }

    /// <summary>Приоритет уровня при дедупликации (меньше — значимее).</summary>
    private static int LevelPriority(LevelKind kind) => kind switch
    {
        LevelKind.Spot => 0,
        LevelKind.CallWall => 1,
        LevelKind.PutWall => 1,
        LevelKind.MaxPain => 2,
        LevelKind.GravityEquilibrium => 3,
        LevelKind.GammaFlip => 4,
        _ => 5 // σ-границы
    };

    private static DirectionBias MapBias(double b) => b switch
    {
        >= 0.45 => DirectionBias.StrongUp,
        >= 0.15 => DirectionBias.ModerateUp,
        > -0.15 => DirectionBias.Neutral,
        > -0.45 => DirectionBias.ModerateDown,
        _ => DirectionBias.StrongDown
    };

    private static string Fmt(double v)
        => double.IsFinite(v) ? v.ToString("#,##0.##", CultureInfo.InvariantCulture) : "—";

    private static string FmtUsd(double v)
        => double.IsFinite(v) ? "$" + v.ToString("#,##0", CultureInfo.InvariantCulture) : "—";
}
