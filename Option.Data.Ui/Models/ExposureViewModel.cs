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
}