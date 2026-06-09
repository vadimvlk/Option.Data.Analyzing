using System.Net.Http.Json;
using System.Globalization;
using System.Text.Json.Serialization;
using Option.Data.Ui.Models;
using Option.Data.Shared.Dto;
using Option.Data.Shared.Configuration;
using Microsoft.Extensions.Caching.Memory;

namespace Option.Data.Ui.Services;

/// <summary>
/// Источник доски Binance Options (eapi). Греки отдаются bulk-вызовом <c>/eapi/v1/mark</c>,
/// но для S-кривой нужна гамма как функция цены — поэтому используем markIV и считаем
/// Black-76 в <see cref="NetGexCalculator"/> (как и для Deribit).
///
/// Нормализация к <see cref="OptionBoard"/>: markIV (дробь) → проценты (×100); код экспирации
/// <c>YYMMDD</c> → <c>dMMMyy</c>; OI берём из <c>sumOpenInterest</c> (в монетах); спот — индекс.
/// Вызовы на снимок: exchangeInfo (кэш 6ч) + mark (5м) + index (1м) + openInterest (5м, на экспирацию).
/// </summary>
public sealed class BinanceOptionBoardSource : IOptionBoardSource
{
    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;
    private readonly ILogger<BinanceOptionBoardSource> _logger;

    public BinanceOptionBoardSource(IHttpClientFactory factory, IMemoryCache cache, ILogger<BinanceOptionBoardSource> logger)
    {
        _http = factory.CreateClient(BinanceConfig.ClientName);
        _cache = cache;
        _logger = logger;
    }

    public ExchangeSource Exchange => ExchangeSource.Binance;

    public async Task<List<string>> GetExpirationsAsync(string currency)
    {
        string prefix = currency + "-";
        return (await GetSymbolsAsync())
            .Where(s => s.Symbol != null && s.Symbol.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(s => FormatExpiration(s.ExpiryDate))
            .Distinct()
            .OrderBy(e => DateTime.ParseExact(e, "dMMMyy", CultureInfo.InvariantCulture))
            .ToList();
    }

    public async Task<OptionBoard> GetBoardAsync(string currency, string expiration)
    {
        if (!DateTime.TryParseExact(expiration, "dMMMyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return OptionBoard.Empty;

        string prefix = currency + "-";
        List<BinanceSymbol> symbols = (await GetSymbolsAsync())
            .Where(s => s.Symbol != null
                        && s.Symbol.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        && FormatExpiration(s.ExpiryDate) == expiration)
            .ToList();
        if (symbols.Count == 0)
            return OptionBoard.Empty;

        double spot = await GetIndexAsync(currency);
        if (spot <= 0)
            return OptionBoard.Empty;

        Dictionary<string, BinanceMark> mark = await GetMarkAsync();
        Dictionary<string, double> oi = await GetOpenInterestAsync(currency, expiration);

        List<OptionData> chain = symbols
            .Select(s => new
            {
                s.Symbol,
                Strike = ParseDouble(s.StrikePrice),
                IsCall = string.Equals(s.Side, "CALL", StringComparison.OrdinalIgnoreCase)
            })
            .GroupBy(x => x.Strike)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var call = g.FirstOrDefault(x => x.IsCall);
                var put = g.FirstOrDefault(x => !x.IsCall);

                mark.TryGetValue(call?.Symbol ?? string.Empty, out BinanceMark? callMark);
                mark.TryGetValue(put?.Symbol ?? string.Empty, out BinanceMark? putMark);

                double callIv = callMark is null ? 0 : ParseDouble(callMark.MarkIv) * 100.0;
                double putIv = putMark is null ? 0 : ParseDouble(putMark.MarkIv) * 100.0;

                return new OptionData
                {
                    Strike = g.Key,
                    CallOi = call != null && oi.TryGetValue(call.Symbol!, out double co) ? co : 0,
                    PutOi = put != null && oi.TryGetValue(put.Symbol!, out double po) ? po : 0,
                    Iv = callIv > 0 ? callIv : putIv,
                    PutIv = putIv,
                    CallPrice = callMark is null ? 0 : ParseDouble(callMark.MarkPrice),
                    PutPrice = putMark is null ? 0 : ParseDouble(putMark.MarkPrice)
                };
            })
            .ToList();

        return new OptionBoard(chain, spot);
    }

    private async Task<IReadOnlyList<BinanceSymbol>> GetSymbolsAsync() =>
        (await _cache.GetOrCreateAsync("BinanceSymbols", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6);
            BinanceExchangeInfo? info = await _http.GetFromJsonAsync<BinanceExchangeInfo>("eapi/v1/exchangeInfo");
            return (IReadOnlyList<BinanceSymbol>)(info?.OptionSymbols ?? new List<BinanceSymbol>());
        }))!;

    private async Task<Dictionary<string, BinanceMark>> GetMarkAsync() =>
        (await _cache.GetOrCreateAsync("BinanceMark", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            List<BinanceMark>? marks = await _http.GetFromJsonAsync<List<BinanceMark>>("eapi/v1/mark");
            return (marks ?? new List<BinanceMark>())
                .Where(m => !string.IsNullOrEmpty(m.Symbol))
                .GroupBy(m => m.Symbol!)
                .ToDictionary(grp => grp.Key, grp => grp.First());
        }))!;

    private async Task<double> GetIndexAsync(string currency)
    {
        double? value = await _cache.GetOrCreateAsync($"BinanceIndex_{currency}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            BinanceIndex? idx = await _http.GetFromJsonAsync<BinanceIndex>($"eapi/v1/index?underlying={currency}USDT");
            return idx is null ? 0 : ParseDouble(idx.IndexPrice);
        });
        return value ?? 0;
    }

    private async Task<Dictionary<string, double>> GetOpenInterestAsync(string currency, string expiration) =>
        (await _cache.GetOrCreateAsync($"BinanceOI_{currency}_{expiration}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            string code = DateTime.ParseExact(expiration, "dMMMyy", CultureInfo.InvariantCulture).ToString("yyMMdd");
            List<BinanceOpenInterest>? list = await _http.GetFromJsonAsync<List<BinanceOpenInterest>>(
                $"eapi/v1/openInterest?underlyingAsset={currency}&expiration={code}");
            return (list ?? new List<BinanceOpenInterest>())
                .Where(o => !string.IsNullOrEmpty(o.Symbol))
                .GroupBy(o => o.Symbol!)
                .ToDictionary(grp => grp.Key, grp => ParseDouble(grp.First().SumOpenInterest));
        }))!;

    private static double ParseDouble(string? s) =>
        double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double v) ? v : 0;

    /// <summary>Unix-ms экспирации Binance → код <c>dMMMyy</c> (как у Deribit, 08:00 UTC).</summary>
    private static string FormatExpiration(long expiryMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(expiryMs).UtcDateTime
            .ToString("dMMMyy", CultureInfo.InvariantCulture).ToUpperInvariant();

    // --- DTO ответов Binance (числа приходят строками) ---
    private sealed class BinanceExchangeInfo
    {
        [JsonPropertyName("optionSymbols")] public List<BinanceSymbol>? OptionSymbols { get; set; }
    }

    private sealed class BinanceSymbol
    {
        [JsonPropertyName("symbol")] public string? Symbol { get; set; }
        [JsonPropertyName("side")] public string? Side { get; set; }
        [JsonPropertyName("strikePrice")] public string? StrikePrice { get; set; }
        [JsonPropertyName("expiryDate")] public long ExpiryDate { get; set; }
        [JsonPropertyName("underlying")] public string? Underlying { get; set; }
    }

    private sealed class BinanceMark
    {
        [JsonPropertyName("symbol")] public string? Symbol { get; set; }
        [JsonPropertyName("markPrice")] public string? MarkPrice { get; set; }
        [JsonPropertyName("markIV")] public string? MarkIv { get; set; }
    }

    private sealed class BinanceIndex
    {
        [JsonPropertyName("indexPrice")] public string? IndexPrice { get; set; }
    }

    private sealed class BinanceOpenInterest
    {
        [JsonPropertyName("symbol")] public string? Symbol { get; set; }
        [JsonPropertyName("sumOpenInterest")] public string? SumOpenInterest { get; set; }
    }
}
