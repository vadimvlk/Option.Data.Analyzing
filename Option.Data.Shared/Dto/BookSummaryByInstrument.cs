using System.Text.Json.Serialization;

namespace Option.Data.Shared.Dto;

public class BookSummaryByInstrument
{
    [JsonPropertyName("jsonrpc")]
    public string? Jsonrpc { get; set; }

    [JsonPropertyName("result")]
    public List<BookSummaryData>? Data { get; set; }

    [JsonPropertyName("usIn")]
    public long UsIn { get; set; }

    [JsonPropertyName("usOut")]
    public long UsOut { get; set; }

    [JsonPropertyName("usDiff")]
    public long UsDiff { get; set; }

    [JsonPropertyName("testnet")]
    public bool Testnet { get; set; }
}