using System.Globalization;
using Option.Data.Shared.Dto;
using Option.Data.Ui.Models;

namespace Option.Data.Ui.Services;

/// <summary>
/// Синтезирует торговый план Trade (<see cref="SessionRecommendation"/>).
///
/// МЕТОДОЛОГИЯ (дилерская модель GEX, переработка):
/// <list type="number">
/// <item><b>Режим — первичен.</b> Знак Net GEX у спота задаёт ПОВЕДЕНИЕ цены, а не направление:
/// +гамма — дилеры гасят волатильность (диапазон, возврат к магнитам), −гамма — усиливают
/// (импульс, пробои). Gamma-flip — пивот смены режима, НЕ сигнал лонг/шорт сам по себе.</item>
/// <item><b>Тип сделки выбирается из режима:</b> +гамма → фейд диапазона между GEX-стенами
/// к магниту (пин-страйк/flip/Max Pain у экспирации); −гамма → импульс по согласованным
/// сигналам; нейтральная гамма → вне рынка (кроме сильного согласования сигналов).</item>
/// <item><b>Направление — взвешенная сумма прозрачных сигналов</b> (<see cref="BiasComponent"/>):
/// структура/положение (вес 0.45), поток ΔDEX по истории снимков (0.35), скос 25Δ RR (0.20).
/// Непрозрачный индекс «конвикции» удалён — вместо него таблица сигналов на странице.</item>
/// <item><b>Размер зон — в σ СЕССИИ</b> (S·σ_ATM·√(1дн)), а не в σ до экспирации: на дальних
/// горизонтах прежний подход давал стопы/входы в разы шире суточного хода.</item>
/// <item><b>R:R — фильтр, а не справка:</b> цель подбирается ближайшей, дающей R:R не ниже
/// порога типа сделки (fade ≥ 1.3, breakout ≥ 1.5, directional ≥ 1.4); если структура
/// не даёт такой геометрии — план не публикуется (StandAside/отложенный вход у края).</item>
/// <item><b>Стены — GEX-взвешенные</b> (γ·OI·S²·0.01 по ноге), фолбэк — стены по сырому OI,
/// когда греки/IV недоступны. σ-границы — лог-нормальные (S·e^{∓kσ√T}).</item>
/// </list>
///
/// Соглашения согласованы с <see cref="OptionExposureMath"/> и <see cref="SessionAnalysisMath"/>:
/// σ передаётся долей (mark_iv/100); DEX — долларовый дельта-нотионал; Net GEX — USD на 1%.
/// </summary>
public class SessionRecommendationBuilder : ISessionRecommendationBuilder
{
    // ---------- Режим ----------

    /// <summary>Порог «≈0» для Net GEX у спота при определении режима: доля от пикового |NetGex| профиля. КАЛИБРУЕМО.</summary>
    private const double NeutralGexProfileFraction = 0.05;

    // ---------- Сигналы направления (веса нормируются по доступным). КАЛИБРУЕМО. ----------

    /// <summary>Вес структурного сигнала (положение в диапазоне стен / относительно flip).</summary>
    private const double WeightStructure = 0.45;
    /// <summary>Вес потока — тренда DEX по истории снимков.</summary>
    private const double WeightFlow = 0.35;
    /// <summary>Вес скоса 25Δ Risk Reversal.</summary>
    private const double WeightSkew = 0.20;

    // ---------- Сигналы ПАМЯТИ (валидированы IC, знак согласован BTC↔ETH; бэктест 2026-06-14). КАЛИБРУЕМО. ----------
    // Веса НАМЕРЕННО малы: существующие сигналы (особенно структурный на ETH) валидированы торговой
    // симуляцией; память — лёгкий тилт (помогает BTC, где старые сигналы слабы), не разбавляющий их.

    /// <summary>Вес миграции gamma-flip за 24ч (знак +: flip ползёт вверх → бычий). Сильнейший новый предиктор.</summary>
    private const double WeightFlipMigration = 0.18;
    /// <summary>Вес потока OI на стенах (знак −: набор PUT-стены → вверх, CALL-стены → вниз).</summary>
    private const double WeightWallFlow = 0.10;
    /// <summary>Вес тренда Net GEX у спота за 24ч (знак +: рост → бычий).</summary>
    private const double WeightNetGexTrend = 0.04;
    /// <summary>Вес положения в реализованном диапазоне (знак +: моментум, вверху → вверх).</summary>
    private const double WeightRangePosition = 0.04;

    /// <summary>Нормировка сырого WallFlowOi: доля от ΣOI, при которой голос насыщается (tanh). КАЛИБРУЕМО.</summary>
    private const double WallFlowOiScale = 0.03;
    /// <summary>Буфер консервативного входа/стопа за gamma-flip, σ сессии. КАЛИБРУЕМО.</summary>
    private const double ConservativeBufferSigmas = 0.3;
    /// <summary>Динамический сигнал памяти подмешивается в bias только при |голос| ≥ порога: плоская память (flip/стены не двигались) НЕ разбавляет конвикцию. КАЛИБРУЕМО.</summary>
    private const double MemorySignalEpsilon = 0.05;

    /// <summary>Скос (в пунктах волатильности), при котором сигнал скоса насыщается (tanh). КАЛИБРУЕМО.</summary>
    private const double SkewScaleVolPts = 8.0;
    /// <summary>|тренд DEX| ниже порога считается отсутствием потока (берётся статический фолбэк). КАЛИБРУЕМО.</summary>
    private const double FlowEpsilon = 0.05;
    /// <summary>Нормированный |DEX| (доля от спот·ΣOI), при котором статический фолбэк насыщается. КАЛИБРУЕМО.</summary>
    private const double StaticDexScale = 0.10;
    /// <summary>Ослабление статического DEX против наблюдаемого потока (уровень ≠ flow). КАЛИБРУЕМО.</summary>
    private const double StaticDexDamp = 0.5;
    /// <summary>Дистанция от flip (в σ сессии), на которой структурный сигнал −гаммы насыщается. КАЛИБРУЕМО.</summary>
    private const double NegGammaFlipDistSigmas = 1.5;

    // ---------- Геометрия сделок (всё — в σ СЕССИИ). КАЛИБРУЕМО. ----------

    /// <summary>«Близко к стене» для активного фейда.</summary>
    private const double WallProximitySigmas = 0.5;
    /// <summary>Глубина зоны входа фейда от стены внутрь диапазона.</summary>
    private const double FadeEntryDepthSigmas = 0.4;
    /// <summary>Стоп фейда — за стеной.</summary>
    private const double FadeStopSigmas = 0.6;
    /// <summary>
    /// Максимальная дистанция от спота до стены фейда (в σ сессии): дальше отложенный
    /// вход — не план сессии (у дальних экспираций стены лежат в десятках σ). КАЛИБРУЕМО.
    /// </summary>
    private const double FadeMaxWallDistSigmas = 3.0;
    /// <summary>Полуширина зоны входа импульсной/направленной сделки «от рынка».</summary>
    private const double MarketEntryHalfSigmas = 0.2;
    /// <summary>Стоп импульсной сделки без flip-привязки.</summary>
    private const double BreakoutStopSigmas = 0.8;
    /// <summary>Стоп направленной сделки (нейтральная гамма) без flip-привязки.</summary>
    private const double DirectionalStopSigmas = 0.7;
    /// <summary>Буфер стопа за gamma-flip.</summary>
    private const double FlipStopBufferSigmas = 0.3;
    /// <summary>Максимальная дистанция до flip (в σ сессии), при которой стоп ставится за flip.</summary>
    private const double FlipStopMaxDistSigmas = 1.5;

    /// <summary>Минимальная дистанция до цели (в σ сессии) по типам сделок.</summary>
    private const double FadeMinTargetSigmas = 0.75;
    private const double BreakoutMinTargetSigmas = 1.0;
    private const double DirectionalMinTargetSigmas = 0.8;

    /// <summary>Минимальный R:R по типам сделок — план с худшей геометрией не публикуется.</summary>
    private const double FadeMinRR = 1.3;
    private const double BreakoutMinRR = 1.5;
    private const double DirectionalMinRR = 1.4;

    /// <summary>Минимальный |BiasScore| для импульсной сделки в −гамме. КАЛИБРУЕМО.</summary>
    private const double MinBreakoutBias = 0.15;
    /// <summary>Минимальный |BiasScore| для направленной сделки при нейтральной гамме. КАЛИБРУЕМО.</summary>
    private const double MinNeutralDirectionalBias = 0.35;
    /// <summary>|BiasScore|, при котором отложенный фейд ставится по дрейфу, а не к ближней стене. КАЛИБРУЕМО.</summary>
    private const double DriftBiasThreshold = 0.10;

    /// <summary>Max Pain допускается целью только у экспирации (пин-эффект), дней. КАЛИБРУЕМО.</summary>
    private const double MaxPainTargetMaxDte = 3.0;

    /// <summary>Допуск дедупликации уровней по цене, % от спота.</summary>
    private const double LevelDedupPercent = 0.1;

    public SessionRecommendation Build(
        ExpirationAnalysis selected, string currency, DateTimeOffset asOf, double dexFlowTrend = 0,
        ContractMemory? memory = null)
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

        List<SessionAnalysisMath.GammaStrike> gammaStrikes = BuildGammaStrikes(selected, asOf);

        return Assemble(currency, asOf, selected.Expiration, tYears * 365.0, spot,
            gammaStrikes, selected.OptionData, atmIv, sessionIv: atmIv, tYears,
            selected.DollarDeltaExposure, dexFlowTrend, selected.RiskReversal25Delta,
            isAggregated: false, aggregatedCount: 1, memory: memory);
    }

    public SessionRecommendation BuildAggregate(
        IReadOnlyList<ExpirationAnalysis> window, string currency,
        DateTimeOffset asOf, double dexFlowTrend = 0, ContractMemory? memory = null)
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
        List<ExpirationAnalysis> byT = window
            .OrderBy(a => OptionExposureMath.YearsToExpiry(a.Expiration, asOf))
            .ToList();
        ExpirationAnalysis horizon = byT[^1];
        double tYears = OptionExposureMath.YearsToExpiry(horizon.Expiration, asOf);
        double atmIv = OptionExposureMath.AtmIvFraction(horizon.OptionData, horizon.UnderlyingPrice);

        // Профиль Net GEX: GammaStrike каждой экспирации окна (свои T/σ), конкатенация.
        var gammaStrikes = new List<SessionAnalysisMath.GammaStrike>();
        foreach (ExpirationAnalysis a in window)
            gammaStrikes.AddRange(BuildGammaStrikes(a, asOf));

        // Сводная цепочка (ΣOI по страйку) — Max Pain/OI-фолбэк стен/центроид/детали.
        List<OptionData> chain = AggregateChain(window);

        // DEX — сумма по окну; скос — по ближайшей экспирации, где RR рассчитан
        // (сводная цепочка греков не содержит).
        double dexRaw = window.Sum(a => a.DollarDeltaExposure);
        double? rr25 = byT.Select(a => a.RiskReversal25Delta).FirstOrDefault(v => v.HasValue);

        // σ сессии — по ATM IV БЛИЖАЙШЕЙ экспирации окна: суточный масштаб задаёт фронт,
        // а не квартальная (term structure искажал бы зоны входа/стопы).
        ExpirationAnalysis front = byT[0];
        double sessionIv = OptionExposureMath.AtmIvFraction(front.OptionData, front.UnderlyingPrice);

        return Assemble(currency, asOf, horizon.Expiration, tYears * 365.0, spot,
            gammaStrikes, chain, atmIv, sessionIv, tYears, dexRaw, dexFlowTrend, rr25,
            isAggregated: true, aggregatedCount: window.Count, memory: memory);
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
    /// Общее ядро плана: лог-нормальный σ-диапазон + σ сессии, профиль гаммы, режим,
    /// GEX-стены и пин, уровни, прозрачные сигналы направления и сделка по режиму.
    /// <paramref name="atmIv"/> — IV горизонта (σ-границы до экспирации);
    /// <paramref name="sessionIv"/> — IV для σ СЕССИИ (в «Сводке» — ближайшая экспирация окна).
    /// </summary>
    private SessionRecommendation Assemble(
        string currency, DateTimeOffset asOf,
        string expirationLabel, double horizonDte, double spot,
        List<SessionAnalysisMath.GammaStrike> gammaStrikes,
        IReadOnlyList<OptionData> chain,
        double atmIv, double sessionIv, double tYears,
        double dexRaw, double dexFlowTrend, double? rr25,
        bool isAggregated, int aggregatedCount, ContractMemory? memory = null)
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

        if (atmIv <= 0 || tYears <= 0)
        {
            rec.Notes.Add("Нет валидной ATM IV или время до экспирации ≤ 0 — σ-диапазон и план недоступны.");
            rec.Primary = StandAsidePrimary("нет валидной IV/времени до экспирации — план не строится.");
            return rec;
        }

        // σ-границы до горизонта — лог-нормальные (IV горизонта); зоны входа/стопы — в σ СЕССИИ
        // (IV ближайшей экспирации: суточный масштаб задаёт фронт).
        (double lower1, double upper1) = SessionAnalysisMath.LogNormalBand(spot, atmIv, tYears, 1);
        (double lower2, double upper2) = SessionAnalysisMath.LogNormalBand(spot, atmIv, tYears, 2);
        double sessionSigma = SessionAnalysisMath.SessionSigmaUsd(
            spot, sessionIv > 0 ? sessionIv : atmIv, tYears);

        rec.Range = new SessionRange
        {
            AtmIvPercent = atmIv * 100.0,
            HorizonYears = tYears,
            Sigma1Usd = OptionExposureMath.ExpectedMove1Sigma(spot, atmIv, tYears),
            SessionSigmaUsd = sessionSigma,
            Lower1 = lower1,
            Upper1 = upper1,
            Lower2 = lower2,
            Upper2 = upper2
        };

        // Профиль Net GEX: диапазон покрывает 2σ горизонта (минимум ±12%, низ не уже 0.25·спота).
        double lowFactor = Math.Min(0.88, Math.Max(0.25, lower2 / spot * 0.97));
        double highFactor = Math.Max(1.12, upper2 / spot * 1.03);
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

        // GEX-взвешенные стены и пин-страйк; фолбэк — стены по сырому OI.
        List<SessionAnalysisMath.StrikeGexBreakdown> strikeGex =
            SessionAnalysisMath.StrikeGexAtPrice(gammaStrikes, spot);

        double? callWallStrike = SessionAnalysisMath.GexCallWall(strikeGex, spot)
                                 ?? SessionAnalysisMath.CallWall(chain, spot)?.Strike;
        double? putWallStrike = SessionAnalysisMath.GexPutWall(strikeGex, spot)
                                ?? SessionAnalysisMath.PutWall(chain, spot)?.Strike;
        double? pinStrike = SessionAnalysisMath.PeakGexStrike(strikeGex, lower2, upper2);

        double maxPain = OptionExposureMath.MaxPain(chain);
        double totalOi = chain.Sum(o => o.CallOi + o.PutOi);
        double centroid = totalOi > 0
            ? chain.Sum(o => o.Strike * (o.CallOi + o.PutOi)) / totalOi
            : maxPain;
        if (!double.IsFinite(centroid)) centroid = maxPain;

        rec.Levels = BuildLevels(rec, spot, chain, callWallStrike, putWallStrike,
            pinStrike, maxPain, centroid, gammaFlip);

        // Прозрачные сигналы направления → BiasScore/Bias/BiasComponents (вкл. сигналы памяти).
        BuildSignals(rec, spot, sessionSigma, gammaFlip, callWallStrike, putWallStrike,
            dexRaw, dexFlowTrend, rr25, totalOi, memory, profilePeak);

        if (totalOi <= 0)
        {
            rec.Notes.Add("Нулевой суммарный открытый интерес — сделка не строится.");
            rec.Primary = StandAsidePrimary("нулевой открытый интерес — позиционирование дилеров не определено.");
            return rec;
        }

        rec.Primary = rec.Regime switch
        {
            VolatilityRegime.PositiveGamma => BuildFadePlan(rec, spot, sessionSigma,
                callWallStrike, putWallStrike, pinStrike, maxPain, gammaFlip, horizonDte),
            VolatilityRegime.NegativeGamma => BuildBreakoutPlan(rec, spot, sessionSigma,
                callWallStrike, putWallStrike, pinStrike, gammaFlip),
            _ => BuildNeutralPlan(rec, spot, sessionSigma,
                callWallStrike, putWallStrike, pinStrike, gammaFlip)
        };

        // Одиночная серия, истекающая раньше торговой сессии: профиль гаммы/стены этой
        // экспирации перестанут существовать в 08:00 UTC, а стоп/цель — останутся.
        // План не запрещаем (пин-эффекты у экспирации реальны), но предупреждаем в нём самом.
        if (!isAggregated && horizonDte < 1.0 && rec.Primary.Action != TradeAction.StandAside)
        {
            rec.Primary.Reason +=
                $" ВНИМАНИЕ: экспирация через ~{rec.HoursToFrontExpiry:0} ч — карта уровней действует только до неё; закрыть позицию до экспирации.";
        }

        // Память: σ-зависимые поля + аддитивное обогащение плана (не переопределяет движок).
        if (memory is not null)
        {
            rec.Memory = memory;
            if (gammaFlip is { } gf && double.IsFinite(gf) && sessionSigma > 0)
                memory.FlipDepthSigmas = (gf - spot) / sessionSigma;
            EnrichWithMemory(rec, spot, sessionSigma, gammaFlip, memory);
        }

        return rec;
    }

    /// <summary>
    /// Аддитивное обогащение плана памятью: риск-заметка о зоне отскока к flip, опц. консервативный
    /// вход к flip, заметка об уверенности при тонком OI. Движок (Action/Side/Entry/Target/Stop) НЕ
    /// переопределяется — только информирование.
    /// </summary>
    private static void EnrichWithMemory(
        SessionRecommendation rec, double spot, double sessionSigma, double? gammaFlip, ContractMemory memory)
    {
        PrimaryTrade pt = rec.Primary;
        bool impulse = pt.Action is TradeAction.Breakout or TradeAction.Directional;

        if (impulse && pt.Stop is { } stop && gammaFlip is { } flip && double.IsFinite(flip) && sessionSigma > 0)
        {
            bool insideShort = pt.Side == TradeSide.Short && flip > spot && stop < flip;
            bool insideLong = pt.Side == TradeSide.Long && flip < spot && stop > flip;
            memory.StopInsideBounceZone = insideShort || insideLong;

            if (memory.StopInsideBounceZone)
            {
                double depth = Math.Abs(flip - spot) / sessionSigma;
                string side = pt.Side == TradeSide.Short ? "шорт" : "лонг";
                pt.RiskNote = $"Стоп {Fmt(stop)} внутри зоны вероятного отскока к gamma-flip {Fmt(flip)} " +
                              $"({depth.ToString("0.0", CultureInfo.InvariantCulture)}σ): возврат к flip выбьет {side} до возобновления движения.";

                int dir = pt.Side == TradeSide.Short ? -1 : +1;
                double consEntry = flip - dir * ConservativeBufferSigmas * sessionSigma;
                double consStop = flip + dir * ConservativeBufferSigmas * sessionSigma;
                if (pt.Target is { } target && Math.Abs(consStop - consEntry) > 0)
                {
                    pt.ConservativeEntry = consEntry;
                    pt.ConservativeStop = consStop;
                    pt.ConservativeRiskReward = Math.Round(Math.Abs(consEntry - target) / Math.Abs(consStop - consEntry), 2);
                }
            }
        }

        if (memory.IsThin && pt.Action != TradeAction.StandAside)
            pt.ConfidenceNote = $"OI этой серии — {(memory.OiRepresentativeness * 100):0}% от самой ликвидной экспирации: " +
                                "структура GEX менее надёжна, ниже уверенность.";
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

    // =====================================================================
    //  Сигналы направления
    // =====================================================================

    /// <summary>
    /// BiasScore = взвешенная сумма доступных сигналов (веса нормируются):
    /// структура (положение в диапазоне стен в +гамме / относительно flip в −гамме),
    /// поток ΔDEX по истории снимков (фолбэк — ослабленный статический DEX), скос 25Δ RR.
    /// Все компоненты сохраняются в <see cref="SessionRecommendation.BiasComponents"/> для рендера.
    /// </summary>
    private static void BuildSignals(
        SessionRecommendation rec, double spot, double sessionSigma,
        double? gammaFlip, double? callWall, double? putWall,
        double dexRaw, double dexFlowTrend, double? rr25, double totalOi,
        ContractMemory? memory, double profilePeak)
    {
        double safeSigma = sessionSigma > 0 && double.IsFinite(sessionSigma) ? sessionSigma : spot * 0.01;
        var components = new List<BiasComponent>();

        // --- 1. Структура/положение ---
        if (rec.Regime == VolatilityRegime.PositiveGamma &&
            callWall is { } cw && putWall is { } pw && cw > pw)
        {
            // +гамма: возврат к середине диапазона стен. У CALL-стены — вниз, у PUT-стены — вверх.
            double mid = (cw + pw) / 2.0;
            double halfRange = (cw - pw) / 2.0;
            double x = SessionAnalysisMath.Clamp((spot - mid) / halfRange, -1, 1);
            double vote = -x;
            components.Add(new BiasComponent
            {
                Name = "Структура (+гамма)",
                RawValue = x,
                Normalized = vote,
                Weight = WeightStructure,
                Explanation = $"спот {Fmt(spot)} в диапазоне стен {Fmt(pw)}…{Fmt(cw)} " +
                              $"({(x >= 0 ? "верхняя" : "нижняя")} половина) → возврат к середине " +
                              $"{(vote >= 0 ? "вверх" : "вниз")}."
            });
        }
        else if (rec.Regime == VolatilityRegime.NegativeGamma && gammaFlip is { } flip && double.IsFinite(flip))
        {
            // −гамма: хеджирование усиливает движение ОТ flip; сторона относительно flip — продолжение.
            double d = (spot - flip) / safeSigma;
            double vote = Math.Tanh(d / NegGammaFlipDistSigmas);
            components.Add(new BiasComponent
            {
                Name = "Структура (−гамма)",
                RawValue = spot - flip,
                Normalized = vote,
                Weight = WeightStructure,
                Explanation = $"спот {Fmt(spot)} {(spot < flip ? "ниже" : "выше")} gamma-flip {Fmt(flip)} " +
                              $"в −гамме → хеджирование усиливает движение {(vote >= 0 ? "вверх" : "вниз")}."
            });
        }

        // --- 2. Поток (ΔDEX по снимкам); фолбэк — ослабленный статический DEX ---
        if (Math.Abs(dexFlowTrend) >= FlowEpsilon)
        {
            // ЗНАК ЭМПИРИЧЕСКИЙ (бэктест на годе снимков BTC/ETH, 2026-06-10: IC>0 на 6/12/24ч
            // на обеих монетах; дилерская трактовка «DEX↑ → хедж-давление вниз» давала IC<0 во
            // всех ячейках). Рост очищенного от цены DEX = накопление путов/разгрузка коллов —
            // спрос на защиту, контрарно-бычий маркер; падение — разгрузка защиты, медвежий.
            double vote = SessionAnalysisMath.Clamp(dexFlowTrend, -1, 1);
            components.Add(new BiasComponent
            {
                Name = "Поток ΔDEX",
                RawValue = dexFlowTrend,
                Normalized = vote,
                Weight = WeightFlow,
                Explanation = $"очищенный от движения цены поток дельта-экспозиции " +
                              $"{(dexFlowTrend > 0
                                  ? "растёт — накопление защитных позиций (контрарно: вверх)"
                                  : "падает — разгрузка защиты (контрарно: вниз)")}."
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
                Explanation = $"{(dexFlowTrend == 0
                                  ? "поток ΔDEX недоступен (нет истории)"
                                  : $"поток ΔDEX слабый ({dexFlowTrend.ToString("+0.00;-0.00", CultureInfo.InvariantCulture)} — ниже порога)")}; " +
                              $"уровень DEX {FmtUsd(dexRaw)} " +
                              $"({(dexRaw > 0 ? "дилер продаёт" : "дилер покупает")}) — ослабленный сигнал " +
                              $"{(vote >= 0 ? "вверх" : "вниз")}."
            });
        }

        // --- 3. Скос 25Δ RR ---
        if (rr25 is { } rr && double.IsFinite(rr))
        {
            // RR>0 — путы дороже коллов (спрос на защиту) → медвежий фон; RR<0 — наоборот.
            double vote = -Math.Tanh(rr / SkewScaleVolPts);
            components.Add(new BiasComponent
            {
                Name = "Скос 25Δ RR",
                RawValue = rr,
                Normalized = vote,
                Weight = WeightSkew,
                Explanation = $"RR {rr.ToString("+0.0;-0.0", CultureInfo.InvariantCulture)} в.п. — " +
                              $"{(rr > 0 ? "путы дороже (страх)" : rr < 0 ? "коллы дороже (жадность)" : "скос плоский")} " +
                              $"→ {(vote > 0.02 ? "вверх" : vote < -0.02 ? "вниз" : "нейтрально")}."
            });
        }

        // --- 4. Сигналы ПАМЯТИ (история срезов; знаки валидированы IC, согласованы BTC↔ETH) ---
        // НЕ подмешиваются при нейтральной гамме: там нет режимной конвикции, и торговая симуляция
        // показала, что память дестабилизирует валидированные направленные сделки нейтрали (ETH).
        if (memory is not null && rec.Regime != VolatilityRegime.Neutral)
        {
            // Миграция flip (знак +): flip ползёт вверх → цена идёт за ним. Норм. на σ сессии.
            if (memory.FlipMigrationUsd is { } fm && safeSigma > 0)
            {
                double vote = SessionAnalysisMath.Clamp(Math.Tanh(fm / safeSigma), -1, 1);
                if (Math.Abs(vote) >= MemorySignalEpsilon)
                    components.Add(new BiasComponent
                {
                    Name = "Миграция flip",
                    RawValue = fm,
                    Normalized = vote,
                    Weight = WeightFlipMigration,
                    Explanation = $"gamma-flip за 24ч сместился на {fm.ToString("+#,##0;-#,##0", CultureInfo.InvariantCulture)} " +
                                  $"→ {(vote >= 0 ? "вверх" : "вниз")} (цена идёт за flip)."
                });
            }

            // Поток OI на стенах (знак −): набор PUT-стены (поддержка) → вверх, CALL-стены → вниз.
            if (memory.WallFlowOi is { } wf && totalOi > 0)
            {
                double vote = SessionAnalysisMath.Clamp(-Math.Tanh(wf / (WallFlowOiScale * totalOi)), -1, 1);
                if (Math.Abs(vote) >= MemorySignalEpsilon)
                    components.Add(new BiasComponent
                {
                    Name = "Поток OI на стенах",
                    RawValue = wf,
                    Normalized = vote,
                    Weight = WeightWallFlow,
                    Explanation = $"за 24ч набор OI {(wf < 0 ? "на PUT-стене (поддержка крепнет)" : "на CALL-стене (сопротивление крепнет)")} " +
                                  $"→ {(vote >= 0 ? "вверх" : "вниз")}."
                });
            }

            // Тренд Net GEX (знак +): рост Net GEX у спота → стабилизация/поддержка. Норм. на пик профиля.
            if (memory.NetGexChange is { } ng && profilePeak > 0)
            {
                double vote = SessionAnalysisMath.Clamp(Math.Tanh(ng / profilePeak), -1, 1);
                if (Math.Abs(vote) >= MemorySignalEpsilon)
                    components.Add(new BiasComponent
                {
                    Name = "Тренд Net GEX",
                    RawValue = ng,
                    Normalized = vote,
                    Weight = WeightNetGexTrend,
                    Explanation = $"Net GEX у спота за 24ч {(ng >= 0 ? "вырос" : "упал")} → {(vote >= 0 ? "вверх" : "вниз")}."
                });
            }

            // Положение в реализованном диапазоне (знак +): моментум — вверху диапазона → вверх.
            if (memory.HasHistory)
            {
                double vote = SessionAnalysisMath.Clamp(2.0 * memory.RangePosition - 1.0, -1, 1);
                components.Add(new BiasComponent
                {
                    Name = "Позиция в диапазоне",
                    RawValue = memory.RangePosition,
                    Normalized = vote,
                    Weight = WeightRangePosition,
                    Explanation = $"спот в {(memory.RangePosition * 100):0}% реализованного диапазона " +
                                  $"({Fmt(memory.RealizedLow)}…{Fmt(memory.RealizedHigh)}) → {(vote >= 0 ? "вверх" : "вниз")} (моментум)."
                });
            }
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
        rec.Bias = MapBias(rec.BiasScore);
    }

    // =====================================================================
    //  Сделки по режиму
    // =====================================================================

    /// <summary>
    /// +Гамма: фейд диапазона. У стены — активный вход против стены к магниту; в середине —
    /// отложенный вход у того края, к которому указывает дрейф сигналов (или ближнего).
    /// R:R — фильтр (≥ <see cref="FadeMinRR"/>): не проходит у обеих стен → вне рынка.
    /// </summary>
    private PrimaryTrade BuildFadePlan(
        SessionRecommendation rec, double spot, double sessionSigma,
        double? callWallStrike, double? putWallStrike, double? pinStrike,
        double maxPain, double? gammaFlip, double horizonDte)
    {
        double s = sessionSigma;
        double resistance = callWallStrike ?? rec.Range.Upper1;
        double support = putWallStrike ?? rec.Range.Lower1;
        bool resIsWall = callWallStrike.HasValue;
        bool supIsWall = putWallStrike.HasValue;

        if (!(resistance > support) || !double.IsFinite(resistance) || !double.IsFinite(support))
        {
            return StandAsidePrimary("диапазон стен вырожден — фейд не строится.",
                "Сетап появится при формировании GEX-стен по обе стороны спота.");
        }

        double prox = WallProximitySigmas * s;

        PrimaryTrade? plan;
        if (spot >= resistance - prox)
        {
            plan = TryFadeSide(rec, spot, s, TradeSide.Short, resistance, resIsWall,
                support, pinStrike, maxPain, gammaFlip, horizonDte, conditional: false);
        }
        else if (spot <= support + prox)
        {
            plan = TryFadeSide(rec, spot, s, TradeSide.Long, support, supIsWall,
                resistance, pinStrike, maxPain, gammaFlip, horizonDte, conditional: false);
        }
        else
        {
            // Середина диапазона: отложенный вход. Край — по дрейфу сигналов, иначе ближний.
            TradeSide first;
            if (rec.BiasScore <= -DriftBiasThreshold) first = TradeSide.Long;        // дрейф вниз → дойдём до PUT-стены
            else if (rec.BiasScore >= DriftBiasThreshold) first = TradeSide.Short;   // дрейф вверх → до CALL-стены
            else first = resistance - spot < spot - support ? TradeSide.Short : TradeSide.Long;

            plan = first == TradeSide.Short
                ? TryFadeSide(rec, spot, s, TradeSide.Short, resistance, resIsWall,
                    support, pinStrike, maxPain, gammaFlip, horizonDte, conditional: true)
                : TryFadeSide(rec, spot, s, TradeSide.Long, support, supIsWall,
                    resistance, pinStrike, maxPain, gammaFlip, horizonDte, conditional: true);

            plan ??= first == TradeSide.Short
                ? TryFadeSide(rec, spot, s, TradeSide.Long, support, supIsWall,
                    resistance, pinStrike, maxPain, gammaFlip, horizonDte, conditional: true)
                : TryFadeSide(rec, spot, s, TradeSide.Short, resistance, resIsWall,
                    support, pinStrike, maxPain, gammaFlip, horizonDte, conditional: true);
        }

        return plan ?? StandAsidePrimary(
            $"+гамма, но сессионного фейда нет: стены {Fmt(support)}…{Fmt(resistance)} дальше {FadeMaxWallDistSigmas.ToString("0.#", CultureInfo.InvariantCulture)}σ сессии, отрезаны gamma-flip (у стены режим уже −гамма) или R:R ниже {FadeMinRR.ToString("0.0", CultureInfo.InvariantCulture)}.",
            "Сетап появится при подходе цены к стене в пределах +гамма-зоны либо при изменении карты OI.");
    }

    /// <summary>
    /// Одна сторона фейда: вход у стены, стоп за стеной, цель — ближайший магнит внутри
    /// диапазона, проходящий по дистанции и R:R. null — геометрия не проходит фильтр.
    /// </summary>
    private PrimaryTrade? TryFadeSide(
        SessionRecommendation rec, double spot, double s, TradeSide side,
        double wall, bool isWall, double oppositeBoundary,
        double? pinStrike, double maxPain, double? gammaFlip,
        double horizonDte, bool conditional)
    {
        int dir = side == TradeSide.Short ? -1 : +1;

        // Режимная согласованность: фейд осмыслен, только пока ВЕСЬ путь от спота до стены
        // лежит в +гамме. Если gamma-flip находится МЕЖДУ спотом и стеной, то у стены режим
        // уже −гамма: эта же модель там покажет «импульс по движению», и вход от такой стены
        // противоречил бы собственному прогнозу системы (вход в зоне, где дилеры разгоняют
        // движение, а не гасят его).
        if (gammaFlip is { } flipPx && double.IsFinite(flipPx) &&
            (wall - flipPx) * (spot - flipPx) < 0)
            return null;

        // Сессионная достижимость: стена дальше FadeMaxWallDistSigmas σ сессии — не план
        // сессии. У дальних экспираций GEX-стены лежат в десятках σ (напр. −40% от спота),
        // и «отложенный вход у стены» там не имеет операционного смысла.
        if (Math.Abs(wall - spot) > FadeMaxWallDistSigmas * s)
            return null;

        double entryNear = wall + dir * FadeEntryDepthSigmas * s; // край зоны, ближний к центру (внутрь диапазона)
        double entryLow = Math.Min(entryNear, wall);
        double entryHigh = Math.Max(entryNear, wall);
        if (!conditional)
        {
            // Активный вход: спот уже в зоне — расширяем зону до спота, вход «от рынка».
            entryLow = Math.Min(entryLow, spot);
            entryHigh = Math.Max(entryHigh, spot);
        }
        double entryMid = (entryLow + entryHigh) / 2.0;

        // Стоп за стеной: пробой стены с запасом отменяет фейд.
        double stop = side == TradeSide.Short
            ? wall + FadeStopSigmas * s
            : wall - FadeStopSigmas * s;

        // Вырожденная геометрия: спот уже ЗА стопом (стена пробита глубже буфера) —
        // по собственному критерию инвалидации этот фейд уже отменён, публиковать нечего.
        if (dir < 0 ? spot >= stop : spot <= stop)
            return null;

        // Кандидаты-цели: пин, flip, Max Pain (только у экспирации), середина и дальняя
        // граница диапазона — все строго в сторону сделки.
        var candidates = new List<double>();
        if (pinStrike is { } pin) candidates.Add(pin);
        if (gammaFlip is { } flip) candidates.Add(flip);
        if (horizonDte <= MaxPainTargetMaxDte) candidates.Add(maxPain);
        candidates.Add((wall + oppositeBoundary) / 2.0);
        candidates.Add(oppositeBoundary - dir * 0.3 * s); // чуть НЕ доходя до противоположной стены

        // Цели фейда — строго ВНУТРИ диапазона: за противоположной границей идея
        // «возврат к центру» не действует (отсекает дальние Max Pain/пин/флип).
        candidates.RemoveAll(p => dir < 0 ? p < oppositeBoundary : p > oppositeBoundary);

        (double target, double rr)? pick = PickTarget(candidates, entryMid, stop, dir,
            FadeMinTargetSigmas * s, FadeMinRR);
        if (pick is null)
            return null;

        string wallName = side == TradeSide.Short
            ? (isWall ? $"CALL-стены {Fmt(wall)}" : $"+1σ {Fmt(wall)}")
            : (isWall ? $"PUT-стены {Fmt(wall)}" : $"−1σ {Fmt(wall)}");

        var trade = new PrimaryTrade
        {
            Action = TradeAction.FadeRange,
            Side = side,
            IsConditional = conditional,
            EntryLow = entryLow,
            EntryHigh = entryHigh,
            Target = pick.Value.target,
            Stop = stop,
            RiskReward = Math.Round(pick.Value.rr, 2),
            Drivers = TopDrivers(rec.BiasComponents, 2),
            Headline = $"ФЕЙД ДИАПАЗОНА — {(side == TradeSide.Short ? "Short" : "Long")} от {wallName} → {Fmt(pick.Value.target)}",
            Reason = $"+гамма: дилеры гасят волатильность — работа от {wallName} к магниту {Fmt(pick.Value.target)}.",
            Invalidation = side == TradeSide.Short
                ? $"закрытие выше {Fmt(stop)} — пробой стены, фейд отменяется"
                : $"закрытие ниже {Fmt(stop)} — пробой стены, фейд отменяется",
            PlanB = "Пробой стены со сменой знака Net GEX → переход к импульсной сделке по направлению пробоя."
        };

        if (conditional)
        {
            double distPct = (wall - spot) / spot * 100.0;
            trade.Trigger = $"подход к {wallName} ({distPct.ToString("+0.0;-0.0", CultureInfo.CurrentCulture)}% от спота) — вход только из зоны";
        }

        return trade;
    }

    /// <summary>
    /// −Гамма: импульсная сделка только при согласованных сигналах (|BiasScore| ≥ порога).
    /// Вход «от рынка», стоп за gamma-flip (смена режима — естественная инвалидация),
    /// цель — ближайший структурный уровень с R:R ≥ <see cref="BreakoutMinRR"/>.
    /// </summary>
    private PrimaryTrade BuildBreakoutPlan(
        SessionRecommendation rec, double spot, double sessionSigma,
        double? callWallStrike, double? putWallStrike, double? pinStrike, double? gammaFlip)
    {
        double s = sessionSigma;

        if (Math.Abs(rec.BiasScore) < MinBreakoutBias)
        {
            return StandAsidePrimary(
                "−гамма: волатильность расширена, но сигналы направления не согласованы — двусторонний риск без преимущества.",
                "Вход — при согласовании потока ΔDEX и положения относительно gamma-flip, либо по факту пробоя GEX-стены.");
        }

        int dir = rec.BiasScore < 0 ? -1 : +1;
        TradeSide side = dir < 0 ? TradeSide.Short : TradeSide.Long;

        double entryLow = spot - MarketEntryHalfSigmas * s;
        double entryHigh = spot + MarketEntryHalfSigmas * s;
        double entryMid = spot;

        double stop;
        string invalidation;
        bool flipOnStopSide = gammaFlip is { } gf && double.IsFinite(gf) &&
                              (dir < 0 ? gf > spot : gf < spot) &&
                              Math.Abs(gf - spot) <= FlipStopMaxDistSigmas * s;
        if (flipOnStopSide && gammaFlip is { } flipPx)
        {
            stop = dir < 0 ? flipPx + FlipStopBufferSigmas * s : flipPx - FlipStopBufferSigmas * s;
            invalidation = $"возврат за gamma-flip {Fmt(flipPx)} — смена режима на +гамму, импульс отменяется";
        }
        else
        {
            stop = dir < 0 ? spot + BreakoutStopSigmas * s : spot - BreakoutStopSigmas * s;
            invalidation = $"закрытие {(dir < 0 ? "выше" : "ниже")} {Fmt(stop)}";
        }

        // Кандидаты-цели по направлению: стена направления (в −гамме её пробой ускоряет ход),
        // пин, σ-границы горизонта.
        var candidates = new List<double>();
        if (dir < 0)
        {
            if (putWallStrike is { } pwS) candidates.Add(pwS);
            candidates.Add(rec.Range.Lower1);
            candidates.Add(rec.Range.Lower2);
        }
        else
        {
            if (callWallStrike is { } cwS) candidates.Add(cwS);
            candidates.Add(rec.Range.Upper1);
            candidates.Add(rec.Range.Upper2);
        }
        if (pinStrike is { } pin) candidates.Add(pin);

        // Режимная согласованность цели: если gamma-flip лежит ПО НАПРАВЛЕНИЮ сделки
        // (импульс против структуры), за флипом начинается +гамма — разгоняющая идея
        // там мертва, и эта же модель рекомендует фейд. Цели за флипом отбрасываем,
        // вместо них — «чуть не доходя до флипа». Сделка ПО структуре не затрагивается:
        // у неё флип на стороне стопа.
        if (gammaFlip is { } flipT && double.IsFinite(flipT) &&
            (dir > 0 ? flipT > entryMid : flipT < entryMid))
        {
            candidates.RemoveAll(p => dir > 0 ? p > flipT : p < flipT);
            candidates.Add(flipT - dir * FlipStopBufferSigmas * s);
        }

        (double target, double rr)? pick = PickTarget(candidates, entryMid, stop, dir,
            BreakoutMinTargetSigmas * s, BreakoutMinRR);
        if (pick is null)
        {
            return StandAsidePrimary(
                $"−гамма с направлением {(dir < 0 ? "вниз" : "вверх")}, но нет цели с R:R ≥ {BreakoutMinRR.ToString("0.0", CultureInfo.InvariantCulture)}.",
                "Сетап появится при отходе цены от ближайших структурных уровней.");
        }

        return new PrimaryTrade
        {
            Action = TradeAction.Breakout,
            Side = side,
            EntryLow = entryLow,
            EntryHigh = entryHigh,
            Target = pick.Value.target,
            Stop = stop,
            RiskReward = Math.Round(pick.Value.rr, 2),
            Drivers = TopDrivers(rec.BiasComponents, 2),
            Headline = $"ИМПУЛЬС (−гамма) — {(side == TradeSide.Short ? "ШОРТ" : "ЛОНГ")} от {Fmt(spot)} → {Fmt(pick.Value.target)}",
            Reason = "−гамма: хеджирование дилеров усиливает движение; направление — по согласованным сигналам (см. таблицу).",
            Invalidation = invalidation,
            PlanB = gammaFlip is { } f && double.IsFinite(f)
                ? $"Возврат выше/ниже gamma-flip {Fmt(f)} в +гамму → переход к фейду диапазона."
                : "Смена знака Net GEX у спота → переход к фейду диапазона."
        };
    }

    /// <summary>
    /// Нейтральная гамма: по умолчанию вне рынка; направленная сделка допускается только
    /// при сильном согласовании сигналов (|BiasScore| ≥ <see cref="MinNeutralDirectionalBias"/>).
    /// </summary>
    private PrimaryTrade BuildNeutralPlan(
        SessionRecommendation rec, double spot, double sessionSigma,
        double? callWallStrike, double? putWallStrike, double? pinStrike, double? gammaFlip)
    {
        double s = sessionSigma;

        if (Math.Abs(rec.BiasScore) < MinNeutralDirectionalBias)
        {
            return StandAsidePrimary(
                "Net GEX у спота ≈ 0 — режим не определён, статистического преимущества нет.",
                "Сетап появится при смещении Net GEX от нуля (режим) либо подходе цены к GEX-стене.");
        }

        int dir = rec.BiasScore < 0 ? -1 : +1;
        TradeSide side = dir < 0 ? TradeSide.Short : TradeSide.Long;

        double entryLow = spot - MarketEntryHalfSigmas * s;
        double entryHigh = spot + MarketEntryHalfSigmas * s;

        double stop;
        string invalidation;
        bool flipOnStopSide = gammaFlip is { } gf && double.IsFinite(gf) &&
                              (dir < 0 ? gf > spot : gf < spot) &&
                              Math.Abs(gf - spot) <= FlipStopMaxDistSigmas * s;
        if (flipOnStopSide && gammaFlip is { } flipPx)
        {
            stop = dir < 0 ? flipPx + FlipStopBufferSigmas * s : flipPx - FlipStopBufferSigmas * s;
            invalidation = $"возврат за gamma-flip {Fmt(flipPx)} — идея отменяется";
        }
        else
        {
            stop = dir < 0 ? spot + DirectionalStopSigmas * s : spot - DirectionalStopSigmas * s;
            invalidation = $"закрытие {(dir < 0 ? "выше" : "ниже")} {Fmt(stop)}";
        }

        var candidates = new List<double>();
        if (dir < 0)
        {
            if (putWallStrike is { } pwS) candidates.Add(pwS);
            candidates.Add(rec.Range.Lower1);
        }
        else
        {
            if (callWallStrike is { } cwS) candidates.Add(cwS);
            candidates.Add(rec.Range.Upper1);
        }
        if (pinStrike is { } pin) candidates.Add(pin);

        (double target, double rr)? pick = PickTarget(candidates, spot, stop, dir,
            DirectionalMinTargetSigmas * s, DirectionalMinRR);
        if (pick is null)
        {
            return StandAsidePrimary(
                "Гамма ≈ 0, сигналы согласованы, но нет цели с приемлемым R:R.",
                "Сетап появится при отходе цены от ближайших структурных уровней.");
        }

        return new PrimaryTrade
        {
            Action = TradeAction.Directional,
            Side = side,
            EntryLow = entryLow,
            EntryHigh = entryHigh,
            Target = pick.Value.target,
            Stop = stop,
            RiskReward = Math.Round(pick.Value.rr, 2),
            Drivers = TopDrivers(rec.BiasComponents, 2),
            Headline = $"{(side == TradeSide.Short ? "ШОРТ" : "ЛОНГ")} от {Fmt(spot)} → {Fmt(pick.Value.target)} (гамма ≈ 0)",
            Reason = "Гамма у спота ≈ 0: режим не задан, направление — по сильному согласованию сигналов (см. таблицу).",
            Invalidation = invalidation,
            PlanB = "Смещение Net GEX от нуля задаст режим: +гамма → фейд диапазона, −гамма → импульс."
        };
    }

    /// <summary>
    /// Ближайшая по дистанции цель в сторону <paramref name="dir"/>, проходящая фильтры:
    /// дистанция ≥ <paramref name="minDist"/> И R:R = дистанция/риск ≥ <paramref name="minRR"/>.
    /// Так как R:R растёт с дистанцией, достаточно ближайшего кандидата с
    /// дистанцией ≥ max(minDist, minRR·риск). null — кандидатов нет (план не публикуется).
    /// </summary>
    private static (double Target, double Rr)? PickTarget(
        IReadOnlyList<double> candidates, double entryMid, double stop, int dir,
        double minDist, double minRR)
    {
        double risk = Math.Abs(entryMid - stop);
        if (risk <= 0 || !double.IsFinite(risk))
            return null;

        double requiredDist = Math.Max(minDist, minRR * risk);
        double best = 0, bestDist = double.MaxValue;

        foreach (double p in candidates)
        {
            if (p <= 0 || !double.IsFinite(p))
                continue;
            double dist = dir > 0 ? p - entryMid : entryMid - p;
            if (dist < requiredDist)
                continue;
            if (dist < bestDist)
            {
                bestDist = dist;
                best = p;
            }
        }

        if (best <= 0 || !double.IsFinite(best))
            return null;

        return (best, bestDist / risk);
    }

    /// <summary>Заглушка «ВНЕ РЫНКА» с причиной и условием появления сетапа.</summary>
    private static PrimaryTrade StandAsidePrimary(string reason, string? setup = null) => new()
    {
        Action = TradeAction.StandAside,
        Side = TradeSide.None,
        Headline = "ВНЕ РЫНКА",
        Reason = reason,
        Setup = setup ?? "План появится при корректном снимке с открытым интересом и ценой базового актива.",
        Invalidation = "—"
    };

    /// <summary>Топ-N драйверов по модулю вклада в bias (короткой строкой).</summary>
    private static List<string> TopDrivers(IReadOnlyList<BiasComponent> components, int n)
        => components
            .Where(c => Math.Abs(c.Contribution) > 1e-6)
            .OrderByDescending(c => Math.Abs(c.Contribution))
            .Take(n)
            .Select(c => $"{c.Name}: {(c.Normalized >= 0 ? "вверх" : "вниз")} (вклад {c.Contribution:+0.00;-0.00})")
            .ToList();

    private List<PriceLevel> BuildLevels(
        SessionRecommendation rec, double spot, IReadOnlyList<OptionData> chain,
        double? callWallStrike, double? putWallStrike, double? pinStrike,
        double maxPain, double centroid, double? gammaFlip)
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

        double? OiAt(double strike, bool call)
        {
            OptionData? row = chain.FirstOrDefault(o => o.Strike == strike);
            if (row is null) return null;
            double oi = call ? row.CallOi : row.PutOi;
            return oi > 0 ? oi : null;
        }

        Add(LevelKind.Spot, "Спот", spot, "Спот");

        if (callWallStrike is { } cw)
            Add(LevelKind.CallWall, $"CALL-стена {Fmt(cw)}", cw, "Сопротивление", OiAt(cw, call: true));

        if (putWallStrike is { } pw)
            Add(LevelKind.PutWall, $"PUT-стена {Fmt(pw)}", pw, "Поддержка", OiAt(pw, call: false));

        if (pinStrike is { } pin)
            Add(LevelKind.GammaPeak, $"Пик гаммы {Fmt(pin)}", pin, "Магнит/пин");

        Add(LevelKind.MaxPain, $"Max Pain {Fmt(maxPain)}", maxPain, "Магнит");
        Add(LevelKind.GravityEquilibrium, $"Центр тяжести {Fmt(centroid)}", centroid, "Справочно");

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
        LevelKind.GammaPeak => 2,
        LevelKind.MaxPain => 3,
        LevelKind.GammaFlip => 4,
        LevelKind.GravityEquilibrium => 5,
        _ => 6 // σ-границы
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
