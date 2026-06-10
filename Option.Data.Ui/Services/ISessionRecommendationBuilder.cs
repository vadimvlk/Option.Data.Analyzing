using Option.Data.Ui.Models;

namespace Option.Data.Ui.Services;

public interface ISessionRecommendationBuilder
{
    /// <param name="dexFlowTrend">
    /// Тренд очищенного от цены потока дельта-экспозиции, −1…+1 (см. <see cref="SessionAnalysisMath.DexTrend"/>):
    /// &gt;0 — DEX растёт (накопление защитных позиций; эмпирически контрарно-бычий маркер),
    /// &lt;0 — падает (разгрузка защиты; медвежий), 0 — нет истории/сигнала.
    /// </param>
    SessionRecommendation Build(
        ExpirationAnalysis selected,
        string currency,
        DateTimeOffset asOf,
        double dexFlowTrend = 0);

    /// <summary>
    /// Агрегированный план по окну экспираций (ближние + квартальная). Профиль гаммы
    /// собирается по всем экспирациям окна; Max Pain/стены/центроид/детали — по сводной
    /// цепочке; σ и подпись горизонта — по самой дальней экспирации окна; скос 25Δ RR —
    /// по ближайшей экспирации окна, где он рассчитывается.
    /// </summary>
    SessionRecommendation BuildAggregate(
        IReadOnlyList<ExpirationAnalysis> window,
        string currency,
        DateTimeOffset asOf,
        double dexFlowTrend = 0);
}
