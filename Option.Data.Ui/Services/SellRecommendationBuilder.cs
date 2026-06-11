using Option.Data.Shared.Dto;
using Option.Data.Ui.Models;

namespace Option.Data.Ui.Services;

/// <summary>
/// Рекомендация непокрытой продажи (спека 2026-06-11-sell-premium-recommendation-design.md):
/// (1) режим Net GEX доски (та же механика, что Trade) → допустимость продажи и параметры
/// профиля; (2) σ_phys = EWMA-RV × множитель режима (клэмп к ATM IV) — прогноз ФИЗИЧЕСКОЙ
/// волы (в +гамме RV систематически ниже IV — там эдж продавца); (3) эдж кандидата
/// EV = bid − Black76(σ_phys); (4) жёсткие фильтры (ликвидность/дельта/P(касания)/стена/σ-граница)
/// → ранжирование по EV/маржа; (5) нога против bias (та же конструкция сигналов, что Trade);
/// (6) план Б с конкретными уровнями. Все пороги «КАЛИБРУЕМО» проверяются sellbacktest'ом.
/// </summary>
public class SellRecommendationBuilder : ISellRecommendationBuilder
{
    // ---------- Режим (как в SessionRecommendationBuilder). КАЛИБРУЕМО. ----------
    private const double NeutralGexProfileFraction = 0.05;

    // ---------- Сигналы направления — зеркало Trade (бэктест IC 2026-06-10). ----------
    private const double WeightStructure = 0.45;
    private const double WeightFlow = 0.35;
    private const double WeightSkew = 0.20;
    private const double SkewScaleVolPts = 8.0;
    private const double FlowEpsilon = 0.05;
    private const double StaticDexScale = 0.10;
    private const double StaticDexDamp = 0.5;
    private const double NegGammaFlipDistSigmas = 1.5;

    /// <summary>|bias|, при котором нога продажи диктуется направлением. КАЛИБРУЕМО.</summary>
    private const double LegBiasThreshold = 0.15;

    // ---------- σ_phys. ----------
    // Walk-forward sellbacktest (GRID 2026-06-11, год BTC/ETH): устойчивого эджа OOS НЕ выявлено
    // (BTC H2 −0.92%/маржу, ETH H1 — 0 допустимых конфигураций; медвежий год, продажа путов
    // убыточна на падении). Дефолты оставлены как теоретически обоснованные (RV(+гамма) < IV);
    // частота жёсткого триггера ≈ предсказанной P(касания) — вероятностная модель калибрована.
    // Пересмотр — после накопления собственной (не ретро-урезанной CleanupJob) истории фронтов.
    private const double PhysVolMultPositiveGamma = 0.85;
    private const double PhysVolMultNeutral = 1.0;
    private const double PhysVolMultNegativeGamma = 1.25;
    private const double PhysVolMinAtmFraction = 0.5;
    private const double PhysVolMaxAtmFraction = 1.5;
    private const double EwmaHalfLifeDays = 7.0;

    // ---------- Фильтры кандидатов. КАЛИБРУЕМО. ----------
    /// <summary>Минимальная премия (бид) как доля форварда: отсев грошовых хвостов.</summary>
    private const double MinBidUsdFractionOfForward = 0.0003;
    /// <summary>Минимальный DTE, дней: короче — пин-риск, карточки не публикуются.</summary>
    private const double MinDteDays = 0.5;

    // ---------- Профиль «Адаптивный». КАЛИБРУЕМО. ----------
    private const double AdaptDeltaMinPosGamma = 0.08, AdaptDeltaMaxPosGamma = 0.20;
    private const double AdaptBandSigmasPosGamma = 1.0;
    private const double AdaptMaxProbTouchPosGamma = 0.25;
    private const double AdaptDeltaMinNeutral = 0.05, AdaptDeltaMaxNeutral = 0.12;
    private const double AdaptBandSigmasNeutral = 1.25;
    private const double AdaptMaxProbTouchNeutral = 0.20;
    private const double StrangleMaxBias = 0.15;
    private const double StrangleMaxCombinedProbTouch = 0.35;

    // ---------- Профиль «Агрессивный». КАЛИБРУЕМО. ----------
    private const double AggrDeltaMin = 0.20, AggrDeltaMax = 0.30;
    private const double AggrMaxProbTouch = 0.45;
    private const double AggrNegGammaDeltaMin = 0.10, AggrNegGammaDeltaMax = 0.15;
    private const double AggrNegGammaBandSigmas = 2.0;

    // ---------- План Б. КАЛИБРУЕМО. ----------
    private const double TakeProfitRemainderFraction = 0.25;
    private const double SoftTriggerPremiumMult = 2.0;
    private const double HardTriggerPremiumMult = 3.0;
    private const double HardTriggerStrikeBufferSessionSigmas = 0.5;

    public SellRecommendation Build(
        OptionBoard board, string currency, string expiration, DateTimeOffset asOf,
        IReadOnlyList<DeltaPoint> dexSeries, IReadOnlyList<PricePoint> priceHistory)
    {
        var rec = new SellRecommendation { Currency = currency, Expiration = expiration, AsOf = asOf };

        if (board is null || board.Chain.Count == 0 || board.Spot <= 0)
            return StandAsideAll(rec, "нет данных доски", "повторите загрузку позже");

        double f = board.Spot;
        double t = OptionExposureMath.YearsToExpiry(expiration, asOf);
        rec.Forward = f;
        rec.TYears = t;
        rec.DteDays = t * 365.0;

        if (rec.DteDays <= 0)
            return StandAsideAll(rec, "экспирация истекла", "выберите следующую экспирацию");

        double atmIv = OptionExposureMath.AtmIvFraction(board.Chain, f);
        if (atmIv <= 0)
            return StandAsideAll(rec, "нет валидной IV на доске", "повторите загрузку позже");
        rec.AtmIvPct = atmIv * 100;

        // --- Греки Black-76 на копии цепочки: live-доска греков не несёт (всегда 0) ---
        List<OptionData> chain = board.Chain.Select(o =>
        {
            double civ = o.Iv > 0 ? o.Iv / 100.0 : 0;
            double piv = o.PutIv > 0 ? o.PutIv / 100.0 : civ;
            return o with
            {
                CallDelta = civ > 0 ? SellMath.Black76Delta(true, f, o.Strike, civ, t) : 0,
                PutDelta = piv > 0 ? SellMath.Black76Delta(false, f, o.Strike, piv, t) : 0,
                CallGamma = SessionAnalysisMath.Black76Gamma(f, o.Strike, civ, t),
                PutGamma = SessionAnalysisMath.Black76Gamma(f, o.Strike, piv, t)
            };
        }).ToList();

        // --- Режим / флип / стены: тот же конвейер, что Trade (одиночная серия) ---
        List<SessionAnalysisMath.GammaStrike> gs = chain
            .Where(o => o.CallOi > 0 || o.PutOi > 0)
            .Select(o => new SessionAnalysisMath.GammaStrike(
                o.Strike, o.CallOi, o.PutOi, o.Iv > 0 ? o.Iv / 100.0 : atmIv, t))
            .OrderBy(s => s.Strike)
            .ToList();

        (double lo2Iv, double up2Iv) = SessionAnalysisMath.LogNormalBand(f, atmIv, t, 2);
        double lowFactor = Math.Min(0.88, Math.Max(0.25, lo2Iv / f * 0.97));
        double highFactor = Math.Max(1.12, up2Iv / f * 1.03);
        List<GammaProfilePoint> profile = SessionAnalysisMath.GammaProfile(gs, f, lowFactor, highFactor);

        rec.NetGexAtSpot = SessionAnalysisMath.NetGexAtPrice(gs, f);
        rec.GammaFlip = SessionAnalysisMath.GammaFlip(profile, f);

        double peak = 0;
        foreach (GammaProfilePoint p in profile)
            peak = Math.Max(peak, Math.Abs(p.NetGex));
        double neutralBand = peak * NeutralGexProfileFraction;
        rec.Regime = rec.NetGexAtSpot > neutralBand ? VolatilityRegime.PositiveGamma
            : rec.NetGexAtSpot < -neutralBand ? VolatilityRegime.NegativeGamma
            : VolatilityRegime.Neutral;

        List<SessionAnalysisMath.StrikeGexBreakdown> breakdown = SessionAnalysisMath.StrikeGexAtPrice(gs, f);
        rec.CallWall = SessionAnalysisMath.GexCallWall(breakdown, f) ?? SessionAnalysisMath.CallWall(chain, f)?.Strike;
        rec.PutWall = SessionAnalysisMath.GexPutWall(breakdown, f) ?? SessionAnalysisMath.PutWall(chain, f)?.Strike;

        // --- σ_phys: EWMA-RV × множитель режима, клэмп к ATM IV ---
        double rv = SellMath.EwmaRealizedVolAnnual(priceHistory, EwmaHalfLifeDays);
        rec.RvAnnualPct = rv * 100;
        double mult = rec.Regime switch
        {
            VolatilityRegime.PositiveGamma => PhysVolMultPositiveGamma,
            VolatilityRegime.NegativeGamma => PhysVolMultNegativeGamma,
            _ => PhysVolMultNeutral
        };
        double sigmaPhys;
        if (rv > 0)
        {
            sigmaPhys = SessionAnalysisMath.Clamp(
                rv * mult, atmIv * PhysVolMinAtmFraction, atmIv * PhysVolMaxAtmFraction);
        }
        else
        {
            sigmaPhys = atmIv * mult;
            rec.Notes.Add("История цены из БД недоступна — σ_phys = ATM IV × множитель режима (без RV).");
        }
        rec.SigmaPhysPct = sigmaPhys * 100;
        rec.SessionSigmaUsd = SessionAnalysisMath.SessionSigmaUsd(f, atmIv, t);
        rec.Band1 = SessionAnalysisMath.LogNormalBand(f, sigmaPhys, t, 1);
        rec.Band2 = SessionAnalysisMath.LogNormalBand(f, sigmaPhys, t, 2);

        // --- Направление (зеркало BuildSignals из Trade) ---
        double dexRaw = OptionExposureMath.DollarDeltaExposure(chain, f);
        double dexFlowTrend = SessionAnalysisMath.DexTrend(dexSeries);
        double? rr25 = OptionExposureMath.RiskReversal25Delta(chain);
        double totalOi = 0;
        foreach (OptionData o in chain)
            totalOi += o.CallOi + o.PutOi;
        BuildSignals(rec, f, dexRaw, dexFlowTrend, rr25, totalOi);

        // --- Уровни (компактная карта) ---
        BuildLevels(rec, chain, breakdown, f, lo2Iv, up2Iv);

        // --- Кандидаты ---
        List<SellCandidate> all = BuildCandidates(rec, chain, f, t, sigmaPhys);
        // Топ-5 каждой ноги (спека §5) — таблица прозрачности.
        rec.TopCandidates = all
            .Where(c => c.EvUsd > 0 && !c.TooSmallPremium)
            .GroupBy(c => c.IsCall)
            .SelectMany(g => g.OrderByDescending(c => c.EvOnMargin).Take(5))
            .OrderByDescending(c => c.EvOnMargin)
            .ToList();

        if (rec.DteDays < MinDteDays)
        {
            string why = $"до экспирации {rec.DteDays * 24:0.#} ч — пин-риск и вырожденная статистика";
            rec.Adaptive = StandAsideCard(SellProfile.Adaptive, why, "выберите следующую экспирацию");
            rec.Aggressive = StandAsideCard(SellProfile.Aggressive, why, "выберите следующую экспирацию");
            return rec;
        }

        rec.Adaptive = BuildAdaptiveCard(rec, all, f, t, sigmaPhys);
        rec.Aggressive = BuildAggressiveCard(rec, all, f, t, sigmaPhys);
        return rec;
    }

    // =====================================================================
    //  Сигналы направления (та же конструкция и веса, что Trade)
    // =====================================================================
    private static void BuildSignals(
        SellRecommendation rec, double spot,
        double dexRaw, double dexFlowTrend, double? rr25, double totalOi)
    {
        double safeSigma = rec.SessionSigmaUsd > 0 && double.IsFinite(rec.SessionSigmaUsd)
            ? rec.SessionSigmaUsd : spot * 0.01;
        var components = new List<BiasComponent>();

        if (rec.Regime == VolatilityRegime.PositiveGamma &&
            rec.CallWall is { } cw && rec.PutWall is { } pw && cw > pw)
        {
            double mid = (cw + pw) / 2.0;
            double halfRange = (cw - pw) / 2.0;
            double x = SessionAnalysisMath.Clamp((spot - mid) / halfRange, -1, 1);
            components.Add(new BiasComponent
            {
                Name = "Структура (+гамма)",
                RawValue = x,
                Normalized = -x,
                Weight = WeightStructure,
                Explanation = $"спот в {(x >= 0 ? "верхней" : "нижней")} половине диапазона стен → возврат к середине."
            });
        }
        else if (rec.Regime == VolatilityRegime.NegativeGamma && rec.GammaFlip is { } flip && double.IsFinite(flip))
        {
            double d = (spot - flip) / safeSigma;
            components.Add(new BiasComponent
            {
                Name = "Структура (−гамма)",
                RawValue = spot - flip,
                Normalized = Math.Tanh(d / NegGammaFlipDistSigmas),
                Weight = WeightStructure,
                Explanation = $"спот {(spot < flip ? "ниже" : "выше")} gamma-flip в −гамме → хедж усиливает движение."
            });
        }

        if (Math.Abs(dexFlowTrend) >= FlowEpsilon)
        {
            // Знак эмпирический (контрарный) — как в Trade, бэктест IC 2026-06-10.
            components.Add(new BiasComponent
            {
                Name = "Поток ΔDEX",
                RawValue = dexFlowTrend,
                Normalized = SessionAnalysisMath.Clamp(dexFlowTrend, -1, 1),
                Weight = WeightFlow,
                Explanation = dexFlowTrend > 0
                    ? "очищенный DEX растёт — накопление защиты (контрарно: вверх)."
                    : "очищенный DEX падает — разгрузка защиты (контрарно: вниз)."
            });
        }
        else if (dexRaw != 0 && totalOi > 0 && double.IsFinite(totalOi))
        {
            double dexNorm = SessionAnalysisMath.Clamp(Math.Abs(dexRaw) / (spot * totalOi), 0, 1);
            double vote = -(dexRaw > 0 ? 1 : -1) * StaticDexDamp * Math.Tanh(dexNorm / StaticDexScale);
            components.Add(new BiasComponent
            {
                Name = "DEX (статический)",
                RawValue = dexRaw,
                Normalized = vote,
                Weight = WeightFlow,
                Explanation = $"поток ΔDEX недоступен/слабый; уровень DEX — ослабленный сигнал {(vote >= 0 ? "вверх" : "вниз")}."
            });
        }

        if (rr25 is { } rr && double.IsFinite(rr))
        {
            components.Add(new BiasComponent
            {
                Name = "Скос 25Δ RR",
                RawValue = rr,
                Normalized = -Math.Tanh(rr / SkewScaleVolPts),
                Weight = WeightSkew,
                Explanation = rr > 0 ? "путы дороже (страх) → вниз." : rr < 0 ? "коллы дороже (жадность) → вверх." : "скос плоский."
            });
        }

        double weightSum = components.Sum(c => c.Weight);
        double score = 0;
        if (weightSum > 0)
        {
            foreach (BiasComponent c in components)
            {
                c.Contribution = c.Normalized * c.Weight / weightSum;
                score += c.Contribution;
            }
        }

        rec.BiasScore = SessionAnalysisMath.Clamp(score, -1, 1);
        rec.BiasComponents = components;
    }

    // =====================================================================
    //  Уровни
    // =====================================================================
    private static void BuildLevels(
        SellRecommendation rec, List<OptionData> chain,
        List<SessionAnalysisMath.StrikeGexBreakdown> breakdown, double f, double lo2Iv, double up2Iv)
    {
        var levels = new List<SellLevel> { new("Спот/форвард", f) };
        if (rec.CallWall is { } cw)
            levels.Add(new SellLevel("CALL-стена (GEX)", cw));
        if (rec.PutWall is { } pw)
            levels.Add(new SellLevel("PUT-стена (GEX)", pw));
        if (rec.GammaFlip is { } gf)
            levels.Add(new SellLevel("Gamma Flip", gf));
        if (SessionAnalysisMath.PeakGexStrike(breakdown, lo2Iv, up2Iv) is { } pin)
            levels.Add(new SellLevel("Пик гаммы (пин)", pin));
        levels.Add(new SellLevel("Max Pain", OptionExposureMath.MaxPain(chain)));
        levels.Add(new SellLevel("+2σ_phys", rec.Band2.Upper));
        levels.Add(new SellLevel("+1σ_phys", rec.Band1.Upper));
        levels.Add(new SellLevel("−1σ_phys", rec.Band1.Lower));
        levels.Add(new SellLevel("−2σ_phys", rec.Band2.Lower));

        foreach (SellLevel l in levels)
            l.DistancePercent = (l.Price - f) / f * 100;
        rec.Levels = levels.OrderByDescending(l => l.Price).ToList();
    }

    // =====================================================================
    //  Кандидаты
    // =====================================================================
    private static List<SellCandidate> BuildCandidates(
        SellRecommendation rec, List<OptionData> chain, double f, double t, double sigmaPhys)
    {
        var result = new List<SellCandidate>();
        double minBidUsd = f * MinBidUsdFractionOfForward;

        foreach (OptionData o in chain)
        {
            if (o.Strike > f)
                TryAdd(true, o);
            else if (o.Strike < f)
                TryAdd(false, o);
        }

        return result;

        void TryAdd(bool isCall, OptionData o)
        {
            double oi = isCall ? o.CallOi : o.PutOi;
            double bidUsd = isCall ? o.CallBid : o.PutBid;
            double markUsd = isCall ? o.CallPrice : o.PutPrice;
            double ivPct = isCall ? o.Iv : (o.PutIv > 0 ? o.PutIv : o.Iv);
            double sigma = ivPct / 100.0;
            if (oi <= 0 || bidUsd <= 0 || sigma <= 0 || markUsd <= 0)
                return;

            double markCoin = markUsd / f;
            double marginCoin = SellMath.ShortMarginCoin(isCall, f, o.Strike, markCoin);
            double marginUsd = marginCoin * f;
            if (marginUsd <= 0)
                return;

            double ev = bidUsd - SellMath.Black76Price(isCall, f, o.Strike, sigmaPhys, t);

            result.Add(new SellCandidate
            {
                IsCall = isCall,
                Strike = o.Strike,
                Instrument = $"{rec.Currency}-{rec.Expiration}-{(int)Math.Round(o.Strike)}-{(isCall ? "C" : "P")}",
                BidUsd = bidUsd,
                BidCoin = bidUsd / f,
                MarkUsd = markUsd,
                MarketIvPct = ivPct,
                Delta = isCall ? o.CallDelta : o.PutDelta,
                ProbItm = SellMath.ProbItm(isCall, f, o.Strike, sigma, t),
                ProbTouch = SellMath.ProbTouch(isCall, f, o.Strike, sigma, t),
                TheoPhysUsd = bidUsd - ev,
                EvUsd = ev,
                MarginCoin = marginCoin,
                MarginUsd = marginUsd,
                EvOnMargin = ev / marginUsd,
                YieldOnMargin = bidUsd / marginUsd,
                YieldAnnualizedPct = rec.DteDays > 0 ? bidUsd / marginUsd * 365.0 / rec.DteDays * 100.0 : 0,
                ThetaPerDayUsd = SellMath.ThetaPerDayUsd(f, o.Strike, sigma, t),
                VegaPerVolPointUsd = SellMath.VegaPerVolPointUsd(f, o.Strike, sigma, t),
                BehindWall = isCall
                    ? rec.CallWall is { } cw && o.Strike >= cw
                    : rec.PutWall is { } pw && o.Strike <= pw,
                TooSmallPremium = bidUsd < minBidUsd
            });
        }
    }

    // =====================================================================
    //  Карточки профилей
    // =====================================================================
    private SellCard BuildAdaptiveCard(
        SellRecommendation rec, List<SellCandidate> all, double f, double t, double sigmaPhys)
    {
        if (rec.Regime == VolatilityRegime.NegativeGamma)
        {
            return StandAsideCard(SellProfile.Adaptive,
                "режим −гамма: дилеры усиливают движения, исторически RV > IV — премия продавца отрицательна",
                "возврат Net GEX у спота в нейтральную полосу или выше (режим ≠ −гамма)");
        }

        (double dMin, double dMax, double bandK, double maxTouch) =
            rec.Regime == VolatilityRegime.PositiveGamma
                ? (AdaptDeltaMinPosGamma, AdaptDeltaMaxPosGamma, AdaptBandSigmasPosGamma, AdaptMaxProbTouchPosGamma)
                : (AdaptDeltaMinNeutral, AdaptDeltaMaxNeutral, AdaptBandSigmasNeutral, AdaptMaxProbTouchNeutral);

        (double loBand, double upBand) = SessionAnalysisMath.LogNormalBand(f, sigmaPhys, t, bandK);

        List<SellCandidate> passed = all.Where(c =>
            !c.TooSmallPremium &&
            c.EvUsd > 0 &&
            Math.Abs(c.Delta) >= dMin && Math.Abs(c.Delta) <= dMax &&
            c.ProbTouch <= maxTouch &&
            c.BehindWall &&
            (c.IsCall ? c.Strike >= upBand : c.Strike <= loBand)).ToList();

        SellCard card = PickLeg(SellProfile.Adaptive, rec, passed);
        if (card.IsStandAside)
            return card;

        // Стрэнгл: +гамма, нейтральный bias, обе ноги прошли фильтры, суммарная P(касания) в допуске.
        if (rec.Regime == VolatilityRegime.PositiveGamma && Math.Abs(rec.BiasScore) < StrangleMaxBias)
        {
            SellCandidate? bestCall = passed.Where(c => c.IsCall).MaxBy(c => c.EvOnMargin);
            SellCandidate? bestPut = passed.Where(c => !c.IsCall).MaxBy(c => c.EvOnMargin);
            if (bestCall is not null && bestPut is not null)
            {
                double combined = Math.Min(1, bestCall.ProbTouch + bestPut.ProbTouch);
                if (combined <= StrangleMaxCombinedProbTouch)
                {
                    card.Leg = SellLeg.Strangle;
                    card.Primary = bestCall;
                    card.SecondLeg = bestPut;
                    card.CombinedProbTouch = combined;
                }
            }
        }

        FinishCard(card, rec, all, f);
        return card;
    }

    private SellCard BuildAggressiveCard(
        SellRecommendation rec, List<SellCandidate> all, double f, double t, double sigmaPhys)
    {
        double dMin = AggrDeltaMin, dMax = AggrDeltaMax;
        double bandK = 0;
        bool requireOppositeBias = false;
        var warnings = new List<string>();

        if (rec.Regime == VolatilityRegime.NegativeGamma)
        {
            if (Math.Abs(rec.BiasScore) < LegBiasThreshold)
            {
                return StandAsideCard(SellProfile.Aggressive,
                    "−гамма без выраженного направления: продажа премии в режиме разгона без защиты направлением",
                    $"появление |bias| ≥ {LegBiasThreshold:0.00} либо выход из −гаммы");
            }
            dMin = AggrNegGammaDeltaMin;
            dMax = AggrNegGammaDeltaMax;
            bandK = AggrNegGammaBandSigmas;
            requireOppositeBias = true;
            warnings.Add("ПРОДАЖА В РЕЖИМЕ РАЗГОНА ВОЛАТИЛЬНОСТИ (−гамма): только против bias, дальние страйки, повышенный риск.");
        }

        (double loBand, double upBand) = bandK > 0
            ? SessionAnalysisMath.LogNormalBand(f, sigmaPhys, t, bandK)
            : (0d, double.MaxValue);

        List<SellCandidate> passed = all.Where(c =>
            !c.TooSmallPremium &&
            c.EvUsd > 0 &&
            Math.Abs(c.Delta) >= dMin && Math.Abs(c.Delta) <= dMax &&
            c.ProbTouch <= AggrMaxProbTouch &&
            (bandK <= 0 || (c.IsCall ? c.Strike >= upBand : c.Strike <= loBand))).ToList();

        if (requireOppositeBias)
        {
            // bias ≤ −порог (вниз) → продаём CALL; bias ≥ +порог (вверх) → продаём PUT.
            passed = passed.Where(c => c.IsCall == rec.BiasScore <= 0).ToList();
        }

        SellCard card = PickLeg(SellProfile.Aggressive, rec, passed);
        if (card.IsStandAside)
            return card;

        foreach (string w in warnings)
            card.Warnings.Add(w);
        if (card.Primary is { BehindWall: false })
            card.Warnings.Add("Страйк ВНУТРИ GEX-стены: ближе зоны, которую защищают дилеры, — риск касания выше модельного.");

        FinishCard(card, rec, all, f);
        return card;
    }

    private static SellCard PickLeg(SellProfile profile, SellRecommendation rec, List<SellCandidate> passed)
    {
        if (passed.Count == 0)
        {
            return StandAsideCard(profile,
                "ни один страйк не прошёл фильтры (ликвидность / дельта / P(касания) / эдж / структура)",
                "изменение IV, OI или режима; проверьте соседнюю экспирацию");
        }

        bool? sellCall = rec.BiasScore <= -LegBiasThreshold ? true
            : rec.BiasScore >= LegBiasThreshold ? false
            : null;

        SellCandidate? primary;
        if (sellCall is { } sc)
        {
            primary = passed.Where(c => c.IsCall == sc).MaxBy(c => c.EvOnMargin);
            if (primary is null)
            {
                return StandAsideCard(profile,
                    $"направление требует продажи {(sc ? "CALL" : "PUT")}, но на этой стороне нет кандидата, прошедшего фильтры",
                    "смягчение bias или появление эджа на нужной стороне");
            }
        }
        else
        {
            primary = passed.MaxBy(c => c.EvOnMargin)!;
        }

        return new SellCard
        {
            Profile = profile,
            Leg = primary.IsCall ? SellLeg.Call : SellLeg.Put,
            Primary = primary
        };
    }

    /// <summary>Экономика карточки, план Б (4 триггера с уровнями) и стресс-таблица.</summary>
    private void FinishCard(SellCard card, SellRecommendation rec, List<SellCandidate> all, double f)
    {
        SellCandidate p = card.Primary!;
        SellCandidate? s = card.SecondLeg;

        card.TotalPremiumUsd = p.BidUsd + (s?.BidUsd ?? 0);
        card.TotalPremiumCoin = p.BidCoin + (s?.BidCoin ?? 0);
        card.TotalMarginCoin = s is null
            ? p.MarginCoin
            : SellMath.StrangleMarginCoin(p.MarginCoin, s.MarginCoin, p.MarkUsd / f, s.MarkUsd / f);
        card.TotalMarginUsd = card.TotalMarginCoin * f;
        card.YieldOnMarginPct = card.TotalMarginUsd > 0 ? card.TotalPremiumUsd / card.TotalMarginUsd * 100 : 0;
        card.YieldAnnualizedPct = rec.DteDays > 0 ? card.YieldOnMarginPct * 365.0 / rec.DteDays : 0;
        card.TotalEvUsd = p.EvUsd + (s?.EvUsd ?? 0);
        if (card.CombinedProbTouch <= 0)
            card.CombinedProbTouch = s is null ? p.ProbTouch : Math.Min(1, p.ProbTouch + s.ProbTouch);

        // --- План Б ---
        var planB = new List<PlanBTrigger>
        {
            new()
            {
                Name = "Тейк-профит",
                Condition = $"остаток премии ≤ {TakeProfitRemainderFraction:P0} собранной " +
                            $"(≤ {card.TotalPremiumUsd * TakeProfitRemainderFraction:N0} $) и DTE > 1",
                Action = "откупить полностью — хвост риска за копейки не держим"
            }
        };
        AddLegTriggers(planB, rec, all, p);
        if (s is not null)
            AddLegTriggers(planB, rec, all, s);
        planB.Add(new PlanBTrigger
        {
            Name = "Режимный выход",
            Condition = "Net GEX у спота ушёл в −гамму (ниже −нейтральной полосы профиля)",
            Action = "сократить/закрыть досрочно: режим разгона исторически даёт RV ~1.5× выше — выходим по причине, а не по боли"
        });
        card.PlanB = planB;

        // --- Стресс-таблица: PnL продавца при цене экспирации на ±1σ/±2σ_phys ---
        var stress = new List<StressPoint>();
        foreach ((string label, double price) in new[]
                 {
                     ("−2σ", rec.Band2.Lower), ("−1σ", rec.Band1.Lower), ("спот", f),
                     ("+1σ", rec.Band1.Upper), ("+2σ", rec.Band2.Upper)
                 })
        {
            double intrinsic = LegIntrinsic(p, price) + (s is null ? 0 : LegIntrinsic(s, price));
            stress.Add(new StressPoint { Label = label, Price = price, PnlUsd = card.TotalPremiumUsd - intrinsic });
        }
        card.Stress = stress;

        static double LegIntrinsic(SellCandidate leg, double priceAtExpiry)
            => leg.IsCall ? Math.Max(0, priceAtExpiry - leg.Strike) : Math.Max(0, leg.Strike - priceAtExpiry);
    }

    private void AddLegTriggers(
        List<PlanBTrigger> planB, SellRecommendation rec, List<SellCandidate> all, SellCandidate leg)
    {
        string legName = leg.IsCall ? "CALL" : "PUT";
        double sess = rec.SessionSigmaUsd;

        double soft = leg.IsCall ? (rec.CallWall ?? rec.Band1.Upper) : (rec.PutWall ?? rec.Band1.Lower);
        double? roll = all
            .Where(c => c.IsCall == leg.IsCall && c.EvUsd > 0 && c.BehindWall &&
                        (leg.IsCall ? c.Strike > leg.Strike : c.Strike < leg.Strike))
            .OrderBy(c => Math.Abs(c.Strike - leg.Strike))
            .FirstOrDefault()?.Strike;
        planB.Add(new PlanBTrigger
        {
            Name = $"Мягкий ({legName} {leg.Strike:N0})",
            Level = soft,
            Condition = $"спот {(leg.IsCall ? "выше" : "ниже")} {soft:N0} ИЛИ премия ноги ≥ {SoftTriggerPremiumMult:0}× собранной",
            Action = $"откупить половину или ролл на {(roll is { } r ? r.ToString("N0") : "следующий страйк за стеной")}"
        });

        double hard = leg.IsCall
            ? leg.Strike - HardTriggerStrikeBufferSessionSigmas * sess
            : leg.Strike + HardTriggerStrikeBufferSessionSigmas * sess;
        planB.Add(new PlanBTrigger
        {
            Name = $"Жёсткий ({legName} {leg.Strike:N0})",
            Level = hard,
            Condition = $"спот достиг {hard:N0} (страйк {(leg.IsCall ? "−" : "+")}{HardTriggerStrikeBufferSessionSigmas:0.0}σ сессии) " +
                        $"ИЛИ премия ≥ {HardTriggerPremiumMult:0}× собранной",
            Action = $"дельта-хедж фьючерсом ≈ {Math.Abs(leg.Delta):0.00} монеты на контракт ИЛИ полный откуп"
        });
    }

    // =====================================================================
    //  StandAside-помощники
    // =====================================================================
    private static SellCard StandAsideCard(SellProfile profile, string reason, string returnCondition) => new()
    {
        Profile = profile,
        IsStandAside = true,
        StandAsideReason = reason,
        ReturnCondition = returnCondition
    };

    private static SellRecommendation StandAsideAll(SellRecommendation rec, string reason, string returnCondition)
    {
        rec.Adaptive = StandAsideCard(SellProfile.Adaptive, reason, returnCondition);
        rec.Aggressive = StandAsideCard(SellProfile.Aggressive, reason, returnCondition);
        return rec;
    }
}
