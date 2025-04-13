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
}