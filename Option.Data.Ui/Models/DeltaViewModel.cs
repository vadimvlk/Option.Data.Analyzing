namespace Option.Data.Ui.Models;

public class DeltaViewModel
{
    public List<DeltaPoint> Series { get; set; } = new();

    /// <summary>Цена базового актива в последнем снимке выбранной экспирации.</summary>
    public double Spot { get; set; }

    /// <summary>Net GEX у спота (USD на 1% движения) — последний снимок выбранной экспирации.</summary>
    public double NetGexAtSpot { get; set; }

    /// <summary>Gamma-flip: цена смены знака Net GEX (null — если профиль не пересекает ноль).</summary>
    public double? GammaFlip { get; set; }

    /// <summary>Профиль Net GEX по цене (Black-76), последний снимок выбранной экспирации.</summary>
    public List<GammaProfilePoint> GammaProfile { get; set; } = new();

    /// <summary>Вклад каждого страйка в Net GEX у спота (USD на 1% движения).</summary>
    public List<StrikeGex> StrikeGex { get; set; } = new();
}

/// <summary>Net GEX отдельного страйка (USD на 1% движения) в последнем снимке.</summary>
public class StrikeGex
{
    public double Strike { get; set; }
    public double NetGex { get; set; }
}

public class DeltaPoint
{
    /// <summary>
    /// Момент записи данных (запуск Job), DeribitData.CreatedAt.
    /// </summary>
    public DateTimeOffset Time { get; set; }

    /// <summary>
    /// Цена базового актива на этот момент (Max UnderlyingPrice среди строк экспирации).
    /// </summary>
    public double UnderlyingPrice { get; set; }

    /// <summary>
    /// Суммарная Delta-экспозиция: -Σ(Delta · OpenInterest) по всем строкам момента.
    /// </summary>
    public double DeltaExposure { get; set; }
}
