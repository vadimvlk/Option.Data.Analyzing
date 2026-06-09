using Option.Data.Shared.Dto;

namespace Option.Data.Ui.Models;

/// <summary>Биржа-источник данных опционов для страницы Snapshot.</summary>
public enum ExchangeSource
{
    Deribit = 0,
    Binance = 1
}

/// <summary>
/// Нормализованная доска опционов ОДНОЙ экспирации, не зависящая от биржи:
/// цепочка по страйкам (<see cref="OptionData"/>) и спот. IV — в процентах, OI — в монетах.
/// </summary>
public sealed record OptionBoard(List<OptionData> Chain, double Spot)
{
    public static OptionBoard Empty { get; } = new(new List<OptionData>(), 0);
}
