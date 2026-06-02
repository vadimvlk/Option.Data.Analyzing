using Option.Data.Ui.Models;

namespace Option.Data.Ui.Services;

public interface ISessionRecommendationBuilder
{
    SessionRecommendation Build(
        ExpirationAnalysis selected,
        string currency,
        DateTimeOffset asOf);
}
