namespace Option.Data.Analyzing.Models;

public record OptionData
{
    /// <summary>
    /// Открытые позиции Call.
    /// </summary>
    public double CallOi { get; set; }

    /// <summary>
    ///  Страйк.
    /// </summary>
    public double Strike { get; set; }

    /// <summary>
    /// Подразумеваемая волатильность.
    /// </summary>
    public double Iv { get; set; }

    /// <summary>
    ///  Открытые позиции Put.
    /// </summary>
    public double PutOi { get; set; }

    /// <summary>
    /// Теоретическая стоимость Call.
    /// </summary>
    public double CallPrice { get; set; }

    /// <summary>
    /// Теоретическая стоимость Put.
    /// </summary>
    public double PutPrice { get; set; }

    /// <summary>
    /// Дельта опциона Call.
    /// </summary>
    public double CallDelta { get; set; }

    /// <summary>
    ///  Гамма опциона Call.
    /// </summary>
    public double CallGamma { get; set; }

    /// <summary>
    /// Дельта опциона Put.
    /// </summary>
    public double PutDelta { get; set; }

    /// <summary>
    ///  Гамма опциона Put.
    /// </summary>
    public double PutGamma { get; set; }
}