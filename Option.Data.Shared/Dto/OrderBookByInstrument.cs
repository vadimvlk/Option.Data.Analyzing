using System.Text.Json.Serialization;

namespace Option.Data.Shared.Dto;

public class OrderBookByInstrument
{
    [JsonPropertyName("result")]
    public OrderBookData? Data { get; set; }
}

public class OrderBookData
{
    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("greeks")]
    public Greeks? Greeks { get; set; }

    [JsonPropertyName("instrument_name")]
    public string? InstrumentName { get; set; }

    [JsonPropertyName("open_interest")]
    public double? OpenInterest { get; set; }

    [JsonPropertyName("mark_price")]
    public double? MarkPrice { get; set; }

    [JsonPropertyName("interest_rate")]
    public double? InterestRate { get; set; }

    [JsonPropertyName("mark_iv")]
    public double? MarkIv { get; set; }

    [JsonPropertyName("underlying_price")]
    public double? UnderlyingPrice { get; set; }

    [JsonPropertyName("estimated_delivery_price")]
    public double? DeliveryPrice { get; set; }
}

public class Greeks
{
    [JsonPropertyName("delta")]
    public double Delta { get; set; }

    [JsonPropertyName("gamma")]
    public double Gamma { get; set; }

    [JsonPropertyName("vega")]
    public double Vega { get; set; }

    [JsonPropertyName("theta")]
    public double Theta { get; set; }

    [JsonPropertyName("rho")]
    public double Rho { get; set; }
}