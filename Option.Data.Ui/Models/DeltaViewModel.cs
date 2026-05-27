namespace Option.Data.Ui.Models;

public class DeltaViewModel
{
    public List<DeltaPoint> Series { get; set; } = new();
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
