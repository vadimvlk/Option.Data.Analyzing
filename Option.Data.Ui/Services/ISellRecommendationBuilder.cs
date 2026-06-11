using Option.Data.Ui.Models;

namespace Option.Data.Ui.Services;

/// <summary>
/// Синтез рекомендации непокрытой продажи опционов по живой доске одной экспирации.
/// Чистая функция: вся загрузка данных — на вызывающей стороне.
/// </summary>
public interface ISellRecommendationBuilder
{
    /// <param name="board">Живая доска (bid/ask заполнены Deribit-источником).</param>
    /// <param name="currency">BTC/ETH.</param>
    /// <param name="expiration">Код экспирации dMMMyy.</param>
    /// <param name="asOf">Момент расчёта.</param>
    /// <param name="dexSeries">История DEX выбранной экспирации из БД (может быть пустой).</param>
    /// <param name="priceHistory">3ч-ряд цены фронта из БД для EWMA-RV (может быть пустым).</param>
    SellRecommendation Build(
        OptionBoard board, string currency, string expiration, DateTimeOffset asOf,
        IReadOnlyList<DeltaPoint> dexSeries, IReadOnlyList<PricePoint> priceHistory);
}
