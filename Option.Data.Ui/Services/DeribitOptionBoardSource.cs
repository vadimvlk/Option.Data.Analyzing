using System.Web;
using System.Net.Http.Json;
using Option.Data.Ui.Models;
using Option.Data.Shared.Dto;
using Option.Data.Shared.Configuration;
using Microsoft.Extensions.Caching.Memory;

namespace Option.Data.Ui.Services;

/// <summary>
/// Источник доски Deribit: один bulk-вызов <c>get_book_summary_by_currency</c> (греков нет —
/// гамму считает <see cref="NetGexCalculator"/> по mark_iv). Логика перенесена из SnapshotModel.
/// </summary>
public sealed class DeribitOptionBoardSource : IOptionBoardSource
{
    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;
    private readonly ILogger<DeribitOptionBoardSource> _logger;

    public DeribitOptionBoardSource(IHttpClientFactory factory, IMemoryCache cache, ILogger<DeribitOptionBoardSource> logger)
    {
        _http = factory.CreateClient(DeribitConfig.ClientName);
        _cache = cache;
        _logger = logger;
    }

    public ExchangeSource Exchange => ExchangeSource.Deribit;

    public async Task<List<string>> GetExpirationsAsync(string currency)
    {
        // Как и прежде на Snapshot — все опционные экспирации (currency=any).
        return (await _cache.GetOrCreateAsync("DeribitExpirations", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15);
            Expirations? expirations =
                await _http.GetFromJsonAsync<Expirations>("get_expirations?currency=any&kind=option");
            return expirations?.Data?.Options ?? new List<string>();
        }))!;
    }

    public async Task<OptionBoard> GetBoardAsync(string currency, string expiration)
    {
        List<BookSummaryData> data = (await _cache.GetOrCreateAsync($"DeribitBook_{currency}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            var qp = HttpUtility.ParseQueryString(string.Empty);
            qp["currency"] = currency;
            qp["kind"] = "option";
            BookSummaryByInstrument? summary =
                await _http.GetFromJsonAsync<BookSummaryByInstrument>($"get_book_summary_by_currency?{qp}");
            return summary?.Data ?? new List<BookSummaryData>();
        }))!;

        if (data.Count == 0)
            return OptionBoard.Empty;

        List<ParsedRow> parsed = data
            .Where(d => !string.IsNullOrEmpty(d.InstrumentName))
            .Select(d =>
            {
                try { return new ParsedRow(d, ParseInstrumentName(d.InstrumentName!)); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse instrument name: {Instrument}", d.InstrumentName);
                    return null;
                }
            })
            .Where(x => x is not null && x.Info.Expiration == expiration)
            .Select(x => x!)
            .ToList();

        if (parsed.Count == 0)
            return OptionBoard.Empty;

        Dictionary<int, BookSummaryData> calls = parsed
            .Where(x => x.Info.Type == "Call")
            .ToDictionary(x => x.Info.Strike, x => x.Data);
        Dictionary<int, BookSummaryData> puts = parsed
            .Where(x => x.Info.Type == "Put")
            .ToDictionary(x => x.Info.Strike, x => x.Data);

        double spot = parsed.Max(x => x.Data.UnderlyingPrice) ?? 0;

        List<OptionData> chain = parsed
            .Select(x => x.Info.Strike)
            .ToHashSet()
            .OrderBy(strike => strike)
            .Select(strike =>
            {
                calls.TryGetValue(strike, out var call);
                puts.TryGetValue(strike, out var put);
                return new OptionData
                {
                    Strike = strike,
                    CallOi = call?.OpenInterest ?? 0,
                    CallPrice = call?.MarkPrice * call?.UnderlyingPrice ?? 0,
                    Iv = call?.MarkIv ?? (put?.MarkIv ?? 0),
                    PutIv = put?.MarkIv ?? 0,
                    PutOi = put?.OpenInterest ?? 0,
                    PutPrice = put?.MarkPrice * put?.UnderlyingPrice ?? 0,
                    CallBid = call?.BidPrice * call?.UnderlyingPrice ?? 0,
                    CallAsk = call?.AskPrice * call?.UnderlyingPrice ?? 0,
                    PutBid = put?.BidPrice * put?.UnderlyingPrice ?? 0,
                    PutAsk = put?.AskPrice * put?.UnderlyingPrice ?? 0
                };
            })
            .ToList();

        return new OptionBoard(chain, spot);
    }

    private sealed record ParsedRow(
        BookSummaryData Data,
        (string Currency, string Expiration, int Strike, string Type) Info);

    private static (string Currency, string Expiration, int Strike, string Type) ParseInstrumentName(string instrumentName)
    {
        // Пример: "ETH-25JUL25-2400-C".
        string[] parts = instrumentName.Split('-');
        if (parts.Length != 4)
            throw new ArgumentException($"Invalid instrument name format: {instrumentName}");

        string currency = parts[0];
        string expiration = parts[1];
        if (!int.TryParse(parts[2], out int strike))
            throw new ArgumentException($"Invalid strike price: {parts[2]}");

        string optionType = parts[3] switch
        {
            "C" => "Call",
            "P" => "Put",
            _ => throw new ArgumentOutOfRangeException($"Unknown option type: {parts[3]}")
        };

        return (currency, expiration, strike, optionType);
    }
}
