using System.Text.Json.Serialization;

namespace Option.Data.Shared.Dto;

public class BookSummaryData
{
    [JsonPropertyName("high")]
    public double? High { get; set; }

    [JsonPropertyName("low")]
    public double? Low { get; set; }

    [JsonPropertyName("last")]
    public double? Last { get; set; }

    [JsonPropertyName("instrument_name")]
    public string? InstrumentName { get; set; }

    [JsonPropertyName("bid_price")]
    public double? BidPrice { get; set; }

    [JsonPropertyName("ask_price")]
    public double? AskPrice { get; set; }

    [JsonPropertyName("open_interest")]
    public double? OpenInterest { get; set; }

    [JsonPropertyName("mark_price")]
    public double? MarkPrice { get; set; }

    [JsonPropertyName("creation_timestamp")]
    public double? CreationTimestamp { get; set; }

    [JsonPropertyName("price_change")]
    public double? PriceChange { get; set; }

    [JsonPropertyName("volume")]
    public double? Volume { get; set; }

    [JsonPropertyName("interest_rate")]
    public double? InterestRate { get; set; }

    [JsonPropertyName("mark_iv")]
    public double? MarkIv { get; set; }

    [JsonPropertyName("underlying_price")]
    public double? UnderlyingPrice { get; set; }

    [JsonPropertyName("underlying_index")]
    public string? UnderlyingIndex { get; set; }

    [JsonPropertyName("base_currency")]
    public string? BaseCurrency { get; set; }

    [JsonPropertyName("estimated_delivery_price")]
    public double? EstimatedDeliveryPrice { get; set; }

    [JsonPropertyName("quote_currency")]
    public string? QuoteCurrency { get; set; }

    [JsonPropertyName("volume_usd")]
    public double? VolumeUsd { get; set; }

    [JsonPropertyName("mid_price")]
    public double? MidPrice { get; set; }
}