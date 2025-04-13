namespace Option.Data.Analyzing.Models;

public record PainResult
{
    public double Strike { get; set; }
    public double CallLosses { get; set; }
    public double PutLosses { get; set; }
    public double TotalLosses { get; set; }
}