namespace Option.Data.Ui.Models;

/// <summary>Профиль риска карточки продажи.</summary>
public enum SellProfile { Adaptive, Aggressive }

/// <summary>Состав рекомендованной продажи.</summary>
public enum SellLeg { Call, Put, Strangle }

/// <summary>Точка ряда цены (для EWMA-RV).</summary>
public readonly record struct PricePoint(DateTimeOffset Time, double Price);

/// <summary>Строка компактной таблицы уровней страницы /Sell.</summary>
public class SellLevel(string label, double price)
{
    public string Label { get; } = label;
    public double Price { get; } = price;
    public double DistancePercent { get; set; }
}

/// <summary>Кандидат на продажу: одна нога (CALL или PUT) одного страйка со всей экономикой.</summary>
public class SellCandidate
{
    public bool IsCall { get; init; }
    public double Strike { get; init; }
    /// <summary>Точное имя инструмента Deribit, напр. BTC-27JUN25-130000-C.</summary>
    public string Instrument { get; init; } = "";
    /// <summary>
    /// Премия продавца для анализа — берётся МАРК-цена (mark_price), а НЕ bid.
    /// Mark стабильна и математически согласована с моделью (≈ Black-76 по рыночной IV),
    /// тогда как bid/ask «человеческие» и шумные. USD.
    /// </summary>
    public double PremiumUsd { get; init; }
    /// <summary>Премия продавца (марк) в базовой монете.</summary>
    public double PremiumCoin { get; init; }
    public double MarketIvPct { get; init; }
    public double Delta { get; init; }
    /// <summary>P(экспирация в деньгах), риск-нейтрально по рыночной IV ноги.</summary>
    public double ProbItm { get; init; }
    /// <summary>P(касания страйка до экспирации) ≈ 2·P(ITM).</summary>
    public double ProbTouch { get; init; }
    /// <summary>Black-76 стоимость по ПРОГНОЗНОЙ σ_phys (модельная «справедливая» цена), USD.</summary>
    public double TheoPhysUsd { get; init; }
    /// <summary>Эдж продавца: PremiumUsd (mark) − TheoPhysUsd. Главный критерий ранжирования (на маржу).</summary>
    public double EvUsd { get; init; }
    public double MarginCoin { get; init; }
    public double MarginUsd { get; init; }
    public double EvOnMargin { get; init; }
    public double YieldOnMargin { get; init; }
    public double YieldAnnualizedPct { get; init; }
    public double ThetaPerDayUsd { get; init; }
    public double VegaPerVolPointUsd { get; init; }
    /// <summary>Страйк за GEX-стеной своей стороны (CALL: K ≥ CallWall; PUT: K ≤ PutWall).</summary>
    public bool BehindWall { get; init; }
    /// <summary>Премия ниже порога ликвидности (отсев грошовых хвостов).</summary>
    public bool TooSmallPremium { get; init; }
}

/// <summary>Триггер плана Б с конкретным уровнем/условием/действием.</summary>
public class PlanBTrigger
{
    public string Name { get; init; } = "";
    /// <summary>Ценовой уровень триггера (если триггер ценовой), USD.</summary>
    public double? Level { get; init; }
    public string Condition { get; init; } = "";
    public string Action { get; init; } = "";
}

/// <summary>Точка стресс-таблицы: PnL продавца при цене экспирации Price.</summary>
public class StressPoint
{
    public string Label { get; init; } = "";
    public double Price { get; init; }
    public double PnlUsd { get; init; }
}

/// <summary>Карточка рекомендации одного профиля (либо StandAside с причиной).</summary>
public class SellCard
{
    public SellProfile Profile { get; init; }
    public bool IsStandAside { get; init; }
    public string StandAsideReason { get; init; } = "";
    public string ReturnCondition { get; init; } = "";
    public SellLeg Leg { get; set; }
    /// <summary>Нога рекомендации (для стрэнгла — CALL-нога).</summary>
    public SellCandidate? Primary { get; set; }
    /// <summary>PUT-нога стрэнгла; null — одиночная нога.</summary>
    public SellCandidate? SecondLeg { get; set; }
    /// <summary>P(касания хотя бы одного страйка); для одиночной ноги = ProbTouch ноги.</summary>
    public double CombinedProbTouch { get; set; }
    public double TotalPremiumUsd { get; set; }
    public double TotalPremiumCoin { get; set; }
    public double TotalMarginCoin { get; set; }
    public double TotalMarginUsd { get; set; }
    public double YieldOnMarginPct { get; set; }
    public double YieldAnnualizedPct { get; set; }
    public double TotalEvUsd { get; set; }
    public List<PlanBTrigger> PlanB { get; set; } = new();
    public List<StressPoint> Stress { get; set; } = new();
    public List<string> Warnings { get; } = new();
}

/// <summary>Результат билдера /Sell: контекст рынка + две карточки + прозрачность.</summary>
public class SellRecommendation
{
    public string Currency { get; init; } = "";
    public string Expiration { get; init; } = "";
    public DateTimeOffset AsOf { get; init; }
    public double Forward { get; set; }
    public double TYears { get; set; }
    public double DteDays { get; set; }
    public VolatilityRegime Regime { get; set; }
    public double NetGexAtSpot { get; set; }
    public double? GammaFlip { get; set; }
    public double? CallWall { get; set; }
    public double? PutWall { get; set; }
    public double AtmIvPct { get; set; }
    /// <summary>EWMA реализованная вола из истории БД, % годовых (0 — истории нет).</summary>
    public double RvAnnualPct { get; set; }
    /// <summary>Прогнозная физическая вола (RV×множитель режима, клэмп к IV), % годовых.</summary>
    public double SigmaPhysPct { get; set; }
    public double SessionSigmaUsd { get; set; }
    /// <summary>Лог-нормальные границы ±1σ/±2σ по σ_phys до экспирации.</summary>
    public (double Lower, double Upper) Band1 { get; set; }
    public (double Lower, double Upper) Band2 { get; set; }
    public double BiasScore { get; set; }
    public List<BiasComponent> BiasComponents { get; set; } = new();
    public List<SellLevel> Levels { get; set; } = new();
    public SellCard Adaptive { get; set; } = new() { Profile = SellProfile.Adaptive, IsStandAside = true, StandAsideReason = "не рассчитано" };
    public SellCard Aggressive { get; set; } = new() { Profile = SellProfile.Aggressive, IsStandAside = true, StandAsideReason = "не рассчитано" };
    /// <summary>Топ кандидатов обеих ног по EV/маржа (таблица прозрачности).</summary>
    public List<SellCandidate> TopCandidates { get; set; } = new();
    public List<string> Notes { get; } = new();
}
