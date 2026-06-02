using System.Globalization;
using Option.Data.Shared.Dto;
using Option.Data.Ui.Models;

namespace Option.Data.Ui.Services;

/// <summary>
/// Синтезирует торговый план сессии (<see cref="SessionRecommendation"/>) по per-expiration
/// аналитике (раздел 8 спецификации). Сводит все экспирации в один план: направление, режим
/// волатильности, диапазон 1σ/2σ, ключевые уровни, скоринг направления и сценарии.
///
/// Соглашения согласованы с <see cref="OptionExposureMath"/> и <see cref="SessionAnalysisMath"/>:
/// σ передаётся долей (mark_iv/100); DEX — USD на $1 движения; Net GEX — USD на 1% движения.
/// </summary>
public class SessionRecommendationBuilder : ISessionRecommendationBuilder
{
    /// <summary>
    /// Порог «≈0» для Net GEX у спота при определении режима: доля от фактического масштаба
    /// профиля гаммы (max |NetGex| по близким страйкам в окне ±15%). Если |NetGexAtSpot| меньше
    /// этой доли от пикового |NetGex| профиля — режим Neutral. Калибровано так, чтобы нейтральная
    /// зона была достижима при реально слабом/неоднозначном GEX у спота.
    /// </summary>
    private const double NeutralGexProfileFraction = 0.05;

    /// <summary>Допуск дедупликации уровней по цене, % от спота.</summary>
    private const double LevelDedupPercent = 0.1;

    // --- Веса скоринга направления. pin+gravity объединены в один «Магнит». КАЛИБРУЕМО. ---
    /// <summary>Вес объединённого магнитного сигнала (Max Pain + OI-центроид); ×pf близости. КАЛИБРУЕМО.</summary>
    private const double WeightMagnet = 0.30;

    /// <summary>Вес долларовой дельта-экспозиции (DEX). КАЛИБРУЕМО.</summary>
    private const double WeightDex = 0.30;

    /// <summary>Вес скоса (25Δ Risk Reversal). КАЛИБРУЕМО.</summary>
    private const double WeightSkew = 0.40;

    /// <summary>Добавка к B за импульс при отрицательной гамме относительно gamma-flip. КАЛИБРУЕМО.</summary>
    private const double NegativeGammaMomentum = 0.15;

    /// <summary>Нормировочный делитель скоса (RR в пунктах волатильности). КАЛИБРУЕМО.</summary>
    private const double SkewNormalizer = 5.0;

    /// <summary>Пол масштаба нормировки магнита: доля спота (не даёт насытиться на однодневных фронтах). КАЛИБРУЕМО.</summary>
    private const double MagnetScaleFloorPct = 0.01;

    // --- Параметры главной рекомендации (раздел B спека). КАЛИБРУЕМО. ---
    /// <summary>Минимальная конвикция (0..100), ниже — ВНЕ РЫНКА. КАЛИБРУЕМО.</summary>
    private const int ConvictionMin = 30;

    /// <summary>Порог |BiasScore| для ВНЕ РЫНКА (нет внятного смещения). КАЛИБРУЕМО.</summary>
    private const double StandAsideBiasMin = 0.15;

    /// <summary>Близость спота к gamma-flip (в долях σ), при которой режим неустойчив → ВНЕ РЫНКА. КАЛИБРУЕМО.</summary>
    private const double FlipProximitySigma = 0.25;

    /// <summary>Полуширина зоны входа в долях σ. КАЛИБРУЕМО.</summary>
    private const double EntryZoneSigma = 0.15;

    public SessionRecommendation Build(
        ExpirationAnalysis selected,
        string currency,
        DateTimeOffset asOf)
    {
        var rec = new SessionRecommendation { Currency = currency, AsOf = asOf };

        if (selected is null || selected.OptionData.Count == 0)
        {
            rec.Notes.Add("Пустой снимок: нет данных по выбранной экспирации — план не строится.");
            return rec;
        }

        ExpirationAnalysis front = selected;
        double frontDte = OptionExposureMath.YearsToExpiry(front.Expiration, asOf) * 365.0;
        double spot = front.UnderlyingPrice;

        rec.FrontExpiration = front.Expiration;
        rec.FrontDaysToExpiry = frontDte;
        rec.Spot = spot;
        rec.FrontChain = front.OptionData;

        if (spot <= 0 || !double.IsFinite(spot))
        {
            rec.Notes.Add("Некорректная цена базового актива — расчёт диапазона недоступен.");
            return rec;
        }

        // --- GammaStrike по выбранной экспирации ---
        List<SessionAnalysisMath.GammaStrike> gammaStrikes = BuildGammaStrikes(front, asOf);

        // --- Профиль гаммы и gamma-flip (рисуемая модель) ---
        rec.GammaProfile = SessionAnalysisMath.GammaProfile(gammaStrikes, spot);
        double? gammaFlip = SessionAnalysisMath.GammaFlip(rec.GammaProfile, spot);
        rec.GammaFlip = gammaFlip;

        // --- Режим: Net GEX у спота из ТОГО ЖЕ профиля (Black-76 в точке спота). ---
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

        // --- Кросс-сверка профиля с реальными греками Deribit выбранной экспирации. ---
        double netGexGreeks = front.NetGammaExposure;
        if (!double.IsFinite(netGexGreeks)) netGexGreeks = 0;
        bool gammaModelsDisagree =
            Math.Abs(netGexAtSpot) > neutralBand &&
            Math.Sign(netGexAtSpot) != Math.Sign(netGexGreeks) &&
            netGexGreeks != 0;

        // --- σ до выбранной экспирации: 1σ = S·σ_ATM·√T (готовое поле ExpectedMove1Sigma). ---
        double tYears = OptionExposureMath.YearsToExpiry(front.Expiration, asOf);
        double atmIv = OptionExposureMath.AtmIvFraction(front.OptionData, spot);
        double sigma1 = front.ExpectedMove1Sigma > 0 && double.IsFinite(front.ExpectedMove1Sigma)
            ? front.ExpectedMove1Sigma
            : OptionExposureMath.ExpectedMove1Sigma(spot, atmIv, tYears);

        rec.HoursToFrontExpiry = Math.Max(frontDte * 24.0, 0.0);

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

        // --- Стены OI по выбранной цепочке ---
        OptionData? callWall = SessionAnalysisMath.CallWall(front.OptionData, spot);
        OptionData? putWall = SessionAnalysisMath.PutWall(front.OptionData, spot);

        // --- Магнит: OI-центроид выбранной экспирации (фолбэк Max Pain) ---
        double centroid = front.OiCentroid > 0 ? front.OiCentroid : front.MaxPain;
        double primaryMagnet = front.MaxPain > 0 ? front.MaxPain : centroid;

        // --- Уровни ---
        rec.Levels = BuildLevels(rec, spot, callWall, putWall, front.MaxPain, centroid, gammaFlip);

        // --- Скоринг направления ---
        ScoreDirection(rec, spot, sigma1, front, gammaFlip, frontDte, gammaModelsDisagree);

        // --- Конвикция и главная сделка сессии ---
        double regimeClarity = profilePeak > 0
            ? SessionAnalysisMath.Clamp(Math.Abs(netGexAtSpot) / profilePeak, 0, 1)
            : 0;
        double agreement = SignAgreement(rec.BiasComponents, rec.BiasScore);
        int conviction = (int)Math.Round(100 * Math.Abs(rec.BiasScore) * agreement * regimeClarity);
        rec.Primary = BuildPrimaryTrade(rec, spot, callWall, putWall, primaryMagnet, gammaFlip,
            conviction, gammaModelsDisagree);

        return rec;
    }

    private static double TotalOi(IReadOnlyList<OptionData> chain)
        => chain.Sum(o => o.CallOi + o.PutOi);

    /// <summary>
    /// Строит список GammaStrike по каждому страйку выбранной экспирации: σ — долей (Iv/100),
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
    /// Доля компонентов скоринга, чей знак <see cref="BiasComponent.Normalized"/> совпадает
    /// со знаком итогового <paramref name="biasScore"/> (компоненты с |Normalized|≈0 не учитываются).
    /// 0..1 — мера согласованности сигналов для расчёта конвикции.
    /// </summary>
    private static double SignAgreement(IReadOnlyList<BiasComponent> components, double biasScore)
    {
        const double eps = 1e-6;
        if (Math.Abs(biasScore) < eps)
            return 0;

        int sign = Math.Sign(biasScore);
        int considered = 0;
        int agree = 0;
        foreach (BiasComponent c in components)
        {
            if (Math.Abs(c.Normalized) < eps)
                continue;
            considered++;
            if (Math.Sign(c.Normalized) == sign)
                agree++;
        }

        return considered > 0 ? (double)agree / considered : 0;
    }

    /// <summary>
    /// Главная сделка сессии (раздел B спека): детерминированная матрица режим×смещение.
    /// +гамма → фейд края к магниту; −гамма → пробой в сторону смещения; иначе/слабый или
    /// противоречивый сигнал / спот у gamma-flip → ВНЕ РЫНКА. Вход/цель/стоп — из тех же
    /// уровней и σ-диапазона, что и карта; цель и стоп всегда по разные стороны от входа.
    /// </summary>
    private PrimaryTrade BuildPrimaryTrade(
        SessionRecommendation rec, double spot,
        OptionData? callWall, OptionData? putWall,
        double primaryMagnet, double? gammaFlip, int conviction, bool gammaModelsDisagree)
    {
        double b = rec.BiasScore;
        double sigma = rec.Range.DailySigma1;
        double safeSigma = sigma > 0 && double.IsFinite(sigma) ? sigma : spot * 0.01;
        double upper1 = rec.Range.Upper1, lower1 = rec.Range.Lower1;
        double upper2 = rec.Range.Upper2, lower2 = rec.Range.Lower2;
        double callWallPrice = callWall?.Strike ?? upper1;
        double putWallPrice = putWall?.Strike ?? lower1;

        var trade = new PrimaryTrade
        {
            Conviction = conviction,
            ConvictionLabel = ConvictionLabel(conviction),
            Drivers = TopDrivers(rec.BiasComponents, 2)
        };

        // --- Условия ВНЕ РЫНКА (сигнал недостоверен/не сформирован) ---
        bool nearFlip = gammaFlip is { } gf && double.IsFinite(gf)
                        && Math.Abs(spot - gf) < FlipProximitySigma * safeSigma;
        string? standReason =
            gammaModelsDisagree ? "модели гаммы расходятся по знаку у спота" :
            rec.Regime == VolatilityRegime.Neutral ? $"Net GEX у спота ≈0 ({FmtUsd(rec.NetGexAtSpot)}/1%) — чёткого режима нет" :
            nearFlip ? $"спот вплотную к gamma-flip {Fmt(gammaFlip!.Value)} — режим неустойчив" :
            Math.Abs(b) < StandAsideBiasMin ? "сигналы гасят друг друга — нет смещения" :
            conviction < ConvictionMin ? "конвикция ниже порога" : null;

        if (standReason is not null)
        {
            trade.Action = TradeAction.StandAside;
            trade.Side = TradeSide.None;
            trade.Headline = "ВНЕ РЫНКА";
            trade.Reason = standReason;
            trade.Setup = $"Сетап появится при выходе за {Fmt(lower1)} / {Fmt(upper1)} или росте |смещения| ≥ {StandAsideBiasMin:0.00}.";
            trade.Invalidation = "—";
            return trade;
        }

        double stopBuffer = Math.Max(safeSigma, spot * 0.0025);
        double entryHalf = EntryZoneSigma * safeSigma;

        if (rec.Regime == VolatilityRegime.PositiveGamma)
        {
            // ФЕЙД: торгуем к магниту. Магнит выше спота → Long от поддержки; ниже → Short от сопротивления.
            bool longSide = primaryMagnet >= spot;
            trade.Action = TradeAction.FadeRange;
            trade.Side = longSide ? TradeSide.Long : TradeSide.Short;
            trade.Target = primaryMagnet;

            if (longSide)
            {
                double entry = NearestInDirection([putWallPrice, lower1], spot, -1, lower1);
                trade.EntryLow = entry - entryHalf;
                trade.EntryHigh = entry + entryHalf;
                trade.Stop = entry - stopBuffer;
                trade.Headline = $"ФЕЙД — Long от поддержки {Fmt(entry)} к магниту {Fmt(primaryMagnet)}";
                trade.Invalidation = $"закрытие ниже {Fmt(trade.Stop.Value)} → коридор сломан, фейд отменяется";
            }
            else
            {
                double entry = NearestInDirection([callWallPrice, upper1], spot, +1, upper1);
                trade.EntryLow = entry - entryHalf;
                trade.EntryHigh = entry + entryHalf;
                trade.Stop = entry + stopBuffer;
                trade.Headline = $"ФЕЙД — Short от сопротивления {Fmt(entry)} к магниту {Fmt(primaryMagnet)}";
                trade.Invalidation = $"закрытие выше {Fmt(trade.Stop.Value)} → коридор сломан, фейд отменяется";
            }

            trade.Reason = $"положительная гамма (Net GEX {FmtUsd(rec.NetGexAtSpot)}/1%) — дилеры гасят волатильность, цену тянет к магниту {Fmt(primaryMagnet)}.";
            trade.PlanB = gammaFlip is { } f1
                ? $"Если закрытие за gamma-flip {Fmt(f1)} — режим ломается, переход к пробою."
                : "Если коридор пробит с закреплением — переход к пробою.";
        }
        else
        {
            // ПРОБОЙ: вход по пробою в сторону смещения.
            bool longSide = b >= 0;
            trade.Action = TradeAction.Breakout;
            trade.Side = longSide ? TradeSide.Long : TradeSide.Short;

            double[] upLevels = [upper1, callWallPrice, upper2];
            double[] downLevels = [lower1, putWallPrice, lower2];

            if (longSide)
            {
                double trigger = NearestInDirection(upLevels, spot, +1, upper1);
                trade.EntryLow = trigger;
                trade.EntryHigh = trigger + 0.1 * safeSigma;
                List<double> tgts = DirectionalTargets([callWallPrice, upper2], trigger, +1, spot, safeSigma);
                trade.Target = tgts.Count > 0 ? tgts[0] : trigger + Math.Max(safeSigma, spot * 0.005);
                trade.Stop = trigger - stopBuffer;
                trade.Headline = $"ПРОБОЙ — Long выше {Fmt(trigger)}";
                trade.Invalidation = $"возврат ниже {Fmt(trigger)} — ложный пробой";
            }
            else
            {
                double trigger = NearestInDirection(downLevels, spot, -1, lower1);
                trade.EntryLow = trigger - 0.1 * safeSigma;
                trade.EntryHigh = trigger;
                List<double> tgts = DirectionalTargets([putWallPrice, lower2], trigger, -1, spot, safeSigma);
                trade.Target = tgts.Count > 0 ? tgts[0] : trigger - Math.Max(safeSigma, spot * 0.005);
                trade.Stop = trigger + stopBuffer;
                trade.Headline = $"ПРОБОЙ — Short ниже {Fmt(trigger)}";
                trade.Invalidation = $"возврат выше {Fmt(trigger)} — ложный пробой";
            }

            trade.Reason = $"отрицательная гамма (Net GEX {FmtUsd(rec.NetGexAtSpot)}/1%) — дилеры усиливают движения, склонность к пробою в сторону {BiasText(rec.Bias)}.";
            trade.PlanB = "Если пробой ложный (возврат за триггер) — фейд обратно в коридор.";
        }

        trade.RiskReward = ComputeRiskReward(trade);
        return trade;
    }

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

    /// <summary>
    /// Скоринг направления по выбранной экспирации. Нормировки в [−1,+1], + = вверх. Каждый сигнал →
    /// BiasComponent (используются для конвикции и драйверов; таблицей не выводятся). Веса
    /// перенормируются (Σw), skew пропускается при отсутствии RR.
    /// </summary>
    private void ScoreDirection(
        SessionRecommendation rec, double spot, double sigma1,
        ExpirationAnalysis front, double? gammaFlip, double frontDte, bool gammaModelsDisagree)
    {
        var components = new List<BiasComponent>();
        double safeSigma = sigma1 > 0 && double.IsFinite(sigma1) ? sigma1 : spot * 0.01;

        // --- Магнит (тяга к Max Pain и OI-центроиду выбранной экспирации) ---
        double pf = SessionAnalysisMath.Clamp(1 - frontDte / 7.0, 0.3, 1);
        double magnetScale = Math.Max(front.ExpectedMove1Sigma, spot * MagnetScaleFloorPct);
        if (!(magnetScale > 0)) magnetScale = safeSigma;

        var pulls = new List<(string Label, double Price, double Raw, double Ratio)>();
        if (front.MaxPain > 0)
            pulls.Add(("Max Pain", front.MaxPain, front.MaxPain - spot, (front.MaxPain - spot) / magnetScale));
        if (front.OiCentroid > 0)
            pulls.Add(("центр тяжести OI", front.OiCentroid, front.OiCentroid - spot, (front.OiCentroid - spot) / magnetScale));

        double magnetRaw = pulls.Count > 0 ? pulls.Average(p => p.Raw) : 0;
        double magnetNorm = pulls.Count > 0 ? Math.Tanh(pulls.Average(p => p.Ratio)) : 0;
        components.Add(new BiasComponent
        {
            Name = "Магнит",
            RawValue = magnetRaw,
            Normalized = magnetNorm,
            Weight = WeightMagnet * pf,
            Explanation = pulls.Count > 0
                ? string.Join("; ", pulls.Select(p =>
                      $"{p.Label} {Fmt(p.Price)} {Direction(p.Raw)} спота на {Fmt(Math.Abs(p.Raw))} ({p.Ratio:+0.00;-0.00;0.00}·масштаб)")) +
                  $"; масштаб {Fmt(magnetScale)}, вес ×близость (DTE {frontDte:0.0})."
                : "магниты недоступны (нет OI)."
        });

        // --- DEX (долларовая дельта-экспозиция) выбранной экспирации ---
        double dexRaw = front.DollarDeltaExposure;
        double totalOi = TotalOi(front.OptionData);
        double dexDenom = spot * totalOi;
        double dex = dexDenom > 0 && double.IsFinite(dexDenom)
            ? SessionAnalysisMath.Clamp(dexRaw / dexDenom, -1, 1)
            : 0;
        components.Add(new BiasComponent
        {
            Name = "DEX (дельта-экспозиция)",
            RawValue = dexRaw,
            Normalized = dex,
            Weight = WeightDex,
            Explanation = $"DEX {FmtUsd(dexRaw)} → {(dex >= 0 ? "бычий" : "медвежий")} тилт " +
                          $"позиционирования (нормирована к спот·OI)."
        });

        // --- skew (25Δ Risk Reversal) — пропускается при отсутствии данных ---
        double? rr = front.RiskReversal25Delta;
        if (rr.HasValue && double.IsFinite(rr.Value))
        {
            double rrAvg = rr.Value;
            double skew = -SessionAnalysisMath.Clamp(rrAvg / SkewNormalizer, -1, 1);
            components.Add(new BiasComponent
            {
                Name = "Скос (25Δ RR)",
                RawValue = rrAvg,
                Normalized = skew,
                Weight = WeightSkew,
                Explanation = $"25Δ Risk Reversal {rrAvg:+0.0;-0.0;0.0} п.в. — " +
                              (rrAvg > 0
                                  ? "путы дороже коллов (спрос на защиту вниз), медвежий риск."
                                  : "коллы дороже путов (жадность), бычий риск.")
            });
        }

        // --- Перенормировка весов и взвешенная сумма ---
        double totalWeight = components.Sum(c => c.Weight);
        double b = 0;
        if (totalWeight > 0)
        {
            foreach (BiasComponent c in components)
            {
                c.Contribution = c.Normalized * (c.Weight / totalWeight);
                b += c.Contribution;
            }
        }

        b = SessionAnalysisMath.Clamp(b, -1, 1);

        // --- Импульс при отрицательной гамме относительно flip ---
        if (rec.Regime == VolatilityRegime.NegativeGamma && gammaFlip is { } flip && !gammaModelsDisagree)
        {
            double sign = Math.Sign(spot - flip);
            b = SessionAnalysisMath.Clamp(b + NegativeGammaMomentum * sign, -1, 1);
            components.Add(new BiasComponent
            {
                Name = "Импульс (−гамма)",
                RawValue = spot - flip,
                Normalized = sign,
                Weight = 0,
                Contribution = NegativeGammaMomentum * sign,
                Explanation = $"режим отрицательной гаммы усиливает движение: спот {Fmt(spot)} " +
                              $"{(sign >= 0 ? "выше" : "ниже")} gamma-flip {Fmt(flip)} → импульс {(sign >= 0 ? "вверх" : "вниз")}."
            });
        }

        rec.BiasScore = b;
        rec.BiasComponents = components;
        rec.Bias = MapBias(b);
    }

    private static DirectionBias MapBias(double b) => b switch
    {
        >= 0.45 => DirectionBias.StrongUp,
        >= 0.15 => DirectionBias.ModerateUp,
        > -0.15 => DirectionBias.Neutral,
        > -0.45 => DirectionBias.ModerateDown,
        _ => DirectionBias.StrongDown
    };

    /// <summary>
    /// Ближайший к споту уровень в сторону движения (dir&gt;0 — вверх, dir&lt;0 — вниз).
    /// Возвращает <paramref name="fallback"/>, если кандидатов на этой стороне нет.
    /// </summary>
    private static double NearestInDirection(IReadOnlyList<double> candidates, double spot, int dir, double fallback)
    {
        double eps = Math.Max(spot * 0.0005, 1e-6);
        double best = fallback;
        double bestDist = double.MaxValue;

        foreach (double p in candidates)
        {
            if (p <= 0 || !double.IsFinite(p)) continue;
            bool onSide = dir > 0 ? p > spot + eps : p < spot - eps;
            if (!onSide) continue;

            double d = Math.Abs(p - spot);
            if (d < bestDist)
            {
                bestDist = d;
                best = p;
            }
        }

        return best;
    }

    /// <summary>
    /// Цели строго ЗА точкой <paramref name="from"/> в сторону движения (<paramref name="dir"/>),
    /// ближайшие первыми, максимум 2 (с дедупом). Если структурных уровней за триггером нет —
    /// измеренный ход: <c>from ± max(σ, 0.5% спота)</c>. Гарантирует, что цели на верной стороне.
    /// </summary>
    private static List<double> DirectionalTargets(IReadOnlyList<double> candidates, double from, int dir, double spot, double sigma)
    {
        double eps = Math.Max(spot * 0.0005, 1e-6);

        List<double> ordered = candidates
            .Where(p => p > 0 && double.IsFinite(p) && (dir > 0 ? p > from + eps : p < from - eps))
            .OrderBy(p => Math.Abs(p - from))
            .ToList();

        var targets = new List<double>();
        foreach (double p in ordered)
        {
            if (targets.Count >= 2) break;
            if (targets.All(t => Math.Abs(t - p) > eps)) targets.Add(p);
        }

        if (targets.Count == 0)
        {
            double mm = from + dir * Math.Max(sigma > 0 ? sigma : 0, spot * 0.005);
            if (mm > 0 && double.IsFinite(mm)) targets.Add(mm);
        }

        return targets;
    }

    private static string Direction(double delta)
        => delta > 0 ? "выше" : delta < 0 ? "ниже" : "на уровне";

    private static string BiasText(DirectionBias bias) => bias switch
    {
        DirectionBias.StrongUp => "сильного роста",
        DirectionBias.ModerateUp => "умеренного роста",
        DirectionBias.Neutral => "нейтрально",
        DirectionBias.ModerateDown => "умеренного снижения",
        DirectionBias.StrongDown => "сильного снижения",
        _ => "нейтрально"
    };

    private static string Fmt(double v)
        => double.IsFinite(v) ? v.ToString("#,##0.##", CultureInfo.InvariantCulture) : "—";

    private static string FmtUsd(double v)
        => double.IsFinite(v) ? "$" + v.ToString("#,##0", CultureInfo.InvariantCulture) : "—";
}
