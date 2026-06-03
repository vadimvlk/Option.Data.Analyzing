using Option.Data.Ui.Models;

namespace Option.Data.Ui.Services;

public interface ISessionRecommendationBuilder
{
    SessionRecommendation Build(
        ExpirationAnalysis selected,
        string currency,
        DateTimeOffset asOf);

    /// <summary>
    /// Агрегированный план по окну экспираций (ближние + квартальная). Профиль гаммы
    /// собирается по всем экспирациям окна; Max Pain/стены/центроид/детали — по сводной
    /// цепочке; σ и подпись горизонта — по самой дальней экспирации окна.
    /// </summary>
    SessionRecommendation BuildAggregate(
        IReadOnlyList<ExpirationAnalysis> window,
        string currency,
        DateTimeOffset asOf);
}
