using Option.Data.Shared.Dto;

namespace Option.Data.Ui.Models;

public enum DirectionBias { StrongDown, ModerateDown, Neutral, ModerateUp, StrongUp }
public enum VolatilityRegime { PositiveGamma, NegativeGamma, Neutral }
public enum LevelKind { Spot, CallWall, PutWall, MaxPain, GravityEquilibrium, GammaFlip, GammaPeak, Sigma1Up, Sigma1Down, Sigma2Up, Sigma2Down }

/// <summary>
/// Тип главной сделки сессии. Выбирается ИЗ РЕЖИМА гаммы (а не из направления):
/// +гамма → <see cref="FadeRange"/> (возврат к середине от стен),
/// −гамма → <see cref="Breakout"/> (движение по импульсу),
/// нейтральная гамма с согласованными сигналами → <see cref="Directional"/>,
/// иначе → <see cref="StandAside"/>.
/// </summary>
public enum TradeAction { FadeRange, Breakout, Directional, StandAside }

/// <summary>Сторона главной сделки.</summary>
public enum TradeSide { Long, Short, None }

public class SessionRecommendation
{
    public string Currency { get; set; } = "";
    public double Spot { get; set; }
    public string FrontExpiration { get; set; } = "";
    public double FrontDaysToExpiry { get; set; }
    public double HoursToFrontExpiry { get; set; }
    public DateTimeOffset AsOf { get; set; }

    public DirectionBias Bias { get; set; }

    /// <summary>Сводный балл направления −1…+1 = взвешенная сумма прозрачных сигналов <see cref="BiasComponents"/>.</summary>
    public double BiasScore { get; set; }

    public VolatilityRegime Regime { get; set; }
    public double NetGexAtSpot { get; set; }           // USD/1%
    public double? GammaFlip { get; set; }

    /// <summary>true — режим «Сводка» (агрегат ближних + квартальной экспираций).</summary>
    public bool IsAggregated { get; set; }

    /// <summary>Число экспираций в окне агрегации (для подписи). 1 — одиночная.</summary>
    public int AggregatedCount { get; set; } = 1;

    public SessionRange Range { get; set; } = new();

    /// <summary>Главная сделка сессии — единственная конкретная рекомендация.</summary>
    public PrimaryTrade Primary { get; set; } = new();

    public List<PriceLevel> Levels { get; set; } = new();      // отсортированы по Price (убыв.)

    /// <summary>Прозрачная таблица сигналов направления (рендерится на странице вместо «конвикции»).</summary>
    public List<BiasComponent> BiasComponents { get; set; } = new();

    public List<GammaProfilePoint> GammaProfile { get; set; } = new();
    public List<string> Notes { get; set; } = new();           // предупреждения/краевые случаи (внутреннее, не рендерится)
    public List<OptionData> FrontChain { get; set; } = new();  // для сворачиваемого блока HtmlBuilder
}

public class SessionRange
{
    public double AtmIvPercent { get; set; }           // σ_ATM выбранной экспирации, % годовых

    /// <summary>T — доля года до выбранного горизонта (экспирации/квартальной).</summary>
    public double HorizonYears { get; set; }

    /// <summary>1σ ожидаемого движения до горизонта, USD: S·σ_ATM·√T (для подписи диапазона).</summary>
    public double Sigma1Usd { get; set; }

    /// <summary>
    /// σ ОДНОЙ торговой сессии (1 день, но не дальше экспирации), USD: S·σ_ATM·√(min(T, 1/365)).
    /// σ_ATM здесь — IV БЛИЖАЙШЕЙ экспирации окна (в «Сводке»), а не квартальной: суточный
    /// масштаб задаёт фронт. Используется для зон входа, стопов и буферов — в отличие от
    /// σ до горизонта, которая на дальних экспирациях на порядок шире сессии.
    /// </summary>
    public double SessionSigmaUsd { get; set; }

    // Лог-нормальные границы до горизонта: S·e^{∓kσ√T}. Никогда не отрицательны
    // (в отличие от прежних арифметических S∓kσ, уходивших ниже нуля при больших σ√T).
    public double Lower1 { get; set; }
    public double Upper1 { get; set; }
    public double Lower2 { get; set; }
    public double Upper2 { get; set; }
}

public class PriceLevel
{
    public LevelKind Kind { get; set; }
    public string Label { get; set; } = "";            // напр. "CALL-стена 2100"
    public double Price { get; set; }
    public string Role { get; set; } = "";             // "Сопротивление"/"Поддержка"/"Магнит"/"Пивот"/"Спот"/"Граница"
    public double? OpenInterest { get; set; }          // для стен
    public double DistancePercent { get; set; }        // (Price-Spot)/Spot*100
}

/// <summary>
/// Главная сделка сессии. Может быть АКТИВНОЙ (вход от текущей цены) или ОТЛОЖЕННОЙ
/// (<see cref="IsConditional"/> = true): зона входа лежит у края диапазона, активация —
/// по условию <see cref="Trigger"/>. При <see cref="Action"/> = StandAside поля входа/цели/стопа
/// пусты, заполнено <see cref="Setup"/>.
/// R:R считается всегда и является ФИЛЬТРОМ: план с R:R ниже порога своего типа
/// (fade ≥ 1.3, breakout ≥ 1.5, directional ≥ 1.4) не публикуется — вместо него StandAside.
/// </summary>
public class PrimaryTrade
{
    public TradeAction Action { get; set; }
    public TradeSide Side { get; set; }
    public string Headline { get; set; } = "";        // "ФЕЙД ДИАПАЗОНА — Long от PUT-стены 2400 → 2520"

    /// <summary>true — отложенный вход: ждать условия <see cref="Trigger"/> (цена ещё не в зоне).</summary>
    public bool IsConditional { get; set; }

    /// <summary>Условие активации отложенного входа (пусто для активного входа).</summary>
    public string Trigger { get; set; } = "";

    public double? EntryLow { get; set; }
    public double? EntryHigh { get; set; }
    public double? Target { get; set; }
    public double? Stop { get; set; }
    public string Invalidation { get; set; } = "";     // условие отмены идеи
    public double? RiskReward { get; set; }
    public string Reason { get; set; } = "";           // одна строка «почему»
    public List<string> Drivers { get; set; } = new(); // топ 1-2 драйвера
    public string PlanB { get; set; } = "";            // одна строка плана-Б
    public string Setup { get; set; } = "";            // для StandAside: условие появления сетапа
}

/// <summary>
/// Один сигнал направления. Все сигналы и веса показываются пользователю как есть —
/// это замена непрозрачного индекса «конвикции».
/// </summary>
public class BiasComponent
{
    public string Name { get; set; } = "";
    public double RawValue { get; set; }
    public double Normalized { get; set; }             // -1..+1 (+ = бычий)
    public double Weight { get; set; }
    public double Contribution { get; set; }           // Normalized*Weight (после нормировки весов)
    public string Explanation { get; set; } = "";
}

public class GammaProfilePoint
{
    public double Price { get; set; }
    public double NetGex { get; set; }                 // USD/1%
}
