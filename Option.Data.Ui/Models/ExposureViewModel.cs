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

    /// <summary>
    /// «Равновесная цена» = среднее Call- и Put-центров (callCog+putCog)/2.
    /// Используется на странице Exposure. НЕ путать с <see cref="OiCentroid"/>.
    /// </summary>
    public double GravityEquilibrium { get; set; }

    /// <summary>
    /// OI-взвешенный центр тяжести всей цепочки Σ(K·(CallOi+PutOi))/Σ(CallOi+PutOi) —
    /// «магнит» цены с учётом фактического распределения открытого интереса (в отличие от
    /// <see cref="GravityEquilibrium"/>, который усредняет два центра 50/50). 0 — недоступен
    /// (нулевой суммарный OI). Используется как магнит на странице Trade.
    /// </summary>
    public double OiCentroid { get; set; }

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