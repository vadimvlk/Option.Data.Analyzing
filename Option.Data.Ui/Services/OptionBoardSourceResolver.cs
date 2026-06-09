using Option.Data.Ui.Models;

namespace Option.Data.Ui.Services;

/// <summary>Резолвит <see cref="IOptionBoardSource"/> по <see cref="ExchangeSource"/>; дефолт — Deribit.</summary>
public sealed class OptionBoardSourceResolver : IOptionBoardSourceResolver
{
    private readonly Dictionary<ExchangeSource, IOptionBoardSource> _map;

    public OptionBoardSourceResolver(IEnumerable<IOptionBoardSource> sources)
        => _map = sources.ToDictionary(s => s.Exchange);

    public IOptionBoardSource Get(ExchangeSource exchange)
        => _map.TryGetValue(exchange, out IOptionBoardSource? source) ? source : _map[ExchangeSource.Deribit];
}
