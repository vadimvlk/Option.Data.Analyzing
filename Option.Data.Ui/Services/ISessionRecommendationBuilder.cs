using Option.Data.Ui.Models;

namespace Option.Data.Ui.Services;

public interface ISessionRecommendationBuilder
{
    SessionRecommendation Build(
        IReadOnlyList<ExpirationAnalysis> analyses,
        string currency,
        DateTimeOffset asOf);
}
