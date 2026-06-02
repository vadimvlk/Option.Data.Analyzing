using Option.Data.Shared.Dto;

namespace Option.Data.Ui.Models;

public enum DirectionBias { StrongDown, ModerateDown, Neutral, ModerateUp, StrongUp }
public enum VolatilityRegime { PositiveGamma, NegativeGamma, Neutral }
public enum LevelKind { Spot, CallWall, PutWall, MaxPain, GravityEquilibrium, GammaFlip, GammaPeak, Sigma1Up, Sigma1Down, Sigma2Up, Sigma2Down }

/// <summary>Тип действия главной сделки сессии.</summary>
public enum TradeAction { FadeRange, Breakout, StandAside }

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
    public double BiasScore { get; set; }              // -1..+1
    public VolatilityRegime Regime { get; set; }
    public double NetGexAtSpot { get; set; }           // USD/1%
    public double? GammaFlip { get; set; }

    public SessionRange Range { get; set; } = new();

    /// <summary>Главная сделка сессии — единственная конкретная рекомендация (раздел B спека).</summary>
    public PrimaryTrade Primary { get; set; } = new();

    public List<PriceLevel> Levels { get; set; } = new();      // отсортированы по Price (убыв.)
    public List<BiasComponent> BiasComponents { get; set; } = new();
    public List<GammaProfilePoint> GammaProfile { get; set; } = new();
    public List<string> Notes { get; set; } = new();           // предупреждения/краевые случаи (внутреннее, не рендерится)
    public List<OptionData> FrontChain { get; set; } = new();  // для сворачиваемого блока HtmlBuilder
}

public class SessionRange
{
    public double AtmIvPercent { get; set; }           // σ_ATM выбранной экспирации, % годовых
    public double SessionYears { get; set; }           // T — доля года до выбранной экспирации
    public double DailySigma1 { get; set; }            // абсолют USD, 1σ до выбранной экспирации (S·σ·√T)
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
/// Главная сделка сессии: одна конкретная рекомендация вместо трёх параллельных сценариев.
/// При <see cref="Action"/> = StandAside поля входа/цели/стопа пусты, заполнено <see cref="Setup"/>.
/// </summary>
public class PrimaryTrade
{
    public TradeAction Action { get; set; }
    public TradeSide Side { get; set; }
    public string Headline { get; set; } = "";        // "ФЕЙД ДИАПАЗОНА — Short от верхнего края к магниту"
    public double? EntryLow { get; set; }
    public double? EntryHigh { get; set; }
    public double? Target { get; set; }
    public double? Stop { get; set; }
    public string Invalidation { get; set; } = "";     // условие отмены идеи
    public double? RiskReward { get; set; }
    public int Conviction { get; set; }                // 0..100
    public string ConvictionLabel { get; set; } = "";  // Высокая/Средняя/Низкая
    public string Reason { get; set; } = "";           // одна строка «почему»
    public List<string> Drivers { get; set; } = new(); // топ 1-2 драйвера
    public string PlanB { get; set; } = "";            // одна строка плана-Б
    public string Setup { get; set; } = "";            // для StandAside: условие появления сетапа
}

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
