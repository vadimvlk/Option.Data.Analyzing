namespace Option.Data.Analyzing.Models;

public record OptionData
{
    public double CallOi { get; set; }         // Открытые позиции Call
    public double Strike { get; set; }         // Страйк
    public double Iv { get; set; }             // Подразумеваемая волатильность
    public double PutOi { get; set; }          // Открытые позиции Put
}