namespace Option.Data.Ui.Models;

/// <summary>
/// Результат расчёта гамма-экспозиции (Net GEX) по цепочке ОДНОЙ экспирации:
/// профиль Net GEX по цене (Black-76), gamma-flip и вклад каждого страйка у спота.
/// Заполняется <see cref="Option.Data.Ui.Services.NetGexCalculator"/> и переиспользуется
/// страницами Snapshot и Delta (партиал <c>_GammaProfile.cshtml</c>).
/// </summary>
public class GammaView
{
    /// <summary>Цена базового актива (спот/форвард) снимка.</summary>
    public double Spot { get; set; }

    /// <summary>Net GEX у спота (USD на 1% движения).</summary>
    public double NetGexAtSpot { get; set; }

    /// <summary>Gamma-flip: цена смены знака Net GEX (null — профиль не пересекает ноль).</summary>
    public double? GammaFlip { get; set; }

    /// <summary>Профиль Net GEX по цене (Black-76).</summary>
    public List<GammaProfilePoint> GammaProfile { get; set; } = new();

    /// <summary>Вклад каждого страйка в Net GEX у спота (USD на 1% движения).</summary>
    public List<StrikeGex> StrikeGex { get; set; } = new();

    /// <summary>Есть ли что рисовать (профиль построен).</summary>
    public bool HasData => GammaProfile.Count > 0;
}
