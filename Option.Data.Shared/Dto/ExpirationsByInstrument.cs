using System.Text.Json.Serialization;

namespace Option.Data.Shared.Dto;

public class ExpirationsByInstrument
{
    [JsonPropertyName("jsonrpc")]
    public string Jsonrpc { get; set; }

    [JsonPropertyName("result")]
    public Dictionary<string, AssetOptions> Data { get; set; }

    [JsonPropertyName("testnet")]
    public bool Testnet { get; set; }

    public bool TryGetOptions(string currency, out List<string> options)
    {
        options = null!;
        string? key = Data?.Keys?.FirstOrDefault(k =>
            string.Equals(k, currency, StringComparison.CurrentCultureIgnoreCase));
       
        if (Data == null || key == null) return false;
        options = Data[key].Options!;
        return true;
    }
}

public class AssetOptions
{
    [JsonPropertyName("option")]
    public List<string>? Options { get; set; }
}