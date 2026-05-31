using Option.Data.Shared.Dto;

namespace Option.Data.Ui.Models;

public enum DirectionBias { StrongDown, ModerateDown, Neutral, ModerateUp, StrongUp }
public enum VolatilityRegime { PositiveGamma, NegativeGamma, Neutral }
public enum LevelKind { Spot, CallWall, PutWall, MaxPain, GravityEquilibrium, GammaFlip, GammaPeak, Sigma1Up, Sigma1Down, Sigma2Up, Sigma2Down }
public enum ScenarioKind { Base, Bullish, Bearish }

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
    public List<PriceLevel> Levels { get; set; } = new();      // отсортированы по Price (убыв.)
    public List<Scenario> Scenarios { get; set; } = new();     // Base, Bullish, Bearish
    public List<BiasComponent> BiasComponents { get; set; } = new();
    public List<GammaProfilePoint> GammaProfile { get; set; } = new();
    public List<string> MacroContext { get; set; } = new();
    public List<string> Notes { get; set; } = new();           // предупреждения/краевые случаи
    public List<OptionData> FrontChain { get; set; } = new();  // для сворачиваемого блока HtmlBuilder
}

public class SessionRange
{
    public double AtmIvPercent { get; set; }           // σ фронта, % годовых
    public double SessionYears { get; set; }           // доля года до конца сессии
    public double DailySigma1 { get; set; }            // абсолют USD, 1σ за сессию
    public double Lower1 { get; set; }
    public double Upper1 { get; set; }
    public double Lower2 { get; set; }
    public double Upper2 { get; set; }
    public double FrontExpiryExpectedMove { get; set; } // EM до экспирации фронта (контекст)
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

public class Scenario
{
    public ScenarioKind Kind { get; set; }
    public string Title { get; set; } = "";
    public string Trigger { get; set; } = "";
    public List<double> Targets { get; set; } = new();
    public double? Stop { get; set; }
    public string Action { get; set; } = "";           // "Покупать у …"/"Продавать у …"/"Вне рынка"
    public string Reason { get; set; } = "";           // "потому что …"
    public double Probability { get; set; }            // 0..1, качественный вес
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
