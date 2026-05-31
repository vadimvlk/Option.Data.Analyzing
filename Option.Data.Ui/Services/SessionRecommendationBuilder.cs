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
    /// <summary>Близкими считаются экспирации с DTE ≤ этого порога (дней).</summary>
    private const double NearDteThreshold = 14.0;

    /// <summary>Опционы Deribit истекают в 08:00 UTC — к этому времени привязана граница сессии.</summary>
    private const int SessionResetHourUtc = 8;

    /// <summary>
    /// Порог «≈0» для Net GEX у спота при определении режима: доля от фактического масштаба
    /// профиля гаммы (max |NetGex| по близким страйкам в окне ±15%). Если |NetGexAtSpot| меньше
    /// этой доли от пикового |NetGex| профиля — режим Neutral. Калибровано так, чтобы нейтральная
    /// зона была достижима при реально слабом/неоднозначном GEX у спота.
    /// </summary>
    private const double NeutralGexProfileFraction = 0.05;

    /// <summary>Допуск дедупликации уровней по цене, % от спота.</summary>
    private const double LevelDedupPercent = 0.1;

    // --- Веса скоринга направления (раздел 8). КАЛИБРУЕМО. ---
    /// <summary>Базовый вес пиннинга к Max Pain; домножается на прокси-фактор близости экспирации. КАЛИБРУЕМО.</summary>
    private const double WeightPinBase = 0.30;

    /// <summary>Вес тяги к центру тяжести близких экспираций. КАЛИБРУЕМО.</summary>
    private const double WeightGravity = 0.20;

    /// <summary>Вес долларовой дельта-экспозиции (DEX). КАЛИБРУЕМО.</summary>
    private const double WeightDex = 0.20;

    /// <summary>Вес скоса (25Δ Risk Reversal). КАЛИБРУЕМО.</summary>
    private const double WeightSkew = 0.20;

    /// <summary>Добавка к B за импульс при отрицательной гамме относительно gamma-flip. КАЛИБРУЕМО.</summary>
    private const double NegativeGammaMomentum = 0.15;

    /// <summary>Нормировочный делитель скоса (RR в пунктах волатильности). КАЛИБРУЕМО.</summary>
    private const double SkewNormalizer = 5.0;

    public SessionRecommendation Build(
        IReadOnlyList<ExpirationAnalysis> analyses,
        string currency,
        DateTimeOffset asOf)
    {
        var rec = new SessionRecommendation { Currency = currency, AsOf = asOf };

        if (analyses.Count == 0)
        {
            rec.Notes.Add("Пустой снимок: нет данных по опционам — план не строится.");
            return rec;
        }

        // --- DTE для каждой экспирации ---
        var withDte = analyses
            .Select(a => new ExpiryView(a, OptionExposureMath.YearsToExpiry(a.Expiration, asOf) * 365.0))
            .ToList();

        // --- Фронт: минимальный DTE среди экспираций с суммарным OI>0 ---
        ExpiryView? frontView = withDte
            .Where(v => TotalOi(v.Analysis.OptionData) > 0)
            .OrderBy(v => v.Dte)
            .FirstOrDefault();

        if (frontView is null)
        {
            // Деградация: нет ни одной экспирации с OI — берём ближайшую по DTE.
            frontView = withDte.OrderBy(v => v.Dte).First();
            rec.Notes.Add("Нет экспираций с открытым интересом — сигнал недостоверен.");
        }

        ExpirationAnalysis front = frontView.Analysis;
        double frontDte = frontView.Dte;
        double spot = front.UnderlyingPrice;

        rec.FrontExpiration = front.Expiration;
        rec.FrontDaysToExpiry = frontDte;
        rec.Spot = spot;
        rec.FrontChain = front.OptionData;

        if (spot <= 0 || !double.IsFinite(spot))
        {
            rec.Notes.Add("Некорректная цена базового актива фронта — расчёт диапазона недоступен.");
            return rec;
        }

        // --- Близкие экспирации: DTE ≤ 14 ---
        List<ExpiryView> near = withDte.Where(v => v.Dte <= NearDteThreshold).ToList();
        if (near.Count == 0)
        {
            near = [frontView];
            rec.Notes.Add("Нет близких экспираций (DTE ≤ 14) — слабый сигнал, план опирается только на фронт.");
        }

        // --- Список GammaStrike по каждой близкой экспирации и страйку ---
        List<SessionAnalysisMath.GammaStrike> gammaStrikes = BuildGammaStrikes(near, asOf);

        // --- Профиль гаммы и gamma-flip (рисуемая модель) ---
        rec.GammaProfile = SessionAnalysisMath.GammaProfile(gammaStrikes, spot);
        double? gammaFlip = SessionAnalysisMath.GammaFlip(rec.GammaProfile, spot);
        rec.GammaFlip = gammaFlip;

        // --- Режим: Net GEX у спота из ТОГО ЖЕ профиля, что рисуется (Black-76 в точке спота). ---
        // Единый источник: знак режима, gamma-flip и нарисованный профиль гарантированно согласованы.
        double netGexAtSpot = SessionAnalysisMath.NetGexAtPrice(gammaStrikes, spot);
        if (!double.IsFinite(netGexAtSpot)) netGexAtSpot = 0;
        rec.NetGexAtSpot = netGexAtSpot;

        // Порог «≈0» калибруется по фактическому масштабу профиля (пиковый |NetGex| в окне ±15%),
        // а не по spot²·OI·ε — иначе нейтральная зона практически недостижима.
        double profilePeak = rec.GammaProfile.Count > 0
            ? rec.GammaProfile.Max(p => double.IsFinite(p.NetGex) ? Math.Abs(p.NetGex) : 0)
            : 0;
        double neutralBand = profilePeak * NeutralGexProfileFraction;
        rec.Regime = netGexAtSpot > neutralBand
            ? VolatilityRegime.PositiveGamma
            : netGexAtSpot < -neutralBand
                ? VolatilityRegime.NegativeGamma
                : VolatilityRegime.Neutral;

        // --- Кросс-сверка с реальными греками Deribit (Σ NetGammaExposure близких). ---
        // Если знак расходится с профильной моделью — модели гаммы противоречат друг другу;
        // фиксируем это в Note (профиль остаётся единым источником режима/flip).
        double netGexGreeks = near.Sum(v => v.Analysis.NetGammaExposure);
        if (!double.IsFinite(netGexGreeks)) netGexGreeks = 0;
        bool gammaModelsDisagree =
            Math.Abs(netGexAtSpot) > neutralBand &&
            Math.Sign(netGexAtSpot) != Math.Sign(netGexGreeks) &&
            netGexGreeks != 0;
        if (gammaModelsDisagree)
        {
            rec.Notes.Add(
                $"Модели гаммы расходятся по знаку у спота: профиль (Black-76) {FmtUsd(netGexAtSpot)}/1%, " +
                $"реальные греки Deribit {FmtUsd(netGexGreeks)}/1%. Режим взят по профилю; " +
                "импульсный модификатор отключён до согласования моделей.");
        }

        // --- Сессия и диапазон ---
        double sessionYears = ComputeSessionYears();
        double atmIv = OptionExposureMath.AtmIvFraction(front.OptionData, spot);
        double dailySigma1 = SessionAnalysisMath.DailyExpectedMove(spot, atmIv, sessionYears);

        rec.HoursToFrontExpiry = Math.Max(frontDte * 24.0, 0.0);

        rec.Range = new SessionRange
        {
            AtmIvPercent = atmIv * 100.0,
            SessionYears = sessionYears,
            DailySigma1 = dailySigma1,
            Lower1 = spot - dailySigma1,
            Upper1 = spot + dailySigma1,
            Lower2 = spot - 2 * dailySigma1,
            Upper2 = spot + 2 * dailySigma1,
            FrontExpiryExpectedMove = front.ExpectedMove1Sigma
        };

        // --- Стены OI (по агрегированной близкой цепочке) ---
        List<OptionData> nearChain = AggregateChain(near);
        OptionData? callWall = SessionAnalysisMath.CallWall(nearChain, spot);
        OptionData? putWall = SessionAnalysisMath.PutWall(nearChain, spot);

        // --- Центр тяжести близких: среднее центров, взвешенное по суммарному OI экспирации ---
        double gravityNear = WeightedGravityEquilibrium(near, front.GravityEquilibrium);

        // --- Уровни ---
        rec.Levels = BuildLevels(rec, spot, callWall, putWall, front.MaxPain, gravityNear, gammaFlip);

        // --- Скоринг направления ---
        ScoreDirection(rec, spot, dailySigma1, front, near, gravityNear, gammaFlip, frontDte, gammaModelsDisagree);

        // --- Сценарии ---
        rec.Scenarios = BuildScenarios(rec, spot, callWall, putWall, front.MaxPain, gravityNear, gammaFlip);

        // --- Макро-контекст и заметки ---
        rec.MacroContext = BuildMacroContext(withDte);
        AddEdgeCaseNotes(rec, frontDte, atmIv);

        return rec;
    }

    /// <summary>Per-expiration вид с предрассчитанным DTE (дней).</summary>
    private sealed record ExpiryView(ExpirationAnalysis Analysis, double Dte);

    private static double TotalOi(IReadOnlyList<OptionData> chain)
        => chain.Sum(o => o.CallOi + o.PutOi);

    private static double TotalNearOi(IReadOnlyList<ExpiryView> near)
        => near.Sum(v => TotalOi(v.Analysis.OptionData));

    /// <summary>
    /// Строит список GammaStrike по каждой близкой экспирации и каждому страйку: σ берётся
    /// долей (Iv/100), T — годы до экспирации от момента снимка. Пропускает страйки без OI.
    /// </summary>
    private static List<SessionAnalysisMath.GammaStrike> BuildGammaStrikes(
        IReadOnlyList<ExpiryView> near, DateTimeOffset asOf)
    {
        var result = new List<SessionAnalysisMath.GammaStrike>();

        foreach (ExpiryView v in near)
        {
            double tYears = OptionExposureMath.YearsToExpiry(v.Analysis.Expiration, asOf);
            foreach (OptionData o in v.Analysis.OptionData)
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
        }

        return result;
    }

    /// <summary>Объединяет цепочки близких экспираций по страйку (сумма CallOi/PutOi, IV — макс.).</summary>
    private static List<OptionData> AggregateChain(IReadOnlyList<ExpiryView> near)
    {
        var byStrike = new Dictionary<double, OptionData>();

        foreach (ExpiryView v in near)
        {
            foreach (OptionData o in v.Analysis.OptionData)
            {
                if (byStrike.TryGetValue(o.Strike, out OptionData? agg))
                {
                    agg.CallOi += o.CallOi;
                    agg.PutOi += o.PutOi;
                    if (o.Iv > agg.Iv) agg.Iv = o.Iv;
                }
                else
                {
                    byStrike[o.Strike] = new OptionData
                    {
                        Strike = o.Strike,
                        CallOi = o.CallOi,
                        PutOi = o.PutOi,
                        Iv = o.Iv
                    };
                }
            }
        }

        return byStrike.Values.OrderBy(o => o.Strike).ToList();
    }

    /// <summary>
    /// Центр тяжести близких — среднее <see cref="ExpirationAnalysis.GravityEquilibrium"/>,
    /// взвешенное по суммарному OI каждой экспирации. Если веса нулевые — возврат фолбэка фронта.
    /// </summary>
    private static double WeightedGravityEquilibrium(IReadOnlyList<ExpiryView> near, double fallback)
    {
        double weightedSum = 0;
        double totalWeight = 0;

        foreach (ExpiryView v in near)
        {
            double ge = v.Analysis.GravityEquilibrium;
            if (ge <= 0 || !double.IsFinite(ge))
                continue;

            double w = TotalOi(v.Analysis.OptionData);
            if (w <= 0)
                continue;

            weightedSum += ge * w;
            totalWeight += w;
        }

        if (totalWeight <= 0)
            return fallback;

        double result = weightedSum / totalWeight;
        return double.IsFinite(result) ? result : fallback;
    }

    /// <summary>Доля года до следующего сброса 08:00 UTC от текущего момента, минимум 1 час.</summary>
    private static double ComputeSessionYears()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var todayReset = new DateTimeOffset(now.Year, now.Month, now.Day, SessionResetHourUtc, 0, 0, TimeSpan.Zero);
        DateTimeOffset nextReset = now < todayReset ? todayReset : todayReset.AddDays(1);

        double hours = (nextReset - now).TotalHours;
        if (hours < 1.0) hours = 1.0; // минимум час до конца сессии

        return hours / (24.0 * 365.0);
    }

    private List<PriceLevel> BuildLevels(
        SessionRecommendation rec, double spot,
        OptionData? callWall, OptionData? putWall,
        double maxPain, double gravityNear, double? gammaFlip)
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
        Add(LevelKind.GravityEquilibrium, $"Центр тяжести {Fmt(gravityNear)}", gravityNear, "Магнит");

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
    /// Скоринг направления (раздел 8). Нормировки в [−1,+1], + = вверх. Каждый сигнал → BiasComponent
    /// с пояснением «потому что …». Веса перенормируются (Σw), skew пропускается при отсутствии RR.
    /// </summary>
    private void ScoreDirection(
        SessionRecommendation rec, double spot, double dailySigma1,
        ExpirationAnalysis front, IReadOnlyList<ExpiryView> near,
        double gravityNear, double? gammaFlip, double frontDte, bool gammaModelsDisagree)
    {
        var components = new List<BiasComponent>();
        double safeSigma = dailySigma1 > 0 && double.IsFinite(dailySigma1) ? dailySigma1 : spot * 0.01;

        // --- pin (тяга к Max Pain фронта) ---
        double pinRaw = front.MaxPain - spot;
        double pin = SessionAnalysisMath.Clamp(pinRaw / safeSigma, -1, 1);
        double pf = SessionAnalysisMath.Clamp(1 - frontDte / 7.0, 0.3, 1);
        components.Add(new BiasComponent
        {
            Name = "Пиннинг (Max Pain)",
            RawValue = pinRaw,
            Normalized = pin,
            Weight = WeightPinBase * pf,
            Explanation = $"потому что Max Pain фронта {Fmt(front.MaxPain)} {Direction(pinRaw)} спота {Fmt(spot)} " +
                          $"на {Fmt(Math.Abs(pinRaw))} (≈{pin:+0.00;-0.00;0.00}σ); вес усилен близостью экспирации (DTE {frontDte:0.0})."
        });

        // --- gravity (тяга к центру тяжести близких) ---
        // gravityNear вычислен один раз в Build и передан сюда — карта уровней, сценарии
        // и этот компонент гарантированно ссылаются на одно значение.
        double gravityRaw = gravityNear - spot;
        double gravity = SessionAnalysisMath.Clamp(gravityRaw / safeSigma, -1, 1);
        components.Add(new BiasComponent
        {
            Name = "Центр тяжести",
            RawValue = gravityRaw,
            Normalized = gravity,
            Weight = WeightGravity,
            Explanation = $"потому что центр тяжести близких {Fmt(gravityNear)} {Direction(gravityRaw)} спота " +
                          $"на {Fmt(Math.Abs(gravityRaw))} (≈{gravity:+0.00;-0.00;0.00}σ)."
        });

        // --- dex (долларовая дельта-экспозиция) ---
        double dexNear = near.Sum(v => v.Analysis.DollarDeltaExposure);
        double totalNearOi = TotalNearOi(near);
        double dexDenom = spot * totalNearOi;
        double dexRaw = dexNear;
        double dex = dexDenom > 0 && double.IsFinite(dexDenom)
            ? SessionAnalysisMath.Clamp(dexNear / dexDenom, -1, 1)
            : 0;
        components.Add(new BiasComponent
        {
            Name = "DEX (дельта-экспозиция)",
            RawValue = dexRaw,
            Normalized = dex,
            Weight = WeightDex,
            Explanation = $"потому что суммарная DEX близких {FmtUsd(dexNear)} → {(dex >= 0 ? "бычий" : "медвежий")} тилт " +
                          $"позиционирования (нормирована к спот·OI)."
        });

        // --- skew (25Δ Risk Reversal) — пропускается при отсутствии данных ---
        List<double> rrValues = near
            .Select(v => v.Analysis.RiskReversal25Delta)
            .Where(rr => rr.HasValue && double.IsFinite(rr.Value))
            .Select(rr => rr!.Value)
            .ToList();

        if (rrValues.Count > 0)
        {
            double rrAvg = rrValues.Average();
            double skew = -SessionAnalysisMath.Clamp(rrAvg / SkewNormalizer, -1, 1);
            components.Add(new BiasComponent
            {
                Name = "Скос (25Δ RR)",
                RawValue = rrAvg,
                Normalized = skew,
                Weight = WeightSkew,
                Explanation = $"потому что 25Δ Risk Reversal {rrAvg:+0.0;-0.0;0.0} п.в. — " +
                              (rrAvg > 0
                                  ? "путы дороже коллов (спрос на защиту вниз), медвежий риск."
                                  : "коллы дороже путов (жадность), бычий риск.")
            });
        }
        else
        {
            rec.Notes.Add("Скос (25Δ RR) недоступен — греков/IV недостаточно; сигнал пропущен, веса перенормированы.");
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
        // Не применяем, если профильная и греческая модели гаммы расходятся по знаку у спота:
        // иначе вывод «−гамма, импульс вниз» противоречил бы профилю с +гаммой у спота.
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
                Explanation = $"потому что режим отрицательной гаммы усиливает движение: спот {Fmt(spot)} " +
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

    /// <summary>Строит три сценария: База/Бык/Медведь. Стиль действия зависит от режима.</summary>
    private List<Scenario> BuildScenarios(
        SessionRecommendation rec, double spot,
        OptionData? callWall, OptionData? putWall,
        double maxPain, double gravityNear, double? gammaFlip)
    {
        bool positiveGamma = rec.Regime == VolatilityRegime.PositiveGamma;
        double lower1 = rec.Range.Lower1;
        double upper1 = rec.Range.Upper1;
        double lower2 = rec.Range.Lower2;
        double upper2 = rec.Range.Upper2;
        double callWallPrice = callWall?.Strike ?? upper1;
        double putWallPrice = putWall?.Strike ?? lower1;
        double magnet = maxPain > 0 ? maxPain : gravityNear;

        var scenarios = new List<Scenario>();

        // --- База (по режиму) ---
        Scenario baseScenario;
        if (positiveGamma)
        {
            baseScenario = new Scenario
            {
                Kind = ScenarioKind.Base,
                Title = "База: флэт / пиннинг",
                Trigger = $"цена удерживается в коридоре {Fmt(lower1)}…{Fmt(upper1)}",
                Targets = [magnet],
                Stop = null,
                Action = $"Фейдить края {Fmt(lower1)}…{Fmt(upper1)} к магниту {Fmt(magnet)}",
                Reason = $"потому что положительная гамма (Net GEX {FmtUsd(rec.NetGexAtSpot)}/1%) — " +
                         $"дилеры гасят волатильность, цену тянет к {Fmt(magnet)}.",
                Probability = 0.55
            };
        }
        else if (rec.Regime == VolatilityRegime.NegativeGamma)
        {
            baseScenario = new Scenario
            {
                Kind = ScenarioKind.Base,
                Title = "База: повышенная волатильность",
                Trigger = $"выход за {Fmt(lower1)} или {Fmt(upper1)}",
                Targets = rec.BiasScore >= 0 ? [upper1, upper2] : [lower1, lower2],
                // Стоп за противоположным краем диапазона (на защитной стороне относительно смещения).
                Stop = rec.BiasScore >= 0 ? lower1 : upper1,
                Action = "Входить по пробою в сторону смещения, не ловить ножи",
                Reason = $"потому что отрицательная гамма (Net GEX {FmtUsd(rec.NetGexAtSpot)}/1%) — " +
                         $"дилеры усиливают движения, склонность к пробою в сторону {BiasText(rec.Bias)}.",
                Probability = 0.50
            };
        }
        else
        {
            baseScenario = new Scenario
            {
                Kind = ScenarioKind.Base,
                Title = "База: нейтральный режим",
                Trigger = $"наблюдение коридора {Fmt(lower1)}…{Fmt(upper1)}",
                Targets = [magnet],
                Stop = null,
                Action = "Ждать подтверждения у границ диапазона",
                Reason = $"потому что Net GEX у спота близок к нулю ({FmtUsd(rec.NetGexAtSpot)}/1%) — " +
                         "чёткого режима нет.",
                Probability = 0.45
            };
        }
        scenarios.Add(baseScenario);

        // Направленные наборы уровней (структурные + σ-границы) по сторонам от спота.
        // Триггер пробоя = БЛИЖАЙШИЙ уровень в сторону движения; цели — уровни строго ЗА ним.
        // Так σ-края естественно становятся триггером, а стены/flip — дальними целями,
        // и цели/стоп всегда оказываются на верной стороне относительно триггера.
        double sigma = rec.Range.DailySigma1;
        double stopBuffer = Math.Max(sigma > 0 ? sigma : 0, spot * 0.0025);
        double flipVal = gammaFlip ?? double.NaN;
        double[] upLevels = [upper1, upper2, callWallPrice, magnet, flipVal];
        double[] downLevels = [lower1, lower2, putWallPrice, magnet, flipVal];

        // --- Бык ---
        if (positiveGamma)
        {
            // Фейд: покупка у ближайшей поддержки, цель — к центру (магнит/верхний край).
            double entry = NearestInDirection(downLevels, spot, -1, lower1);
            scenarios.Add(new Scenario
            {
                Kind = ScenarioKind.Bullish,
                Title = "Бычий сценарий",
                Trigger = $"откуп от поддержки {Fmt(entry)}",
                Targets = DirectionalTargets([magnet, upper1], entry, +1, spot, sigma),
                Stop = entry - stopBuffer,
                Action = $"Покупать у поддержки {Fmt(entry)} (фейд края)",
                Reason = $"потому что в положительной гамме откаты к {Fmt(entry)} выкупаются к магниту {Fmt(magnet)}.",
                Probability = rec.BiasScore >= 0 ? 0.45 : 0.30
            });
        }
        else
        {
            double trigger = NearestInDirection(upLevels, spot, +1, upper1);
            scenarios.Add(new Scenario
            {
                Kind = ScenarioKind.Bullish,
                Title = "Бычий сценарий",
                Trigger = $"закрепление выше {Fmt(trigger)}",
                Targets = DirectionalTargets(upLevels, trigger, +1, spot, sigma),
                Stop = trigger - stopBuffer,
                Action = $"Покупать по пробою вверх выше {Fmt(trigger)}",
                Reason = $"потому что пробой {Fmt(trigger)} в отрицательной гамме провоцирует ускорение вверх." +
                         FlipContinuation(gammaFlip, trigger, +1, spot),
                Probability = rec.BiasScore >= 0 ? 0.45 : 0.30
            });
        }

        // --- Медведь ---
        if (positiveGamma)
        {
            // Фейд: продажа у ближайшего сопротивления, цель — к центру (магнит/нижний край).
            double entry = NearestInDirection(upLevels, spot, +1, upper1);
            scenarios.Add(new Scenario
            {
                Kind = ScenarioKind.Bearish,
                Title = "Медвежий сценарий",
                Trigger = $"отбой от сопротивления {Fmt(entry)}",
                Targets = DirectionalTargets([magnet, lower1], entry, -1, spot, sigma),
                Stop = entry + stopBuffer,
                Action = $"Продавать у сопротивления {Fmt(entry)} (фейд края)",
                Reason = $"потому что в положительной гамме отскоки к {Fmt(entry)} продаются к магниту {Fmt(magnet)}.",
                Probability = rec.BiasScore < 0 ? 0.45 : 0.30
            });
        }
        else
        {
            double trigger = NearestInDirection(downLevels, spot, -1, lower1);
            scenarios.Add(new Scenario
            {
                Kind = ScenarioKind.Bearish,
                Title = "Медвежий сценарий",
                Trigger = $"пробой ниже {Fmt(trigger)}",
                Targets = DirectionalTargets(downLevels, trigger, -1, spot, sigma),
                Stop = trigger + stopBuffer,
                Action = $"Продавать по пробою вниз ниже {Fmt(trigger)}",
                Reason = $"потому что пробой {Fmt(trigger)} в отрицательной гамме провоцирует ускорение вниз." +
                         FlipContinuation(gammaFlip, trigger, -1, spot),
                Probability = rec.BiasScore < 0 ? 0.45 : 0.30
            });
        }

        return scenarios;
    }

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

    /// <summary>Заметка о продолжении за gamma-flip, если он лежит ЗА триггером в сторону движения.</summary>
    private static string FlipContinuation(double? gammaFlip, double trigger, int dir, double spot)
    {
        if (gammaFlip is not { } flip || !double.IsFinite(flip))
            return "";

        double eps = spot * 0.0005;
        bool ahead = dir > 0 ? flip > trigger + eps : flip < trigger - eps;
        return ahead ? $" Ускорение усилится при проходе gamma-flip {Fmt(flip)}." : "";
    }

    /// <summary>Макро-контекст по дальним экспирациям (DTE>14): крупнейшие OI-страйки и общий скос.</summary>
    private static List<string> BuildMacroContext(IReadOnlyList<ExpiryView> withDte)
    {
        var lines = new List<string>();
        List<ExpiryView> far = withDte.Where(v => v.Dte > NearDteThreshold).ToList();

        if (far.Count == 0)
        {
            lines.Add("Дальних экспираций (DTE > 14) в снимке нет — горизонт ограничен ближним сроком.");
            return lines;
        }

        // Агрегируем дальние цепочки по страйку для поиска крупнейших стен.
        var byStrike = new Dictionary<double, (double Call, double Put)>();
        foreach (ExpiryView v in far)
        {
            foreach (OptionData o in v.Analysis.OptionData)
            {
                byStrike.TryGetValue(o.Strike, out (double Call, double Put) acc);
                byStrike[o.Strike] = (acc.Call + o.CallOi, acc.Put + o.PutOi);
            }
        }

        if (byStrike.Count > 0)
        {
            KeyValuePair<double, (double Call, double Put)> topCall = byStrike.OrderByDescending(kv => kv.Value.Call).First();
            KeyValuePair<double, (double Call, double Put)> topPut = byStrike.OrderByDescending(kv => kv.Value.Put).First();
            lines.Add($"Дальний горизонт: крупнейший CALL-OI на {Fmt(topCall.Key)} ({Fmt(topCall.Value.Call)}), " +
                      $"PUT-OI на {Fmt(topPut.Key)} ({Fmt(topPut.Value.Put)}).");
        }

        List<double> rr = far
            .Select(v => v.Analysis.RiskReversal25Delta)
            .Where(x => x.HasValue && double.IsFinite(x.Value))
            .Select(x => x!.Value)
            .ToList();

        if (rr.Count > 0)
        {
            double avg = rr.Average();
            lines.Add($"Скос дальних экспираций {avg:+0.0;-0.0;0.0} п.в. — " +
                      (avg > 0 ? "стратегический спрос на защиту вниз." : "стратегический перевес коллов."));
        }

        return lines;
    }

    private static void AddEdgeCaseNotes(SessionRecommendation rec, double frontDte, double atmIv)
    {
        if (frontDte < 1.0)
            rec.Notes.Add("Экспирация фронта сегодня (DTE < 1) — усиленный пиннинг к Max Pain, движения у границ резкие.");

        if (atmIv <= 0)
            rec.Notes.Add("ATM IV недоступна или нулевая — диапазон 1σ/2σ может быть недостоверен.");

        if (rec.Range.DailySigma1 <= 0)
            rec.Notes.Add("Дневное σ не рассчитано — проверьте наличие IV в снимке.");
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
