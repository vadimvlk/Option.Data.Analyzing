using Option.Data.Shared.Dto;

namespace Option.Data.Ui.Models;

public class ExposureViewModel
{
    public List<ExpirationAnalysis> ExpirationsData { get; set; } = new();
}

public class ExpirationAnalysis
{
    public string Expiration { get; set; } = string.Empty;
    public double UnderlyingPrice { get; set; }
    public List<OptionData> OptionData { get; set; } = new();
    public double CallCenterOfGravity { get; set; }
    public double PutCenterOfGravity { get; set; }
    public double GravityEquilibrium { get; set; }
    public double? UpperBoundary { get; set; }
    public double? LowerBoundary { get; set; }

    /// <summary>DEX — долларовая дельта-экспозиция продавца, USD на $1 движения спота.</summary>
    public double DollarDeltaExposure { get; set; }

    /// <summary>Net GEX — дилерская гамма-экспозиция, USD на 1% движения; знак задаёт режим хеджирования.</summary>
    public double NetGammaExposure { get; set; }

    /// <summary>Max Pain — страйк минимальной суммарной внутренней стоимости опционов.</summary>
    public double MaxPain { get; set; }

    /// <summary>Ожидаемое движение 1σ к экспирации, USD (S·σ_ATM·√T).</summary>
    public double ExpectedMove1Sigma { get; set; }

    /// <summary>25Δ Risk Reversal (скос), пункты волатильности; null — греков недостаточно.</summary>
    public double? RiskReversal25Delta { get; set; }
}