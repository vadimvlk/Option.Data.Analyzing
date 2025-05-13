using Option.Data.Shared.Dto;

namespace Option.Data.Ui.Services;

public interface IOptionsAnalysisHtmlBuilder
{
    string CalculateCallPutRatioHtml(List<OptionData> data);
    string CalculateMaxPainHtml(List<OptionData> data, double currentPrice);
    string AnalyzeOpenInterestHtml(List<OptionData> data);
    string AnalyzeCentersOfGravityHtml(List<OptionData> data, double currentPrice);
    string CalculateProfitLossHtml(List<OptionData> data, double currentPrice);
    string AnalyzeGlobalSellerPositionHtml(List<OptionData> data, double currentPrice);

    string AnalyzePriceMovementPotentialHtml(List<OptionData> data, double currentPrice);
}