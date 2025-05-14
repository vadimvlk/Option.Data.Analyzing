using System.Text.Json.Serialization;
using Option.Data.Shared.Dto;

public class Expirations
{
    [JsonPropertyName("jsonrpc")]
    public string Jsonrpc { get; set; }

    [JsonPropertyName("result")]
    public AssetOptions Data { get; set; }

    [JsonPropertyName("testnet")]
    public bool Testnet { get; set; }
    
}