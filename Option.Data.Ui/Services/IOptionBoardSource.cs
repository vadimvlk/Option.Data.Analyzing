using Option.Data.Ui.Models;

namespace Option.Data.Ui.Services;

/// <summary>
/// Источник live-доски опционов одной биржи. Реализации нормализуют данные к общему виду
/// (<see cref="OptionBoard"/>): IV в процентах, коды экспираций в формате <c>dMMMyy</c>,
/// OI в монетах. За кэширование сетевых ответов отвечает реализация.
/// </summary>
public interface IOptionBoardSource
{
    /// <summary>Биржа, которую обслуживает источник.</summary>
    ExchangeSource Exchange { get; }

    /// <summary>Доступные коды экспираций (формат <c>dMMMyy</c>) для валюты (BTC/ETH).</summary>
    Task<List<string>> GetExpirationsAsync(string currency);

    /// <summary>Доска выбранной валюты и экспирации (или <see cref="OptionBoard.Empty"/>).</summary>
    Task<OptionBoard> GetBoardAsync(string currency, string expiration);
}

/// <summary>Возвращает источник по выбранной бирже.</summary>
public interface IOptionBoardSourceResolver
{
    IOptionBoardSource Get(ExchangeSource exchange);
}
